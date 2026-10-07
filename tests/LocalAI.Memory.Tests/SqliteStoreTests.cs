using LocalAI.Tests;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Memory.Tests;

public sealed class SqliteFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TestPaths.New("sqlite"), "localai.db");
    public SqliteDatabase Database { get; }

    public AssistantContext Assistant { get; } = new();

    public SqliteFixture()
    {
        Database = new SqliteDatabase(Path, NullLogger<SqliteDatabase>.Instance);
        Assistant.Set(CreateAssistant("Diana"));
    }

    public AssistantProfile CreateAssistant(string name) =>
        new SqliteAssistantStore(Database).CreateAsync(Draft(name), LocalAI.Core.Speech.VoiceStyle.Default).GetAwaiter().GetResult();

    public static NewAssistant Draft(string name) => new(name, "", "model", "en_US-lessac-medium", "en");

    public SqliteDatabase Reopen() => new(Path, NullLogger<SqliteDatabase>.Instance);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public sealed class SqliteConversationStoreTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    private readonly SqliteConversationStore _store;

    public SqliteConversationStoreTests() => _store = new SqliteConversationStore(_fx.Database, _fx.Assistant);

    public void Dispose() => _fx.Dispose();

    private Task<StoredMessage> Add(long conversationId, ChatRole role, string text, string? lang = null, DateTimeOffset? at = null) =>
        _store.AddMessageAsync(new StoredMessage
        {
            ConversationId = conversationId, Role = role, Content = text, CreatedAt = at ?? DateTimeOffset.Now,
            Language = lang, Source = InputSource.Text, Model = role == ChatRole.Assistant ? "m" : null,
        });

    [Fact]
    public async Task Creates_lists_renames_and_deletes_conversations()
    {
        var a = await _store.CreateAsync("First");
        var b = await _store.CreateAsync("Second");
        // a becomes most recent. An explicit later time: on a fast machine all three writes share one millisecond.
        await Add(a.Id, ChatRole.User, "bump", at: DateTimeOffset.Now.AddSeconds(1));

        var list = await _store.ListAsync();
        Assert.Equal([a.Id, b.Id], list.Select(c => c.Id));

        await _store.RenameAsync(b.Id, "Renamed");
        Assert.Equal("Renamed", (await _store.GetAsync(b.Id))!.Title);

        await _store.DeleteAsync(a.Id);
        Assert.Null(await _store.GetAsync(a.Id));
        Assert.Empty(await _store.GetMessagesAsync(a.Id)); // cascade
    }

    [Fact]
    public async Task Persists_messages_with_metadata_across_reopen()
    {
        var c = await _store.CreateAsync("Chat");
        await Add(c.Id, ChatRole.User, "Hi, how are you?", "en");
        await _store.AddMessageAsync(new StoredMessage
        {
            ConversationId = c.Id, Role = ChatRole.Assistant, Content = "All good!", CreatedAt = DateTimeOffset.Now,
            Language = "en", Model = "gemma", Source = InputSource.Voice, MetadataJson = "{\"interrupted\":false}",
        });

        var reopened = new SqliteConversationStore(_fx.Reopen(), _fx.Assistant);
        var messages = await reopened.GetMessagesAsync(c.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Hi, how are you?", messages[0].Content);
        Assert.Equal("en", messages[0].Language);
        Assert.Equal(InputSource.Voice, messages[1].Source);
        Assert.Equal("gemma", messages[1].Model);
        Assert.Equal("{\"interrupted\":false}", messages[1].MetadataJson);
        Assert.True(messages[0].Id < messages[1].Id);
    }

    [Fact]
    public async Task Recent_messages_are_the_newest_in_chronological_order()
    {
        var c = await _store.CreateAsync("Chat");
        for (var i = 0; i < 10; i++) await Add(c.Id, i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}");
        var recent = await _store.GetRecentMessagesAsync(c.Id, 3);
        Assert.Equal(["m7", "m8", "m9"], recent.Select(m => m.Content));
    }

    [Fact]
    public async Task Search_is_case_and_accent_insensitive_with_snippets()
    {
        var pt = await _store.CreateAsync("Programação");
        await Add(pt.Id, ChatRole.User, "Como funciona a memória do computador?");
        var other = await _store.CreateAsync("Cooking");
        await Add(other.Id, ChatRole.User, "How do I make carbonara?");

        var hits = await _store.SearchAsync("MEMORIA");
        var hit = Assert.Single(hits);
        Assert.Equal(pt.Id, hit.Conversation.Id);
        Assert.Contains("memória", hit.Snippet, StringComparison.Ordinal);

        Assert.Single(await _store.SearchAsync("programacao")); // title match, accent-insensitive
        Assert.Empty(await _store.SearchAsync("nonexistent"));
        Assert.Empty(await _store.SearchAsync("   "));
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        await _store.CreateAsync("x");
        var again = new SqliteConversationStore(_fx.Reopen(), _fx.Assistant);
        Assert.Single(await again.ListAsync());
    }
}

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Stores_memories_with_embeddings()
    {
        var store = new SqliteMemoryStore(_fx.Database, _fx.Assistant);
        var vector = new[] { 0.1f, -0.5f, 3.25f };
        var a = await store.AddAsync(new MemoryItem { Content = "User prefers C#.", CreatedAt = DateTimeOffset.Now, Embedding = vector, EmbeddingModel = "e", SourceConversationId = 7 });
        var b = await store.AddAsync(new MemoryItem { Content = "User likes coffee.", CreatedAt = DateTimeOffset.Now });

        var list = await new SqliteMemoryStore(_fx.Reopen(), _fx.Assistant).ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(vector, list[0].Embedding!);
        Assert.Equal(7, list[0].SourceConversationId);
        Assert.Null(list[1].Embedding);

        await store.UpdateEmbeddingAsync(b.Id, [1f, 2f], "e2");
        await store.DeleteAsync(a.Id);
        var after = Assert.Single(await store.ListAsync());
        Assert.Equal([1f, 2f], after.Embedding!);
        Assert.Equal("e2", after.EmbeddingModel);
    }

    [Fact]
    public async Task Memories_survive_conversation_deletion()
    {
        var conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
        var memories = new SqliteMemoryStore(_fx.Database, _fx.Assistant);
        var c = await conversations.CreateAsync("chat");
        await memories.AddAsync(new MemoryItem { Content = "fact", CreatedAt = DateTimeOffset.Now, SourceConversationId = c.Id });
        await conversations.DeleteAsync(c.Id);
        Assert.Single(await memories.ListAsync());
    }
}

public sealed class MemoryRetrieverTests
{
    private sealed class ListStore(params MemoryItem[] items) : IMemoryStore
    {
        public Task<MemoryItem> AddAsync(MemoryItem item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryItem>>(items);
        public Task DeleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateEmbeddingAsync(long id, float[] embedding, string model, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AxisEmbeddings(bool available = true) : IEmbeddingService
    {
        public bool IsAvailable => available;
        public string ModelId => "axis";
        public Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default) =>
            Task.FromResult(text.Contains("language", StringComparison.OrdinalIgnoreCase) ? new[] { 1f, 0.1f } : new[] { 0f, 1f });
    }

    [Fact]
    public async Task Ranks_by_cosine_and_applies_threshold()
    {
        var store = new ListStore(
            new MemoryItem { Id = 1, Content = "User prefers C#.", Embedding = [1f, 0f], EmbeddingModel = "axis" },
            new MemoryItem { Id = 2, Content = "User likes coffee.", Embedding = [0f, 1f], EmbeddingModel = "axis" },
            new MemoryItem { Id = 3, Content = "Old model vector", Embedding = [1f, 0f], EmbeddingModel = "other" });
        var retriever = new MemoryRetriever(store, new AxisEmbeddings(), NullLogger<MemoryRetriever>.Instance);

        var results = await retriever.RetrieveAsync("Which language should I use?", 5, 0.5f);
        var top = Assert.Single(results); // coffee below threshold, foreign-model vector ignored
        Assert.Equal(1, top.Memory.Id);
        Assert.True(top.Score > 0.9f);
    }

    [Fact]
    public async Task Falls_back_to_keywords_without_embeddings()
    {
        var store = new ListStore(
            new MemoryItem { Id = 1, Content = "User prefers writing software in C#." },
            new MemoryItem { Id = 2, Content = "User likes coffee." });
        var retriever = new MemoryRetriever(store, new AxisEmbeddings(available: false), NullLogger<MemoryRetriever>.Instance);
        var results = await retriever.RetrieveAsync("what software should I write", 5, 0.9f);
        Assert.Equal(1, Assert.Single(results).Memory.Id);
    }

    [Fact]
    public void Cosine_similarity_basics()
    {
        Assert.Equal(1f, VectorMath.Cosine([1f, 2f], [2f, 4f]), 4);
        Assert.Equal(0f, VectorMath.Cosine([1f, 0f], [0f, 1f]), 4);
        Assert.Equal(0f, VectorMath.Cosine([1f], [1f, 2f]));
        Assert.Equal(0f, VectorMath.Cosine([0f, 0f], [1f, 1f]));
    }
}
