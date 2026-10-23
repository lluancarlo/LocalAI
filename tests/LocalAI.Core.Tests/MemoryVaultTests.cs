using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class VaultLayoutTests
{
    [Theory]
    [InlineData("Jessica", "jessica")]
    [InlineData("José da Silva", "jose-da-silva")]
    [InlineData("  Read selection: how it works?  ", "read-selection-how-it-works")]
    [InlineData("C# & .NET 10", "c-net-10")]
    [InlineData("!!!", "note")]
    public void Names_are_portable(string text, string expected) => Assert.Equal(expected, VaultLayout.Slug(text));

    [Fact]
    public void Long_names_are_cut_without_a_trailing_dash()
    {
        var slug = VaultLayout.Slug(string.Join(' ', Enumerable.Repeat("word", 30)));
        Assert.True(slug.Length <= 60);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void Assistants_with_the_same_slug_are_told_apart_by_id()
    {
        var jose = PromptBuilderTests.Profile("pt") with { Id = 1, Name = "José" };
        var jose2 = jose with { Id = 2, Name = "Jose" };
        var nova = jose with { Id = 3, Name = "Nova" };
        Assert.Equal("jose-1", VaultLayout.AssistantFolder(jose, [jose, jose2, nova]));
        Assert.Equal("nova", VaultLayout.AssistantFolder(nova, [jose, jose2, nova]));
        Assert.Equal("jessica/jessica-profile.md", VaultLayout.ProfilePath("jessica"));
    }

    [Fact]
    public void A_second_conversation_with_the_same_title_and_day_gets_a_number()
    {
        var date = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
        var taken = new HashSet<string> { "jessica/conversations/2026-10-22-read-selection.md" };
        Assert.Equal("jessica/conversations/2026-10-22-read-selection-2.md",
            VaultLayout.ConversationPath("jessica", date, "Read selection", taken.Contains));
    }
}

public sealed class NoteDocumentTests
{
    private const string Note =
        "---\ntype: conversation\nupdated: 2026-10-22T10:00\n---\n# Title\n\nmy intro\n<!-- local-ai:begin -->\n## Summary\nText.\n<!-- local-ai:end -->\n\nmy notes below\n";

    [Fact]
    public void Round_trips_and_keeps_the_users_text()
    {
        var doc = NoteDocument.Parse(Note);
        Assert.Equal("## Summary\nText.", doc.Generated);
        Assert.Contains("my intro", doc.Before, StringComparison.Ordinal);
        Assert.Contains("my notes below", doc.After, StringComparison.Ordinal);
        Assert.Equal(Note, doc.Render());

        var rewritten = (doc with { Generated = "## Summary\nNew text." }).WithFrontmatter("updated", "2026-10-23T09:00").Render();
        Assert.Contains("my intro", rewritten, StringComparison.Ordinal);
        Assert.Contains("my notes below", rewritten, StringComparison.Ordinal);
        Assert.Contains("New text.", rewritten, StringComparison.Ordinal);
        Assert.Contains("updated: 2026-10-23T09:00", rewritten, StringComparison.Ordinal);
        Assert.Equal("conversation", NoteDocument.Parse(rewritten).GetFrontmatter("type"));
    }

    [Fact]
    public void A_note_without_markers_belongs_to_the_user()
    {
        var doc = NoteDocument.Parse("# Mine\n\nAll mine.\n");
        Assert.False(doc.HasGeneratedBlock);
        Assert.Equal("# Mine\n\nAll mine.\n", doc.Render());
    }

    [Fact]
    public void Re_saving_with_other_line_endings_is_not_an_edit()
    {
        Assert.Equal(NoteDocument.Hash("## A\n- one\n"), NoteDocument.Hash("## A  \r\n- one\r\n"));
        Assert.NotEqual(NoteDocument.Hash("## A\n- one"), NoteDocument.Hash("## A\n- two"));
    }
}

public sealed class NoteFormatsTests
{
    private static readonly MemoryHeadings English = new();

    [Theory]
    [InlineData("{\"summary\": \"S\", \"decisions\": [\"D\"], \"openItems\": [], \"facts\": [\"User likes tea.\"]}", true)]
    [InlineData("Sure:\n```json\n{\"summary\": \"S\", \"facts\": []}\n```", true)]
    [InlineData("I cannot do that.", false)]
    [InlineData("{not json", false)]
    public void Reads_the_models_json_robustly(string raw, bool valid) =>
        Assert.Equal(valid, NoteFormats.ParseConversation(raw) != null);

    [Fact]
    public void Conversation_notes_have_fixed_sections_and_skip_empty_ones()
    {
        var md = NoteFormats.RenderConversation(new ConversationSummary("We built X.", ["Use Y"], [], ["User likes tea."]), English);
        Assert.Equal("## Summary\nWe built X.\n\n## Decisions\n- Use Y\n\n## Facts learned\n- User likes tea.", md);
        Assert.Equal(["User likes tea."], NoteFormats.FactsOf(md, [English]));
    }

    [Fact]
    public void The_profile_reads_sections_in_any_language()
    {
        var portuguese = new MemoryHeadings { Preferences = "Preferências", Skills = "Habilidades" };
        var profile = NoteFormats.ReadProfile("## Preferências\n- Prefere C#\n## Skills\n- Knows Rust\n## Other\n- Lives in Rome", [English, portuguese]);
        Assert.Equal(["Prefere C#"], profile.Preferences);
        Assert.Equal(["Knows Rust"], profile.Skills);
        Assert.Equal(["Lives in Rome"], profile.About);
    }

    [Fact]
    public void Items_the_user_wrote_survive_the_merge()
    {
        var current = ProfileContent.Empty with { Preferences = ["Prefers dark mode", "My own note"] };
        var merged = ProfileContent.Empty with { Preferences = ["Prefers dark mode"], Skills = ["Knows C#"] };
        var kept = NoteFormats.KeepUserItems(merged, current, new HashSet<string> { "My own note" });
        Assert.Equal(["Prefers dark mode", "My own note"], kept.Preferences);
        Assert.Equal(["Knows C#"], kept.Skills);
    }

    [Fact]
    public void The_transcript_keeps_the_newest_part_and_the_selection()
    {
        var messages = Enumerable.Range(0, 200)
            .Select(i => new StoredMessage { Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, Content = $"message {i} " + new string('x', 100) })
            .Append(new StoredMessage { Role = ChatRole.User, Content = "What is this?", SelectedText = "ON DELETE CASCADE" })
            .ToList();
        var transcript = NoteFormats.Transcript(messages);
        Assert.True(transcript.Length <= NoteFormats.MaxTranscriptChars + 200);
        Assert.Contains("ON DELETE CASCADE", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("message 0 ", transcript, StringComparison.Ordinal);
    }
}

public sealed class MemoryVaultTests
{
    private const string Summary =
        "{\"summary\": \"The user set up pagination for the orders API.\", \"decisions\": [\"Use cursor pagination\"], " +
        "\"openItems\": [\"Write the tests\"], \"facts\": [\"User works on an orders API.\"]}";

    private readonly MemoryHarness _m;
    private AssistantProfile _nova = null!;

    public MemoryVaultTests()
    {
        var llm = new FakeLanguageModel
        {
            Responder = messages => messages[0].Content.Contains("profile of the USER", StringComparison.Ordinal)
                ? ["{\"about\": [], \"preferences\": [\"Prefers C#.\"], \"skills\": [], \"projects\": [\"Works on an orders API.\"], \"routine\": []}"]
                : [Summary],
        };
        _m = new MemoryHarness(llm);
    }

    private async Task<long> ConversationAsync(string title = "Orders API pagination")
    {
        _nova = await _m.Assistants.CreateAsync(new NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        _m.Context.Set(_nova);
        var c = await _m.Conversations.CreateAsync(title);
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.User, Content = "How do we paginate?" });
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.Assistant, Content = "Use a cursor." });
        return c.Id;
    }

    private string ConversationFile => Assert.Single(_m.Storage.Files.Keys, k => k.Contains("/conversations/", StringComparison.Ordinal));

    [Fact]
    public async Task Writes_a_conversation_note_with_frontmatter_markers_and_sections()
    {
        var id = await ConversationAsync();

        Assert.True(await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None));

        Assert.StartsWith("nova/conversations/", ConversationFile, StringComparison.Ordinal);
        Assert.EndsWith("-orders-api-pagination.md", ConversationFile, StringComparison.Ordinal);
        var text = _m.Storage.Files[ConversationFile];
        Assert.Contains("type: conversation", text, StringComparison.Ordinal);
        Assert.Contains($"conversation: {id}", text, StringComparison.Ordinal);
        Assert.Contains("tags: [local-ai, local-ai/nova]", text, StringComparison.Ordinal);
        Assert.Contains("# Orders API pagination", text, StringComparison.Ordinal);
        Assert.Contains(NoteDocument.BeginMarker + "\n## Summary\nThe user set up pagination", text, StringComparison.Ordinal);
        Assert.Contains("## Open items\n- Write the tests", text, StringComparison.Ordinal);
        var note = Assert.Single(_m.Index.Notes);
        Assert.Equal(NoteKind.Conversation, note.Kind);
        Assert.Contains(_m.Index.Chunks, c => c.Text.Contains("cursor pagination", StringComparison.Ordinal));
        Assert.All(_m.Index.Chunks, c => Assert.NotNull(c.Embedding));

        // Nothing new: the note is not written again.
        Assert.False(await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None));
    }

    [Fact]
    public async Task Notes_follow_the_notes_language()
    {
        _m.Options.Value.Memory.NotesLanguage = "pt";
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        Assert.Contains("## Resumo", _m.Storage.Files[ConversationFile], StringComparison.Ordinal);
        Assert.Contains("Brazilian Portuguese", _m.Llm.Requests[^1][0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_block_the_user_edited_is_never_overwritten()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        var edited = _m.Storage.Files[ConversationFile].Replace("Use cursor pagination", "Use keyset pagination (my correction)", StringComparison.Ordinal);
        _m.Storage.Files[ConversationFile] = edited;
        _m.Index.Notes[0] = _m.Index.Notes[0] with { SourceUpdatedAt = DateTimeOffset.MinValue }; // the conversation went on

        Assert.False(await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None));
        Assert.Equal(edited, _m.Storage.Files[ConversationFile]);
    }

    [Fact]
    public async Task Rewriting_keeps_the_text_the_user_added_outside_the_markers()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        _m.Storage.Files[ConversationFile] += "\nMy own remark.\n";
        _m.Index.Notes[0] = _m.Index.Notes[0] with { SourceUpdatedAt = DateTimeOffset.MinValue };

        Assert.True(await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None));
        Assert.Contains("My own remark.", _m.Storage.Files[ConversationFile], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_profile_merges_new_facts_and_keeps_the_users_lines()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        Assert.True(await _m.Vault.ConsolidateProfileAsync(_nova.Id, CancellationToken.None));
        const string path = "nova/nova-profile.md";
        Assert.Contains("## Preferences\n- Prefers C#.", _m.Storage.Files[path], StringComparison.Ordinal);
        Assert.Contains("aliases: [\"Nova - Profile\"]", _m.Storage.Files[path], StringComparison.Ordinal);

        // The user adds a line; the next merge (whose model output lacks it) keeps it.
        _m.Storage.Files[path] = _m.Storage.Files[path].Replace("- Prefers C#.", "- Prefers C#.\n- Hates meetings before 10.", StringComparison.Ordinal);
        await Task.Delay(20); // the next note is newer than the profile
        var second = await _m.Conversations.CreateAsync("Another chat");
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = second.Id, Role = ChatRole.User, Content = "Hi" });
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = second.Id, Role = ChatRole.Assistant, Content = "Hello" });
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, second.Id, CancellationToken.None);
        Assert.True(await _m.Vault.ConsolidateProfileAsync(_nova.Id, CancellationToken.None));

        Assert.Contains("- Hates meetings before 10.", _m.Storage.Files[path], StringComparison.Ordinal);
        Assert.Contains("(user) Hates meetings before 10.", _m.Llm.Requests[^1][1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_new_facts_the_profile_is_left_alone()
    {
        await ConversationAsync();
        Assert.False(await _m.Vault.ConsolidateProfileAsync(_nova.Id, CancellationToken.None));
        Assert.Empty(_m.Llm.Requests);
    }

    [Fact]
    public async Task The_old_memory_moves_into_the_profile_without_the_model()
    {
        await ConversationAsync();
        _m.Index.LegacyFacts[_nova.Id] = ["User likes tea.", "User lives in Rome."];

        await _m.Vault.MigrateLegacyFactsAsync(_nova.Id, CancellationToken.None);

        Assert.Contains("## About\n- User likes tea.\n- User lives in Rome.", _m.Storage.Files["nova/nova-profile.md"], StringComparison.Ordinal);
        Assert.Empty(_m.Llm.Requests);
        Assert.Empty(_m.Index.LegacyFacts);
    }

    [Fact]
    public async Task Scanning_indexes_the_users_notes_and_forgets_deleted_ones()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        _m.Storage.Files["nova/my-ideas.md"] = "# Ideas\n\n## Next\nTry a vector index for the orders search.\n";
        _m.Storage.Files.Remove(ConversationFile);

        await _m.Vault.ScanAsync(_nova.Id, CancellationToken.None);

        var note = Assert.Single(_m.Index.Notes);
        Assert.Equal("nova/my-ideas.md", note.Path);
        Assert.Contains(_m.Index.Chunks, c => c.Text.Contains("vector index", StringComparison.Ordinal));
        Assert.Null(await _m.Index.FindConversationNoteAsync(id)); // so the conversation note is written again
    }

    [Fact]
    public async Task Recall_brings_the_profile_and_the_best_passages()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        await _m.Vault.ConsolidateProfileAsync(_nova.Id, CancellationToken.None);

        var memory = await _m.Vault.RecallAsync("which pagination did we pick for orders", CancellationToken.None);

        Assert.Contains("Prefers C#.", memory.Profile, StringComparison.Ordinal);
        Assert.DoesNotContain("type: profile", memory.Profile, StringComparison.Ordinal);
        Assert.Contains(memory.Passages, p => p.Text.Contains("cursor pagination", StringComparison.Ordinal));
        Assert.DoesNotContain(memory.Passages, p => p.NoteTitle == "nova-profile"); // the profile is already in

        _m.Options.Value.Memory.Enabled = false;
        Assert.True((await _m.Vault.RecallAsync("pagination", CancellationToken.None)).IsEmpty);
    }

    [Fact]
    public async Task Only_the_newest_conversation_notes_are_recalled_and_marked_in_memory()
    {
        _m.Options.Value.Memory.MaxConversationNotes = 2;
        var ids = new List<long> { await ConversationAsync() };
        for (var i = 2; i <= 3; i++)
        {
            var c = await _m.Conversations.CreateAsync($"Orders API pagination {i}");
            await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.User, Content = "How do we paginate?" });
            await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.Assistant, Content = "Use a cursor." });
            ids.Add(c.Id);
        }
        foreach (var id in ids) Assert.True(await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None));

        Assert.Equal([ids[1], ids[2]], (await _m.Vault.ConversationsInMemoryAsync()).Order());
        var oldest = Path.GetFileNameWithoutExtension((await _m.Index.FindConversationNoteAsync(ids[0]))!.Path);
        var memory = await _m.Vault.RecallAsync("which pagination did we pick for orders", CancellationToken.None);
        Assert.NotEmpty(memory.Passages);
        Assert.DoesNotContain(memory.Passages, p => p.NoteTitle == oldest);

        // A newer note gone: the older one is searched again.
        await _m.Vault.DeleteConversationNoteAsync(ids[2]);
        Assert.Equal([ids[0], ids[1]], (await _m.Vault.ConversationsInMemoryAsync()).Order());
    }

    [Fact]
    public void Keyword_search_leaves_out_the_common_words_of_every_language() =>
        Assert.Equal("pagination pick orders", _m.Vault.Keywords("Which pagination did we pick for the orders, e para que?"));

    private const string IdentityFile = "lia/lia-identity.md";

    private async Task<AssistantProfile> LiaAsync(string style = "Warm and witty.\n\n### Tone\n- Casual")
    {
        var lia = await _m.Assistants.CreateAsync(new NewAssistant("Lia", style, "gemma-4-12b", "pt_BR-dii-high", "pt"),
            new VoiceStyle { Speed = 1.1, Expressiveness = 0.75 });
        _m.Context.Set(lia);
        return lia;
    }

    [Fact]
    public async Task The_identity_note_says_who_the_assistant_is_with_its_settings()
    {
        var lia = await LiaAsync();

        Assert.True(await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None));

        var text = _m.Storage.Files[IdentityFile];
        Assert.Contains("type: identity", text, StringComparison.Ordinal);
        Assert.Contains("model: \"gemma-4-12b\"", text, StringComparison.Ordinal);
        Assert.Contains("voice: \"pt_BR-dii-high\"", text, StringComparison.Ordinal);
        Assert.Contains("voice-speed: 1.1", text, StringComparison.Ordinal);
        Assert.Contains("voice-expressiveness: 0.75", text, StringComparison.Ordinal);
        Assert.Contains("# Lia - Identity", text, StringComparison.Ordinal);
        Assert.Contains("## Who Lia is\nLia is a personal AI", text, StringComparison.Ordinal);
        Assert.Contains("## Personality and style\nWarm and witty.\n\n### Tone\n- Casual", text, StringComparison.Ordinal);
        Assert.Contains("[[lia-profile]]", text, StringComparison.Ordinal);
        Assert.Equal(NoteKind.Identity, Assert.Single(_m.Index.Notes).Kind);

        // Up to date: not written again.
        Assert.False(await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_identity_note_follows_a_new_shortcut_but_keeps_the_users_edits()
    {
        var lia = await LiaAsync();
        await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None);
        _m.Storage.Files[IdentityFile] += "My own line about Lia.\n";

        Assert.True(Desktop.HotkeyGesture.TryParse("Ctrl+Alt+L", out var ctrlAltL));
        await _m.Assistants.SetHotkeyAsync(lia.Id, ctrlAltL);
        Assert.True(await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None));
        Assert.Contains("shortcut: \"Ctrl+Alt+L\"", _m.Storage.Files[IdentityFile], StringComparison.Ordinal);
        Assert.Contains("My own line about Lia.", _m.Storage.Files[IdentityFile], StringComparison.Ordinal); // outside the markers

        // The user rewrote the generated part: it is theirs now and defines who the assistant is.
        _m.Storage.Files[IdentityFile] = _m.Storage.Files[IdentityFile].Replace("Warm and witty.", "Calm and formal.", StringComparison.Ordinal);
        await _m.Assistants.SetHotkeyAsync(lia.Id, null);
        Assert.False(await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None));
        Assert.Contains("Calm and formal.", _m.Storage.Files[IdentityFile], StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_pasted_text_is_searched_by_its_start_and_its_end()
    {
        var pasted = "Start of the log. " + new string('x', 20_000) + " What failed here?";
        var query = MemoryVault.SearchQuery(pasted);
        Assert.True(query.Length <= MemoryVault.MaxQueryChars + 5);
        Assert.StartsWith("Start of the log.", query, StringComparison.Ordinal);
        Assert.EndsWith("What failed here?", query, StringComparison.Ordinal);
        Assert.Equal("short question", MemoryVault.SearchQuery("short question"));
    }

    [Fact]
    public async Task Recall_brings_the_identity_for_the_prompt()
    {
        var lia = await LiaAsync("# Role\nWarm and witty.");
        Assert.Null((await _m.Vault.RecallAsync("hello", CancellationToken.None)).Identity); // not written yet

        await _m.Vault.WriteIdentityNoteAsync(lia.Id, CancellationToken.None);
        var memory = await _m.Vault.RecallAsync("warm witty personality", CancellationToken.None);

        Assert.Contains("Warm and witty.", memory.Identity, StringComparison.Ordinal);
        Assert.DoesNotContain("type: identity", memory.Identity, StringComparison.Ordinal);
        Assert.DoesNotContain("# Lia - Identity", memory.Identity, StringComparison.Ordinal); // the note's title
        Assert.Contains("# Role", memory.Identity, StringComparison.Ordinal); // a heading of the style stays
        Assert.DoesNotContain(memory.Passages, p => p.NoteTitle == "lia-identity"); // already in the prompt
    }

    [Fact]
    public async Task Deleting_a_conversation_or_an_assistant_deletes_their_notes()
    {
        var id = await ConversationAsync();
        await _m.Vault.WriteConversationNoteAsync(_nova.Id, id, CancellationToken.None);
        await _m.Vault.MigrateLegacyFactsAsync(_nova.Id, CancellationToken.None);

        await _m.Vault.DeleteConversationNoteAsync(id);
        Assert.DoesNotContain(_m.Storage.Files.Keys, k => k.Contains("/conversations/", StringComparison.Ordinal));

        _m.Storage.Files["nova/nova-profile.md"] = "# Profile\n";
        await _m.Vault.DeleteAssistantNotesAsync(_nova);
        Assert.Empty(_m.Storage.Files);
        Assert.Empty(_m.Index.Notes);
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeActivity : IAssistantActivity
{
    private bool _busy;
    public bool IsBusy => _busy;
    public event EventHandler<bool>? BusyChanged;

    public void Set(bool busy)
    {
        _busy = busy;
        BusyChanged?.Invoke(this, busy);
    }
}

public sealed class MemoryWorkerTests : IAsyncDisposable
{
    private static readonly LanguageData Languages = LanguageData.LoadDefault();
    private readonly ManualTime _time = new();
    private readonly FakeActivity _activity = new();
    private readonly FakeLanguageModel _llm = new();
    private readonly MemoryHarness _m;
    private readonly AssistantSession _session;
    private readonly MemoryWorker _worker;
    private readonly List<string> _outcomes = [];

    public MemoryWorkerTests()
    {
        _llm.Responder = messages => messages[0].Content.Contains("profile of the USER", StringComparison.Ordinal)
            ? ["{\"about\": [\"Likes tea\"]}"]
            : ["{\"summary\": \"S\", \"facts\": [\"User likes tea.\"]}"];
        _m = new MemoryHarness(_llm);
        var options = _m.Options;
        var speech = new SpeechOutput(new FakeTts(), new FakePlayer(), Languages, NullLogger<SpeechOutput>.Instance);
        _session = new AssistantSession(_llm, _m.Conversations, new PromptBuilder(options, _m.Context), new HeuristicLanguageDetector(Languages),
            _m.Vault, speech, options, NullLogger<AssistantSession>.Instance);
        _worker = new MemoryWorker(_m.Vault, _m.Index, _m.Storage, _m.Assistants, _activity, _session, _m.Context, _llm, options, _time,
            NullLogger<MemoryWorker>.Instance, _m.Work);
        _worker.JobFinished += (_, e) => { lock (_outcomes) _outcomes.Add($"{e.Job.Kind}:{e.Outcome}"); };
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();
        _session.Dispose();
    }

    private async Task<(AssistantProfile Assistant, long Conversation)> ConversationAsync()
    {
        var nova = await _m.Assistants.CreateAsync(new NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        _m.Context.Set(nova);
        var c = await _m.Conversations.CreateAsync("Tea");
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.User, Content = "I like tea" });
        await _m.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.Assistant, Content = "Noted" });
        _m.Index.NeedingNotes.Add((c.Id, nova.Id));
        return (nova, c.Id);
    }

    private async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
            _worker.Nudge();
        }
        Assert.True(condition());
    }

    private bool Finished(string outcome)
    {
        lock (_outcomes) return _outcomes.Contains(outcome);
    }

    [Fact]
    public async Task Writes_notes_only_once_the_assistant_has_been_idle()
    {
        await ConversationAsync();
        var working = new List<bool>();
        _worker.WorkingChanged += (_, w) => { lock (working) working.Add(w); };
        _worker.Start();
        await Task.Delay(200);
        _worker.Nudge();
        await Task.Delay(200);
        Assert.Empty(_m.Storage.Files); // not idle long enough yet

        _time.Advance(MemoryWorker.IdleDelay + TimeSpan.FromSeconds(1));
        await UntilAsync(() => Finished("SummarizeConversation:done"));
        Assert.Contains(_m.Storage.Files.Keys, k => k.StartsWith("nova/conversations/", StringComparison.Ordinal));
        // The profile merge waits a little to take several notes at once.
        Assert.Contains(_m.Index.Jobs, j => j.Kind == MemoryJobKind.ConsolidateProfile);
        Assert.Equal("notes-since", Assert.Single(_m.Index.State.Keys, k => k == "notes-since"));
        // The app shows background work while each job runs (yellow status dot and tray dot).
        lock (working)
        {
            Assert.True(working.Count >= 2 && working.Count % 2 == 0);
            Assert.Equal(working.Count / 2, working.Count(w => w));
        }
        Assert.False(_worker.IsWorking);
    }

    [Fact]
    public async Task Finishing_an_assistant_does_all_its_memory_work_at_once()
    {
        var (nova, _) = await ConversationAsync(); // just talked: the idle loop would still wait
        var steps = new List<string>();
        _m.Work.Changed += (_, _) => { lock (steps) steps.Add(_m.Work.Current?.Text ?? ""); };

        var jobs = await _worker.FinishAsync(nova.Id, CancellationToken.None);

        Assert.True(jobs >= 3);
        Assert.Contains(_m.Storage.Files.Keys, k => k.StartsWith("nova/conversations/", StringComparison.Ordinal));
        Assert.Contains("Likes tea", _m.Storage.Files["nova/nova-profile.md"], StringComparison.Ordinal); // merged now, not in 5 minutes
        Assert.True(_m.Storage.Files.ContainsKey("nova/nova-identity.md"));
        Assert.DoesNotContain(_m.Index.Jobs, j => j.AssistantId == nova.Id && j.Kind != MemoryJobKind.IndexNote);
        lock (steps) Assert.Contains(steps, s => s.StartsWith("Nova · Conversation note:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_the_finish_leaves_the_work_queued()
    {
        var (nova, _) = await ConversationAsync();
        _llm.TokenDelay = TimeSpan.FromMilliseconds(30);
        _llm.Responder = _ => Enumerable.Range(0, 100).Select(_ => " ").Append("{\"summary\": \"S\", \"facts\": []}");
        using var cts = new CancellationTokenSource();
        _worker.JobStarted += (_, job) => { if (job.Kind == MemoryJobKind.SummarizeConversation) cts.CancelAfter(100); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.FinishAsync(nova.Id, cts.Token));

        Assert.Contains(_m.Index.Jobs, j => j.Kind == MemoryJobKind.SummarizeConversation); // done later
        Assert.DoesNotContain(_m.Storage.Files.Keys, k => k.Contains("/conversations/", StringComparison.Ordinal));
        Assert.Null(_m.Work.Current);
    }

    [Fact]
    public async Task Writes_the_identity_notes_of_existing_and_new_assistants()
    {
        var (nova, _) = await ConversationAsync();
        _time.Advance(MemoryWorker.IdleDelay + TimeSpan.FromSeconds(1));
        _worker.Start();
        await UntilAsync(() => _m.Storage.Files.ContainsKey("nova/nova-identity.md")); // existing assistant, at start

        await _m.Assistants.CreateAsync(new NewAssistant("Lia", "Calm.", "m", "v", "pt"), VoiceStyle.Default);
        await _worker.QueueIdentityNotesAsync(); // the app does this when an assistant is created
        await UntilAsync(() => _m.Storage.Files.ContainsKey("lia/lia-identity.md"));
        Assert.Contains("Calm.", _m.Storage.Files["lia/lia-identity.md"], StringComparison.Ordinal);
        Assert.Equal(nova.Name, NoteDocument.Parse(_m.Storage.Files["nova/nova-identity.md"]).GetFrontmatter("assistant"));
    }

    [Fact]
    public async Task The_user_pre_empts_background_work_and_it_resumes_later()
    {
        await ConversationAsync();
        _llm.TokenDelay = TimeSpan.FromMilliseconds(30);
        _llm.Responder = _ => Enumerable.Range(0, 100).Select(_ => " ").Append("{\"summary\": \"S\", \"facts\": []}");
        var started = new TaskCompletionSource();
        _worker.JobStarted += (_, job) => { if (job.Kind == MemoryJobKind.SummarizeConversation) started.TrySetResult(); };
        _time.Advance(MemoryWorker.IdleDelay + TimeSpan.FromSeconds(1));
        _worker.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _activity.Set(true); // the user sends a message

        await UntilAsync(() => Finished("SummarizeConversation:pre-empted"));
        Assert.DoesNotContain(_m.Storage.Files.Keys, k => k.Contains("/conversations/", StringComparison.Ordinal));
        var job = Assert.Single(_m.Index.Jobs, j => j.Kind == MemoryJobKind.SummarizeConversation);
        Assert.Equal(0, job.Attempts); // not counted as a failure

        _llm.TokenDelay = TimeSpan.Zero;
        _activity.Set(false);
        _time.Advance(MemoryWorker.IdleDelay + TimeSpan.FromSeconds(1));
        await UntilAsync(() => Finished("SummarizeConversation:done"));
        Assert.Contains(_m.Storage.Files.Keys, k => k.Contains("/conversations/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_note_edited_in_obsidian_is_indexed_again()
    {
        var (nova, _) = await ConversationAsync();
        _m.Index.NeedingNotes.Clear();
        _time.Advance(MemoryWorker.IdleDelay + TimeSpan.FromSeconds(1));
        _worker.Start();

        _m.Storage.Edit("nova/ideas.md", "# Ideas\n\n## Next\nAdd a search box.\n");

        await UntilAsync(() => _m.Index.Chunks.Any(c => c.Text.Contains("search box", StringComparison.Ordinal)));
        Assert.Equal(nova.Id, Assert.Single(_m.Index.Notes, n => n.Path == "nova/ideas.md").AssistantId);
    }
}
