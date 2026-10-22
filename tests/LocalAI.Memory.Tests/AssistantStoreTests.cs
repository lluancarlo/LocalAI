using LocalAI.Tests;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using Microsoft.Data.Sqlite;

namespace LocalAI.Memory.Tests;

public sealed class AssistantStoreTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    /// <summary>A fact of the memory used before notes, as the old store saved it.</summary>
    private async Task AddLegacyFactAsync(long assistantId, string content)
    {
        await using var c = await _fx.Database.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO memories(content, created_at, assistant_id) VALUES ($c, 0, $a);";
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$a", assistantId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Conversations_and_old_memories_are_isolated_per_assistant()
    {
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var index = new SqliteVaultIndex(_fx.Database);
        var nova = _fx.Assistant.Current!;
        await conversations.CreateAsync("Nova's chat");
        await AddLegacyFactAsync(nova.Id, "User likes tea.");

        var marco = _fx.CreateAssistant("Marco");
        _fx.Assistant.Set(marco);
        Assert.Empty(await conversations.ListAsync());
        Assert.Empty(await index.TakeLegacyFactsAsync(marco.Id));
        Assert.Empty(await conversations.SearchAsync("chat"));
        await conversations.CreateAsync("Marco's chat");

        _fx.Assistant.Set(nova);
        Assert.Equal("Nova's chat", Assert.Single(await conversations.ListAsync()).Title);
        Assert.Equal("User likes tea.", Assert.Single(await index.TakeLegacyFactsAsync(nova.Id)));
        Assert.Empty(await index.TakeLegacyFactsAsync(nova.Id)); // taken once
    }

    [Fact]
    public async Task Shortcut_is_saved_and_cleared()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        var nova = _fx.Assistant.Current!;
        Assert.Null(nova.Hotkey);
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+D", out var gesture));

        await store.SetHotkeyAsync(nova.Id, gesture);
        Assert.Equal(gesture, (await store.ListAsync()).Single(a => a.Id == nova.Id).Hotkey);

        await store.SetHotkeyAsync(nova.Id, null);
        Assert.Null((await store.ListAsync()).Single(a => a.Id == nova.Id).Hotkey);
    }

    [Fact]
    public async Task Before_any_assistant_exists_there_is_nothing_to_list()
    {
        var none = new AssistantContext();
        Assert.Empty(await new SqliteConversationStore(_fx.Database, none).ListAsync());
        Assert.Empty(await new SqliteConversationStore(_fx.Database, none).SearchAsync("x"));
    }

    [Fact]
    public async Task Deleting_an_assistant_deletes_its_data_only()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var index = new SqliteVaultIndex(_fx.Database);
        var nova = _fx.Assistant.Current!;
        await conversations.CreateAsync("keep");

        var marco = _fx.CreateAssistant("Marco");
        _fx.Assistant.Set(marco);
        var doomed = await conversations.CreateAsync("doomed");
        foreach (var role in new[] { ChatRole.User, ChatRole.Assistant })
            await conversations.AddMessageAsync(new StoredMessage
            {
                ConversationId = doomed.Id, Role = role, Content = "hi", CreatedAt = DateTimeOffset.Now, Source = InputSource.Text,
            });
        await index.SaveNoteAsync(new VaultNote { AssistantId = marco.Id, Path = "marco/marco-profile.md", Kind = NoteKind.Profile });
        await index.EnqueueAsync(MemoryJobKind.ConsolidateProfile, marco.Id, null, null, DateTimeOffset.Now);
        Assert.Equal(new AssistantDataSummary(1, 2, 1), await store.GetDataSummaryAsync(marco.Id));

        await store.DeleteAsync(marco.Id);
        Assert.Equal(new AssistantDataSummary(0, 0, 0), await store.GetDataSummaryAsync(marco.Id));
        Assert.Equal(new AssistantDataSummary(1, 0, 0), await store.GetDataSummaryAsync(nova.Id));
        Assert.Null(await conversations.GetAsync(doomed.Id));
        Assert.Empty(await index.ListNotesAsync(marco.Id));
        Assert.Null(await index.NextJobAsync(DateTimeOffset.Now.AddDays(1))); // its queued work goes too
        Assert.Equal([nova.Id], (await store.ListAsync()).Select(a => a.Id));
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_model_voice_and_style_round_trip()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(SqliteFixture.Draft("NOVA"), VoiceStyle.Default));

        var style = new VoiceStyle { Speed = 1.2, Pitch = -2, Expressiveness = 0.4, Rhythm = 1.0 };
        var created = await store.CreateAsync(SqliteFixture.Draft("Ava"), style);
        var saved = Assert.Single(await new SqliteAssistantStore(_fx.Reopen()).ListAsync(), a => a.Id == created.Id);
        Assert.Equal(("model", "en_US-lessac-medium", "en"), (saved.ModelId, saved.VoiceId, saved.Language));
        Assert.Equal(style, saved.VoiceStyle);
    }

    [Fact]
    public async Task Upgrading_keeps_existing_assistants_with_their_portuguese_voice()
    {
        var path = Path.Combine(TestPaths.New("v2"), "localai.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var c = new SqliteConnection($"Data Source={path}"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE conversations (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                    CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                        role TEXT NOT NULL, content TEXT NOT NULL, created_at INTEGER NOT NULL, language TEXT, model TEXT, source TEXT NOT NULL, metadata TEXT);
                    CREATE TABLE memories (id INTEGER PRIMARY KEY AUTOINCREMENT, content TEXT NOT NULL, created_at INTEGER NOT NULL,
                        source_conversation_id INTEGER, embedding BLOB, embedding_model TEXT);
                    CREATE TABLE assistants (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        style_prompt TEXT NOT NULL, voices TEXT NOT NULL, created_at INTEGER NOT NULL);
                    ALTER TABLE conversations ADD COLUMN assistant_id INTEGER REFERENCES assistants(id) ON DELETE CASCADE;
                    ALTER TABLE memories ADD COLUMN assistant_id INTEGER REFERENCES assistants(id) ON DELETE CASCADE;
                    INSERT INTO assistants(name, style_prompt, voices, created_at)
                        VALUES ('Nova', 'Calm.', '{"en":"en_US-lessac-medium","pt":"pt_BR-cadu-medium"}', 0);
                    PRAGMA user_version = 2;
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var db = new SqliteDatabase(path, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance);
            var nova = Assert.Single(await new SqliteAssistantStore(db).ListAsync());
            Assert.Equal(("Nova", "Calm.", "pt_BR-cadu-medium", "pt", ""), (nova.Name, nova.StylePrompt, nova.VoiceId, nova.Language, nova.ModelId));
            Assert.Equal(VoiceStyle.Default, nova.VoiceStyle);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task First_assistant_adopts_data_saved_before_assistants_existed()
    {
        var path = Path.Combine(TestPaths.New("legacy"), "localai.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var c = new SqliteConnection($"Data Source={path}"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE conversations (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                    CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                        role TEXT NOT NULL, content TEXT NOT NULL, created_at INTEGER NOT NULL, language TEXT, model TEXT, source TEXT NOT NULL, metadata TEXT);
                    CREATE INDEX ix_messages_conversation ON messages(conversation_id, id);
                    CREATE INDEX ix_conversations_updated ON conversations(updated_at DESC);
                    CREATE TABLE memories (id INTEGER PRIMARY KEY AUTOINCREMENT, content TEXT NOT NULL, created_at INTEGER NOT NULL,
                        source_conversation_id INTEGER, embedding BLOB, embedding_model TEXT);
                    INSERT INTO conversations(title, created_at, updated_at) VALUES ('old chat', 0, 0);
                    INSERT INTO memories(content, created_at) VALUES ('old fact', 0);
                    PRAGMA user_version = 1;
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var db = new SqliteDatabase(path, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance);
            var context = new AssistantContext();
            context.Set(await new SqliteAssistantStore(db).CreateAsync(SqliteFixture.Draft("Nova"), VoiceStyle.Default));

            Assert.Equal("old chat", Assert.Single(await new SqliteConversationStore(db, context).ListAsync()).Title);
            Assert.Equal("old fact", Assert.Single(await new SqliteVaultIndex(db).TakeLegacyFactsAsync(context.CurrentId)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
