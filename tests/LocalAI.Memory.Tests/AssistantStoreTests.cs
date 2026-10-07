using LocalAI.Tests;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using Microsoft.Data.Sqlite;

namespace LocalAI.Memory.Tests;

public sealed class AssistantStoreTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Conversations_and_memories_are_isolated_per_assistant()
    {
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var memories = new SqliteMemoryStore(_fx.Database, _fx.Assistant);
        var diana = _fx.Assistant.Current!;
        await conversations.CreateAsync("Diana's chat");
        await memories.AddAsync(new MemoryItem { Content = "User likes tea.", CreatedAt = DateTimeOffset.Now });

        _fx.Assistant.Set(_fx.CreateAssistant("Marco"));
        Assert.Empty(await conversations.ListAsync());
        Assert.Empty(await memories.ListAsync());
        Assert.Empty(await conversations.SearchAsync("chat"));
        await conversations.CreateAsync("Marco's chat");

        _fx.Assistant.Set(diana);
        Assert.Equal("Diana's chat", Assert.Single(await conversations.ListAsync()).Title);
        Assert.Equal("User likes tea.", Assert.Single(await memories.ListAsync()).Content);
    }

    [Fact]
    public async Task Shortcut_is_saved_and_cleared()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        var diana = _fx.Assistant.Current!;
        Assert.Null(diana.Hotkey);
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+D", out var gesture));

        await store.SetHotkeyAsync(diana.Id, gesture);
        Assert.Equal(gesture, (await store.ListAsync()).Single(a => a.Id == diana.Id).Hotkey);

        await store.SetHotkeyAsync(diana.Id, null);
        Assert.Null((await store.ListAsync()).Single(a => a.Id == diana.Id).Hotkey);
    }

    [Fact]
    public async Task Before_any_assistant_exists_there_is_nothing_to_list()
    {
        var none = new AssistantContext();
        Assert.Empty(await new SqliteMemoryStore(_fx.Database, none).ListAsync());
        Assert.Empty(await new SqliteConversationStore(_fx.Database, none).ListAsync());
        Assert.Empty(await new SqliteConversationStore(_fx.Database, none).SearchAsync("x"));
    }

    [Fact]
    public async Task Memory_from_a_conversation_belongs_to_that_conversations_assistant()
    {
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var memories = new SqliteMemoryStore(_fx.Database, _fx.Assistant);
        var diana = _fx.Assistant.Current!;
        var chat = await conversations.CreateAsync("chat");

        _fx.Assistant.Set(_fx.CreateAssistant("Marco"));
        await memories.AddAsync(new MemoryItem { Content = "late fact", CreatedAt = DateTimeOffset.Now, SourceConversationId = chat.Id });
        Assert.Empty(await memories.ListAsync());

        _fx.Assistant.Set(diana);
        Assert.Single(await memories.ListAsync());
    }

    [Fact]
    public async Task Deleting_an_assistant_deletes_its_data_only()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var memories = new SqliteMemoryStore(_fx.Database, _fx.Assistant);
        var diana = _fx.Assistant.Current!;
        await conversations.CreateAsync("keep");

        var marco = _fx.CreateAssistant("Marco");
        _fx.Assistant.Set(marco);
        var doomed = await conversations.CreateAsync("doomed");
        foreach (var role in new[] { ChatRole.User, ChatRole.Assistant })
            await conversations.AddMessageAsync(new StoredMessage
            {
                ConversationId = doomed.Id, Role = role, Content = "hi", CreatedAt = DateTimeOffset.Now, Source = InputSource.Text,
            });
        await memories.AddAsync(new MemoryItem { Content = "doomed fact", CreatedAt = DateTimeOffset.Now });
        Assert.Equal(new AssistantDataSummary(1, 2, 1), await store.GetDataSummaryAsync(marco.Id));

        await store.DeleteAsync(marco.Id);
        Assert.Equal(new AssistantDataSummary(0, 0, 0), await store.GetDataSummaryAsync(marco.Id));
        Assert.Equal(new AssistantDataSummary(1, 0, 0), await store.GetDataSummaryAsync(diana.Id));
        Assert.Null(await conversations.GetAsync(doomed.Id));
        Assert.Empty(await memories.ListAsync());
        Assert.Equal([diana.Id], (await store.ListAsync()).Select(a => a.Id));
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_model_voice_and_style_round_trip()
    {
        var store = new SqliteAssistantStore(_fx.Database);
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(SqliteFixture.Draft("DIANA"), VoiceStyle.Default));

        var style = new VoiceStyle { Speed = 1.2, Pitch = -2, Expressiveness = 0.4, Rhythm = 1.0 };
        await store.SetVoiceStyleAsync(_fx.Assistant.CurrentId, style);
        var saved = Assert.Single(await new SqliteAssistantStore(_fx.Reopen()).ListAsync());
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
                        VALUES ('Diana', 'Calm.', '{"en":"en_US-lessac-medium","pt":"pt_BR-cadu-medium"}', 0);
                    PRAGMA user_version = 2;
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var db = new SqliteDatabase(path, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance);
            var diana = Assert.Single(await new SqliteAssistantStore(db).ListAsync());
            Assert.Equal(("Diana", "Calm.", "pt_BR-cadu-medium", "pt", ""), (diana.Name, diana.StylePrompt, diana.VoiceId, diana.Language, diana.ModelId));
            Assert.Equal(VoiceStyle.Default, diana.VoiceStyle);
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
            context.Set(await new SqliteAssistantStore(db).CreateAsync(SqliteFixture.Draft("Diana"), VoiceStyle.Default));

            Assert.Equal("old chat", Assert.Single(await new SqliteConversationStore(db, context).ListAsync()).Title);
            Assert.Equal("old fact", Assert.Single(await new SqliteMemoryStore(db, context).ListAsync()).Content);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
