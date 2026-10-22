using LocalAI.Tests;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
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
        Assistant.Set(CreateAssistant("Nova"));
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
    public async Task Persists_the_text_selected_with_a_message()
    {
        var c = await _store.CreateAsync("Chat");
        await _store.AddMessageAsync(new StoredMessage
        {
            ConversationId = c.Id, Role = ChatRole.User, Content = "What does this mean?", CreatedAt = DateTimeOffset.Now,
            Source = InputSource.Voice, SelectedText = "ON DELETE CASCADE",
        });
        await Add(c.Id, ChatRole.Assistant, "It deletes the children too.");

        var messages = await new SqliteConversationStore(_fx.Reopen(), _fx.Assistant).GetMessagesAsync(c.Id);
        Assert.Equal("ON DELETE CASCADE", messages[0].SelectedText);
        Assert.Null(messages[1].SelectedText);
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

public sealed class SqliteVaultIndexTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    private readonly SqliteVaultIndex _index;
    private readonly SqliteConversationStore _conversations;

    public SqliteVaultIndexTests()
    {
        _index = new SqliteVaultIndex(_fx.Database);
        _conversations = new SqliteConversationStore(_fx.Database, _fx.Assistant);
    }

    public void Dispose() => _fx.Dispose();

    private long Nova => _fx.Assistant.CurrentId;

    private Task<VaultNote> NoteAsync(string path, long? conversationId = null) =>
        _index.SaveNoteAsync(new VaultNote
        {
            AssistantId = Nova, Path = path, Kind = conversationId == null ? NoteKind.Profile : NoteKind.Conversation,
            ConversationId = conversationId, GeneratedText = "## Summary\nx", UpdatedAt = DateTimeOffset.Now,
        });

    [Fact]
    public async Task Notes_are_saved_found_and_updated_in_place()
    {
        var note = await NoteAsync("nova/conversations/2026-10-22-chat.md", conversationId: 5);
        Assert.True(note.Id > 0);

        var again = await _index.SaveNoteAsync(note with { FileHash = "abc", SourceUpdatedAt = DateTimeOffset.Now });
        Assert.Equal(note.Id, again.Id);
        var found = await new SqliteVaultIndex(_fx.Reopen()).FindConversationNoteAsync(5);
        Assert.Equal("abc", found!.FileHash);
        Assert.Equal("## Summary\nx", found.GeneratedText);
        Assert.Equal(note.Id, (await _index.FindNoteAsync(Nova, note.Path))!.Id);
        Assert.Single(await _index.ListNotesAsync(Nova));
    }

    [Fact]
    public async Task Passages_keep_their_vectors_and_are_found_by_words_ignoring_accents()
    {
        var note = await NoteAsync("nova/conversations/2026-10-22-orders-api.md", conversationId: 1);
        await _index.ReplaceChunksAsync(note.Id,
        [
            new NoteChunk { Heading = "Summary", Text = "Paginação por cursor na API de pedidos.", Embedding = [1f, 0f], EmbeddingModel = "e" },
            new NoteChunk { Heading = "Decisions", Text = "Use PostgreSQL for orders." },
        ]);

        var chunks = await _index.ListChunksAsync(Nova);
        Assert.Equal(2, chunks.Count);
        Assert.Equal([1f, 0f], chunks[0].Embedding!);
        Assert.Equal("2026-10-22-orders-api", chunks[0].NoteTitle);

        Assert.Equal([chunks[0].Id], await _index.SearchChunksAsync(Nova, "paginacao", 10));
        Assert.Equal([chunks[1].Id], await _index.SearchChunksAsync(Nova, "which database for ORDERS? postgresql", 10));
        Assert.Empty(await _index.SearchChunksAsync(Nova, "\"; DROP TABLE note_chunks; --", 10)); // user text is never FTS syntax

        await _index.ReplaceChunksAsync(note.Id, [new NoteChunk { Heading = "Summary", Text = "Rewritten." }]);
        Assert.Empty(await _index.SearchChunksAsync(Nova, "postgresql", 10));
        await _index.DeleteNoteAsync(note.Id);
        Assert.Empty(await _index.ListChunksAsync(Nova));
        Assert.Empty(await _index.SearchChunksAsync(Nova, "rewritten", 10));
    }

    [Fact]
    public async Task Passages_of_other_assistants_are_not_found()
    {
        var note = await NoteAsync("nova/nova-profile.md");
        await _index.ReplaceChunksAsync(note.Id, [new NoteChunk { Heading = "About", Text = "Likes espresso." }]);
        var marco = _fx.CreateAssistant("Marco");
        Assert.Empty(await _index.SearchChunksAsync(marco.Id, "espresso", 10));
        Assert.Empty(await _index.ListChunksAsync(marco.Id));
    }

    [Fact]
    public async Task Quiet_new_conversations_with_a_reply_and_no_current_note_need_one()
    {
        var since = DateTimeOffset.Now.AddMinutes(-1);
        var chat = await _conversations.CreateAsync("chat");
        var empty = await _conversations.CreateAsync("only a question");
        foreach (var (conversation, role) in new[] { (chat, ChatRole.User), (chat, ChatRole.Assistant), (empty, ChatRole.User) })
            await _conversations.AddMessageAsync(new StoredMessage
            {
                ConversationId = conversation.Id, Role = role, Content = "x", CreatedAt = DateTimeOffset.Now, Source = InputSource.Text,
            });
        var later = DateTimeOffset.Now.AddMinutes(1);

        Assert.Equal([(chat.Id, Nova)], await _index.ConversationsNeedingNotesAsync(since, later));
        Assert.Empty(await _index.ConversationsNeedingNotesAsync(since, DateTimeOffset.Now.AddMinutes(-5))); // not quiet yet
        Assert.Empty(await _index.ConversationsNeedingNotesAsync(later, later)); // older than the notes

        var note = await NoteAsync("nova/conversations/chat.md", chat.Id);
        await _index.SaveNoteAsync(note with { SourceUpdatedAt = DateTimeOffset.Now.AddMinutes(1) });
        Assert.Empty(await _index.ConversationsNeedingNotesAsync(since, later)); // up to date

        await _index.DeleteNoteAsync(note.Id); // the user deleted the file
        Assert.Single(await _index.ConversationsNeedingNotesAsync(since, later));
    }

    [Fact]
    public async Task The_queue_runs_jobs_in_time_order_without_duplicates()
    {
        var now = DateTimeOffset.Now;
        await _index.EnqueueAsync(MemoryJobKind.ConsolidateProfile, Nova, null, null, now.AddMinutes(5));
        await _index.EnqueueAsync(MemoryJobKind.SummarizeConversation, Nova, 7, null, now.AddMinutes(1));
        await _index.EnqueueAsync(MemoryJobKind.SummarizeConversation, Nova, 7, null, now); // same job, sooner
        await _index.EnqueueAsync(MemoryJobKind.IndexNote, Nova, null, "nova/a.md", now.AddMinutes(2));

        var first = await _index.NextJobAsync(now);
        Assert.Equal((MemoryJobKind.SummarizeConversation, 7L), (first!.Kind, first.ConversationId!.Value));
        Assert.Null(await new SqliteVaultIndex(_fx.Reopen()).NextJobAsync(now.AddSeconds(-1)));

        await _index.PostponeJobAsync(first.Id, now.AddMinutes(10), failed: true);
        Assert.Equal(MemoryJobKind.IndexNote, (await _index.NextJobAsync(now.AddMinutes(3)))!.Kind);
        await _index.CompleteJobAsync((await _index.NextJobAsync(now.AddMinutes(3)))!.Id);
        Assert.Equal(MemoryJobKind.ConsolidateProfile, (await _index.NextJobAsync(now.AddMinutes(6)))!.Kind);
        await _index.CompleteJobAsync((await _index.NextJobAsync(now.AddMinutes(6)))!.Id);
        Assert.Equal(1, (await _index.NextJobAsync(now.AddMinutes(11)))!.Attempts);

        // One assistant's jobs only (its AI turning off).
        Assert.NotNull(await _index.NextJobAsync(now.AddMinutes(11), assistantId: Nova));
        Assert.Null(await _index.NextJobAsync(now.AddMinutes(11), assistantId: Nova + 1000));
    }

    [Fact]
    public async Task State_is_kept_across_restarts()
    {
        Assert.Null(await _index.GetStateAsync("notes-since"));
        await _index.SetStateAsync("notes-since", "a");
        await _index.SetStateAsync("notes-since", "b");
        Assert.Equal("b", await new SqliteVaultIndex(_fx.Reopen()).GetStateAsync("notes-since"));
    }
}
