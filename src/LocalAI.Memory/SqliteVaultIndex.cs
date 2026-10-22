using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LocalAI.Core.Memory.Vault;
using Microsoft.Data.Sqlite;
using static LocalAI.Memory.SqliteDatabase;

namespace LocalAI.Memory;

/// <summary>
/// The index of the memory notes in SQLite: notes, their sections (embeddings as float32 BLOBs, full-text search with
/// FTS5), the queue of background memory work and small app state. Everything here can be rebuilt from the note files.
/// </summary>
public sealed partial class SqliteVaultIndex(SqliteDatabase db) : IVaultIndex
{
    [GeneratedRegex(@"[\p{L}\p{N}_]{3,}")] private static partial Regex Word();

    private const string NoteColumns = "id, assistant_id, path, kind, conversation_id, generated, file_hash, source_updated_at, updated_at";

    // ---------------- Notes ----------------

    public Task<VaultNote?> FindNoteAsync(long assistantId, string path, CancellationToken ct = default) =>
        QueryNoteAsync($"SELECT {NoteColumns} FROM vault_notes WHERE assistant_id = $a AND path = $p;", ct,
            ("$a", assistantId), ("$p", path));

    public Task<VaultNote?> FindConversationNoteAsync(long conversationId, CancellationToken ct = default) =>
        QueryNoteAsync($"SELECT {NoteColumns} FROM vault_notes WHERE conversation_id = $c ORDER BY id LIMIT 1;", ct,
            ("$c", conversationId));

    public async Task<IReadOnlyList<VaultNote>> ListNotesAsync(long assistantId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {NoteColumns} FROM vault_notes WHERE assistant_id = $a ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", assistantId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<VaultNote>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadNote(r));
        return list;
    }

    public async Task<VaultNote> SaveNoteAsync(VaultNote note, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = note.Id == 0
            ? """
              INSERT INTO vault_notes(assistant_id, path, kind, conversation_id, generated, file_hash, source_updated_at, updated_at)
              VALUES ($a, $p, $k, $c, $g, $h, $s, $u)
              ON CONFLICT(assistant_id, path) DO UPDATE SET kind = $k, conversation_id = $c, generated = $g,
                  file_hash = $h, source_updated_at = $s, updated_at = $u
              RETURNING id;
              """
            : """
              UPDATE vault_notes SET path = $p, kind = $k, conversation_id = $c, generated = $g, file_hash = $h,
                  source_updated_at = $s, updated_at = $u
              WHERE id = $id RETURNING id;
              """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$a", note.AssistantId);
        cmd.Parameters.AddWithValue("$p", note.Path);
        cmd.Parameters.AddWithValue("$k", note.Kind.ToString());
        cmd.Parameters.AddWithValue("$c", (object?)note.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)note.GeneratedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", note.FileHash);
        cmd.Parameters.AddWithValue("$s", note.SourceUpdatedAt is { } s ? ToUnixMs(s) : DBNull.Value);
        cmd.Parameters.AddWithValue("$u", ToUnixMs(note.UpdatedAt == default ? DateTimeOffset.UnixEpoch : note.UpdatedAt));
        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return note with { Id = id is long value ? value : note.Id };
    }

    public async Task DeleteNoteAsync(long noteId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vault_notes WHERE id = $id;"; // sections cascade
        cmd.Parameters.AddWithValue("$id", noteId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<VaultNote?> QueryNoteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadNote(r) : null;
    }

    private static VaultNote ReadNote(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        AssistantId = r.GetInt64(1),
        Path = r.GetString(2),
        Kind = Enum.Parse<NoteKind>(r.GetString(3)),
        ConversationId = r.IsDBNull(4) ? null : r.GetInt64(4),
        GeneratedText = r.IsDBNull(5) ? null : r.GetString(5),
        FileHash = r.GetString(6),
        SourceUpdatedAt = r.IsDBNull(7) ? null : FromUnixMs(r.GetInt64(7)),
        UpdatedAt = FromUnixMs(r.GetInt64(8)),
    };

    // ---------------- Sections ----------------

    public async Task ReplaceChunksAsync(long noteId, IReadOnlyList<NoteChunk> chunks, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var delete = c.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM note_chunks WHERE note_id = $n;";
            delete.Parameters.AddWithValue("$n", noteId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var chunk in chunks)
        {
            await using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO note_chunks(note_id, heading, text, embedding, embedding_model) VALUES ($n, $h, $t, $e, $m);";
            insert.Parameters.AddWithValue("$n", noteId);
            insert.Parameters.AddWithValue("$h", chunk.Heading);
            insert.Parameters.AddWithValue("$t", chunk.Text);
            insert.Parameters.AddWithValue("$e", chunk.Embedding is { } e ? MemoryMarshal.AsBytes(e.AsSpan()).ToArray() : DBNull.Value);
            insert.Parameters.AddWithValue("$m", (object?)chunk.EmbeddingModel ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NoteChunk>> ListChunksAsync(long assistantId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT k.id, k.note_id, n.path, k.heading, k.text, k.embedding, k.embedding_model
            FROM note_chunks k JOIN vault_notes n ON n.id = k.note_id
            WHERE n.assistant_id = $a ORDER BY k.id;
            """;
        cmd.Parameters.AddWithValue("$a", assistantId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<NoteChunk>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new NoteChunk
            {
                Id = r.GetInt64(0),
                NoteId = r.GetInt64(1),
                NoteTitle = Path.GetFileNameWithoutExtension(r.GetString(2)),
                Heading = r.GetString(3),
                Text = r.GetString(4),
                Embedding = r.IsDBNull(5) ? null : MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(5)).ToArray(),
                EmbeddingModel = r.IsDBNull(6) ? null : r.GetString(6),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<long>> SearchChunksAsync(long assistantId, string query, int limit, CancellationToken ct = default)
    {
        // Each word as a quoted term, any of them may match: user text never reaches FTS5 syntax.
        var terms = Word().Matches(query).Select(m => "\"" + m.Value + "\"").Distinct().Take(20).ToList();
        if (terms.Count == 0 || limit <= 0) return [];
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT k.id FROM note_chunks_fts f
            JOIN note_chunks k ON k.id = f.rowid
            JOIN vault_notes n ON n.id = k.note_id
            WHERE note_chunks_fts MATCH $q AND n.assistant_id = $a
            ORDER BY bm25(note_chunks_fts) LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", terms));
        cmd.Parameters.AddWithValue("$a", assistantId);
        cmd.Parameters.AddWithValue("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new List<long>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) ids.Add(r.GetInt64(0));
        return ids;
    }

    // ---------------- Conversations and the old memory ----------------

    public async Task<IReadOnlyList<(long ConversationId, long AssistantId)>> ConversationsNeedingNotesAsync(
        DateTimeOffset since, DateTimeOffset idleBefore, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT cv.id, cv.assistant_id FROM conversations cv
            LEFT JOIN vault_notes n ON n.conversation_id = cv.id
            WHERE cv.assistant_id IS NOT NULL AND cv.created_at >= $since AND cv.updated_at <= $idle
              AND EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = cv.id AND m.role = 'Assistant')
              AND (n.id IS NULL OR n.source_updated_at IS NULL OR n.source_updated_at < cv.updated_at)
            ORDER BY cv.updated_at;
            """;
        cmd.Parameters.AddWithValue("$since", ToUnixMs(since));
        cmd.Parameters.AddWithValue("$idle", ToUnixMs(idleBefore));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<(long, long)>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add((r.GetInt64(0), r.GetInt64(1)));
        return list;
    }

    public async Task<IReadOnlyList<string>> TakeLegacyFactsAsync(long assistantId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var facts = new List<string>();
        await using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT content FROM memories WHERE assistant_id = $a ORDER BY id;";
            select.Parameters.AddWithValue("$a", assistantId);
            await using var r = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) facts.Add(r.GetString(0));
        }
        await using (var delete = c.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM memories WHERE assistant_id = $a;";
            delete.Parameters.AddWithValue("$a", assistantId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return facts;
    }

    // ---------------- Job queue ----------------

    public async Task EnqueueAsync(MemoryJobKind kind, long assistantId, long? conversationId, string? path, DateTimeOffset notBefore,
        CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // The same job already waiting starts at the earlier of the two times.
        cmd.CommandText = """
            UPDATE memory_jobs SET not_before = MIN(not_before, $nb)
            WHERE kind = $k AND assistant_id = $a AND conversation_id IS $c AND path IS $p;
            INSERT INTO memory_jobs(kind, assistant_id, conversation_id, path, not_before)
            SELECT $k, $a, $c, $p, $nb
            WHERE NOT EXISTS (SELECT 1 FROM memory_jobs WHERE kind = $k AND assistant_id = $a AND conversation_id IS $c AND path IS $p);
            """;
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$a", assistantId);
        cmd.Parameters.AddWithValue("$c", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nb", ToUnixMs(notBefore));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<MemoryJob?> NextJobAsync(DateTimeOffset now, CancellationToken ct = default, long? assistantId = null)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, assistant_id, conversation_id, path, attempts FROM memory_jobs
            WHERE not_before <= $now AND ($a IS NULL OR assistant_id = $a) ORDER BY not_before, id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
        cmd.Parameters.AddWithValue("$a", (object?)assistantId ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new MemoryJob(r.GetInt64(0), Enum.Parse<MemoryJobKind>(r.GetString(1)), r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5));
    }

    public async Task CompleteJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_jobs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", jobId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task PostponeJobAsync(long jobId, DateTimeOffset notBefore, bool failed, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE memory_jobs SET not_before = $nb, attempts = attempts + $f WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$nb", ToUnixMs(notBefore));
        cmd.Parameters.AddWithValue("$f", failed ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------------- State ----------------

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_state WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_state(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
