using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalAI.Memory;

/// <summary>Local SQLite database (WAL mode) with forward-only schema migrations.</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly Lazy<Task> _initialized;

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE conversations (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            title       TEXT    NOT NULL,
            created_at  INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL
        );
        CREATE TABLE messages (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
            role            TEXT    NOT NULL,
            content         TEXT    NOT NULL,
            created_at      INTEGER NOT NULL,
            language        TEXT,
            model           TEXT,
            source          TEXT    NOT NULL,
            metadata        TEXT
        );
        CREATE INDEX ix_messages_conversation ON messages(conversation_id, id);
        CREATE INDEX ix_conversations_updated ON conversations(updated_at DESC);
        CREATE TABLE memories (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            content                TEXT    NOT NULL,
            created_at             INTEGER NOT NULL,
            source_conversation_id INTEGER,
            embedding              BLOB,
            embedding_model        TEXT
        );
        """,
        // v2: assistants own their conversations and memories
        """
        CREATE TABLE assistants (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            style_prompt  TEXT    NOT NULL,
            voices        TEXT    NOT NULL,
            created_at    INTEGER NOT NULL
        );
        ALTER TABLE conversations ADD COLUMN assistant_id INTEGER REFERENCES assistants(id) ON DELETE CASCADE;
        ALTER TABLE memories ADD COLUMN assistant_id INTEGER REFERENCES assistants(id) ON DELETE CASCADE;
        CREATE INDEX ix_conversations_assistant ON conversations(assistant_id, updated_at DESC);
        CREATE INDEX ix_memories_assistant ON memories(assistant_id);
        """,
        // v3: each assistant has one language model and one voice (in one language) with a tunable style
        """
        ALTER TABLE assistants ADD COLUMN model_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE assistants ADD COLUMN voice_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE assistants ADD COLUMN language TEXT NOT NULL DEFAULT '';
        ALTER TABLE assistants ADD COLUMN voice_style TEXT NOT NULL DEFAULT '';
        UPDATE assistants SET
            voice_id = COALESCE(json_extract(voices, '$.pt'), json_extract(voices, '$.en'), json_extract(voices, '$.it'), ''),
            language = CASE
                WHEN json_extract(voices, '$.pt') IS NOT NULL THEN 'pt'
                WHEN json_extract(voices, '$.en') IS NOT NULL THEN 'en'
                WHEN json_extract(voices, '$.it') IS NOT NULL THEN 'it'
                ELSE '' END;
        ALTER TABLE assistants DROP COLUMN voices;
        """,
        // v4: optional global shortcut that turns a live voice conversation with the assistant on and off
        """
        ALTER TABLE assistants ADD COLUMN hotkey TEXT NOT NULL DEFAULT '';
        """,
        // v5: text the user selected in another application and sent with a message (Read selection)
        """
        ALTER TABLE messages ADD COLUMN selected_text TEXT;
        """,
        // v6: memory notes (Markdown files are the truth; this is their index), their sections with embeddings and
        // full-text search, the queue of background memory work, and small app state
        """
        CREATE TABLE vault_notes (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            assistant_id      INTEGER NOT NULL REFERENCES assistants(id) ON DELETE CASCADE,
            path              TEXT    NOT NULL,
            kind              TEXT    NOT NULL,
            conversation_id   INTEGER,
            generated         TEXT,
            file_hash         TEXT    NOT NULL DEFAULT '',
            source_updated_at INTEGER,
            updated_at        INTEGER NOT NULL,
            UNIQUE (assistant_id, path)
        );
        CREATE INDEX ix_vault_notes_conversation ON vault_notes(conversation_id);
        CREATE TABLE note_chunks (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            note_id         INTEGER NOT NULL REFERENCES vault_notes(id) ON DELETE CASCADE,
            heading         TEXT    NOT NULL,
            text            TEXT    NOT NULL,
            embedding       BLOB,
            embedding_model TEXT
        );
        CREATE INDEX ix_note_chunks_note ON note_chunks(note_id);
        CREATE VIRTUAL TABLE note_chunks_fts USING fts5(
            heading, text, content='note_chunks', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER note_chunks_ai AFTER INSERT ON note_chunks BEGIN
            INSERT INTO note_chunks_fts(rowid, heading, text) VALUES (new.id, new.heading, new.text);
        END;
        CREATE TRIGGER note_chunks_ad AFTER DELETE ON note_chunks BEGIN
            INSERT INTO note_chunks_fts(note_chunks_fts, rowid, heading, text) VALUES ('delete', old.id, old.heading, old.text);
        END;
        CREATE TABLE memory_jobs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            kind            TEXT    NOT NULL,
            assistant_id    INTEGER NOT NULL REFERENCES assistants(id) ON DELETE CASCADE,
            conversation_id INTEGER,
            path            TEXT,
            not_before      INTEGER NOT NULL,
            attempts        INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE app_state (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    public SqliteDatabase(string databasePath, ILogger<SqliteDatabase> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        _initialized = new Lazy<Task>(MigrateAsync);
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        await _initialized.Value.ConfigureAwait(false);
        return await OpenRawAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // Unicode-aware, case/accent-insensitive "contains" for search (SQLite's LIKE only folds ASCII).
        connection.CreateFunction("localai_contains", (string? haystack, string? needle) =>
            haystack != null && needle != null &&
            CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task MigrateAsync()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (dir != null) Directory.CreateDirectory(dir);

        await using var connection = await OpenRawAsync(CancellationToken.None).ConfigureAwait(false);
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);

        for (var v = version; v < Migrations.Length; v++)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v] + $"\nPRAGMA user_version = {v + 1};";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Database migrated to schema v{Version}", v + 1);
        }
    }

    internal static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    internal static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
}
