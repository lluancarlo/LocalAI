using System.Globalization;
using System.Text.RegularExpressions;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Memory.Vault;

/// <summary>
/// What the memory adds to a prompt: the user's profile, the most relevant passages of past notes, the identity note
/// (who the assistant is; null until it is written, then the assistant's style is used instead) and the most relevant
/// passages of the documents it learned (its learn folder).
/// </summary>
public sealed record MemoryContext(string? Profile, IReadOnlyList<NoteChunk> Passages, string? Identity = null,
    IReadOnlyList<NoteChunk>? Documents = null)
{
    public static readonly MemoryContext Empty = new(null, []);
    public IReadOnlyList<NoteChunk> Documents { get; init; } = Documents ?? [];
    public bool IsEmpty => string.IsNullOrWhiteSpace(Profile) && Passages.Count == 0 && string.IsNullOrWhiteSpace(Identity) && Documents.Count == 0;
}

/// <summary>
/// An assistant's learn folder: its files, and what the last Learn indexed (when, how many passages, and whether the
/// files changed since).
/// </summary>
public sealed record LearnStatus(IReadOnlyList<VaultFile> Files, DateTimeOffset? LearnedAt, int LearnedFiles, int Passages, bool Changed);

/// <summary>
/// Each assistant's memory as Markdown notes (its profile of the user and one note per conversation), the source of
/// truth, plus their index for retrieval. The user's edits win: a generated block the user changed is never
/// overwritten, items the user wrote in the profile always survive, and text outside the markers is never touched.
/// Note and conversation text is never logged.
/// </summary>
public sealed partial class MemoryVault(
    IVaultStorage storage,
    IVaultIndex index,
    IAssistantStore assistants,
    IConversationStore conversations,
    ILanguageModel llm,
    IEmbeddingService embeddings,
    AssistantContext context,
    LanguageData languages,
    IOptions<LocalAiOptions> options,
    TimeProvider time,
    ILogger<MemoryVault> logger,
    WorkActivity? activity = null)
{
    private readonly WorkActivity _activity = activity ?? new WorkActivity();

    /// <summary>Shows a step of the background memory work (slow motion pauses here).</summary>
    private Task StepAsync(string text, CancellationToken ct) => _activity.StepAsync(WorkKind.Memory, text, ct);

    /// <summary>Longest section kept as one passage; longer ones are split at paragraphs.</summary>
    private const int MaxChunkChars = 1200;
    /// <summary>
    /// Longest search query: a long pasted text is searched by its start and its end (where the question usually is).
    /// The embedding model reads at most 2048 tokens.
    /// </summary>
    internal const int MaxQueryChars = 2000;
    /// <summary>
    /// Longest identity sent to the model: the longest style (<see cref="AssistantManager.MaxStyleLength"/>) with room
    /// for the rest of the note, so the style is never cut.
    /// </summary>
    private const int MaxIdentityChars = AssistantManager.MaxStyleLength + 4000;
    /// <summary>
    /// Full-text matches read for one search: more than are ranked, since some are of notes recall does not search.
    /// </summary>
    private const int KeywordMatches = 300;
    /// <summary>Passages embedded in one request to the embedding server.</summary>
    private const int EmbedBatchSize = 16;
    private readonly MemoryOptions _options = options.Value.Memory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>What recall searches for one assistant: kept until the index changes, so a reply does not read it all again.</summary>
    /// <param name="Conversations">The conversations whose note is searched (newest <see cref="MemoryOptions.MaxConversationNotes"/>).</param>
    /// <param name="Documents">The passages of the learned documents (searched apart, with their own share).</param>
    private sealed record RecallSet(IReadOnlyList<NoteChunk> Chunks, IReadOnlySet<long> Conversations, IReadOnlyList<NoteChunk> Documents);

    private readonly Lock _recallGate = new();
    private readonly Dictionary<long, RecallSet> _recallSets = [];
    private long _indexVersion;
    private HashSet<string>? _stopWords;

    [GeneratedRegex(@"[\p{L}\p{N}_]+")] private static partial Regex Word();

    /// <summary>Raised after a note was written, re-indexed or removed (the Memory page refreshes).</summary>
    public event EventHandler? NotesChanged;

    private MemoryHeadings Headings => NoteFormats.Headings(languages, _options.NotesLanguage);
    private IReadOnlyList<MemoryHeadings> AnyHeadings => NoteFormats.AllHeadings(languages);

    private static readonly GenerationOptions Background = new() { Temperature = 0.2f, MaxTokens = 800, Background = true, Json = true };

    public string Root => storage.Root;

    public async Task<string> AssistantFolderAsync(AssistantProfile assistant, CancellationToken ct = default) =>
        VaultLayout.AssistantFolder(assistant, await assistants.ListAsync(ct).ConfigureAwait(false));

    private async Task<AssistantProfile?> FindAssistantAsync(long id, CancellationToken ct) =>
        (await assistants.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == id);

    private string Now() => time.GetLocalNow().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    // ---------------- Conversation notes ----------------

    /// <summary>Writes (or updates) the note of a conversation. Returns false when nothing had to be written.</summary>
    public async Task<bool> WriteConversationNoteAsync(long assistantId, long conversationId, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return false;
        if (await conversations.GetAsync(conversationId, ct).ConfigureAwait(false) is not { } conversation) return false;
        await StepAsync("Conversation note: reading the conversation", ct).ConfigureAwait(false);
        var messages = await conversations.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        if (!messages.Any(m => m.Role == ChatRole.Assistant)) return false;

        var existing = await index.FindConversationNoteAsync(conversationId, ct).ConfigureAwait(false);
        if (existing?.SourceUpdatedAt >= conversation.UpdatedAt && storage.Exists(existing.Path)) return false; // up to date

        var current = existing != null ? storage.Read(existing.Path) : null;
        var document = current != null ? NoteDocument.Parse(current) : null;
        if (existing != null && document != null && IsUserOwned(document, existing))
        {
            // The user changed the generated part: their version stays. Remember it is current so it is not retried.
            await index.SaveNoteAsync(existing with { SourceUpdatedAt = conversation.UpdatedAt }, ct).ConfigureAwait(false);
            IndexChanged();
            return false;
        }

        // A note already written is updated with the messages that came after it, so what it says of the start of a
        // long conversation stays. A long conversation (or a long new part) is read in parts, each one updating the note.
        var summary = existing != null ? NoteFormats.ReadConversation(document?.Generated, AnyHeadings) : null;
        var unread = summary != null && existing?.SourceUpdatedAt is { } covered ? messages.Where(m => m.CreatedAt > covered).ToList() : messages;
        if (summary != null && !unread.Any(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Content)))
        {
            // Nothing newer than the note to read (e.g. clocks changed): the whole conversation, from scratch.
            summary = null;
            unread = messages;
        }
        var parts = NoteFormats.TranscriptParts(unread);
        for (var i = 0; i < parts.Count; i++)
        {
            await StepAsync(parts.Count == 1
                ? "Conversation note: the model summarizes the conversation"
                : $"Conversation note: the model summarizes the conversation (part {i + 1} of {parts.Count})", ct).ConfigureAwait(false);
            var raw = await llm.GenerateAsync(NoteFormats.ConversationPrompt(assistant.Name, _options.NotesLanguage, parts[i], summary), Background, ct)
                .ConfigureAwait(false);
            summary = NoteFormats.ParseConversation(raw) ?? throw new InvalidDataException("The conversation summary was not valid JSON.");
        }
        if (summary == null) return false; // nothing to summarize
        var block = NoteFormats.RenderConversation(summary, Headings);
        await StepAsync("Conversation note: saving the note", ct).ConfigureAwait(false);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
            var path = existing?.Path ?? VaultLayout.ConversationPath(folder, conversation.CreatedAt, conversation.Title, storage.Exists);
            var alias = $"{conversation.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {conversation.Title}";
            var note = (document ?? NoteDocument.Create(
            [
                ("type", "conversation"),
                ("assistant", NoteDocument.Quote(assistant.Name)),
                ("conversation", conversationId.ToString(CultureInfo.InvariantCulture)),
                ("created", conversation.CreatedAt.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)),
                ("updated", Now()),
                ("aliases", $"[{NoteDocument.Quote(alias)}]"),
                ("tags", $"[local-ai, local-ai/{folder}]"),
            ], conversation.Title, block)) with { Generated = block };
            note = note.WithFrontmatter("updated", Now());
            var text = note.Render();
            storage.Write(path, text);

            var saved = await index.SaveNoteAsync(new VaultNote
            {
                Id = existing?.Id ?? 0,
                AssistantId = assistantId,
                Path = path,
                Kind = NoteKind.Conversation,
                ConversationId = conversationId,
                GeneratedText = block,
                SourceUpdatedAt = conversation.UpdatedAt,
                UpdatedAt = time.GetLocalNow(),
            }, ct).ConfigureAwait(false);
            await IndexAsync(saved, text, ct).ConfigureAwait(false);
            logger.LogInformation("Wrote the note of conversation {Conversation} ({Facts} facts)", conversationId, summary.Facts.Count);
        }
        finally
        {
            _writeGate.Release();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>True if the user removed the markers or changed the generated block since the app wrote it.</summary>
    private static bool IsUserOwned(NoteDocument document, VaultNote note) =>
        !document.HasGeneratedBlock || NoteDocument.Hash(document.Generated) != NoteDocument.Hash(note.GeneratedText);

    // ---------------- Profile ----------------

    /// <summary>
    /// Merges the facts learned in conversation notes written since the last merge into the profile. Returns false
    /// when there was nothing new.
    /// </summary>
    public async Task<bool> ConsolidateProfileAsync(long assistantId, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return false;
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        var path = VaultLayout.ProfilePath(folder);
        var profileNote = await index.FindNoteAsync(assistantId, path, ct).ConfigureAwait(false);
        var text = storage.Read(path);
        var document = text != null ? NoteDocument.Parse(text) : null;
        if (document is { HasGeneratedBlock: false }) return false; // the whole profile is the user's

        var since = profileNote?.UpdatedAt ?? DateTimeOffset.MinValue;
        await StepAsync("Profile: collecting the new facts", ct).ConfigureAwait(false);
        var newFacts = new List<string>();
        foreach (var note in await index.ListNotesAsync(assistantId, ct).ConfigureAwait(false))
        {
            if (note.Kind != NoteKind.Conversation || note.UpdatedAt <= since || storage.Read(note.Path) is not { } noteText) continue;
            newFacts.AddRange(NoteFormats.FactsOf(NoteDocument.Parse(noteText).Generated, AnyHeadings));
        }
        if (newFacts.Count == 0) return false;

        var current = NoteFormats.ReadProfile(document?.Generated, AnyHeadings);
        var generated = NoteFormats.ReadProfile(profileNote?.GeneratedText, AnyHeadings).AllItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userItems = current.AllItems.Where(i => !generated.Contains(i)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await StepAsync($"Profile: the model merges {newFacts.Count} new facts", ct).ConfigureAwait(false);
        var raw = await llm.GenerateAsync(NoteFormats.ProfilePrompt(_options.NotesLanguage, current, userItems, newFacts), Background, ct)
            .ConfigureAwait(false);
        var merged = NoteFormats.ParseProfile(raw) ?? throw new InvalidDataException("The profile was not valid JSON.");
        await WriteProfileAsync(assistant, folder, profileNote, document, NoteFormats.KeepUserItems(merged, current, userItems), ct)
            .ConfigureAwait(false);
        logger.LogInformation("Merged {Facts} new facts into the profile of assistant {Id}", newFacts.Count, assistantId);
        return true;
    }

    /// <summary>Moves the facts of the old memory (before notes) into the profile, without the language model.</summary>
    public async Task MigrateLegacyFactsAsync(long assistantId, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return;
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        var path = VaultLayout.ProfilePath(folder);
        var profileNote = await index.FindNoteAsync(assistantId, path, ct).ConfigureAwait(false);
        var text = storage.Read(path);
        var document = text != null ? NoteDocument.Parse(text) : null;

        await StepAsync("Moving old memories into the profile", ct).ConfigureAwait(false);
        var facts = await index.TakeLegacyFactsAsync(assistantId, ct).ConfigureAwait(false);
        if (facts.Count == 0) return;
        if (document is { HasGeneratedBlock: false })
        {
            // The user owns the whole profile: add the old facts below their text instead of dropping them.
            storage.Write(path, document.Render().TrimEnd('\n') + "\n\n" + string.Join('\n', facts.Select(f => "- " + f)) + "\n");
        }
        else
        {
            var profile = NoteFormats.AddFacts(NoteFormats.ReadProfile(document?.Generated, AnyHeadings), facts);
            await WriteProfileAsync(assistant, folder, profileNote, document, profile, ct).ConfigureAwait(false);
        }
        logger.LogInformation("Moved {Count} memories of assistant {Id} into its profile note", facts.Count, assistantId);
    }

    private async Task WriteProfileAsync(AssistantProfile assistant, string folder, VaultNote? note, NoteDocument? document,
        ProfileContent profile, CancellationToken ct)
    {
        var block = NoteFormats.RenderProfile(profile, Headings);
        await StepAsync("Profile: saving the note", ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = VaultLayout.ProfilePath(folder);
            // The user edited the profile while the model was merging (Settings or Obsidian): their text wins, and
            // the facts are merged again next time (the profile's date did not move).
            var now = storage.Read(path) is { } latest ? NoteDocument.Parse(latest) : null;
            if (now?.Generated != document?.Generated || now?.HasGeneratedBlock != document?.HasGeneratedBlock)
            {
                logger.LogInformation("The profile of assistant {Id} changed during the merge: kept as it is", assistant.Id);
                return;
            }
            var updated = (document ?? NewProfile(assistant, folder, block)) with { Generated = block };
            updated = updated.WithFrontmatter("updated", Now());
            var text = updated.Render();
            storage.Write(path, text);
            var saved = await index.SaveNoteAsync(new VaultNote
            {
                Id = note?.Id ?? 0,
                AssistantId = assistant.Id,
                Path = path,
                Kind = NoteKind.Profile,
                GeneratedText = block,
                UpdatedAt = time.GetLocalNow(),
            }, ct).ConfigureAwait(false);
            await IndexAsync(saved, text, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    private NoteDocument NewProfile(AssistantProfile assistant, string folder, string block)
    {
        var title = $"{assistant.Name} - {Headings.Profile}";
        return NoteDocument.Create(
        [
            ("type", "profile"),
            ("assistant", NoteDocument.Quote(assistant.Name)),
            ("updated", Now()),
            ("aliases", $"[{NoteDocument.Quote(title)}]"),
            ("tags", $"[local-ai, local-ai/{folder}]"),
        ], title, block);
    }

    // ---------------- Identity ----------------

    /// <summary>
    /// True when the user edited the identity note's generated block (or removed its markers): it then defines who the
    /// assistant is, and a new personality does not reach the replies until the note is edited too.
    /// </summary>
    public async Task<bool> IsIdentityEditedAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        var path = VaultLayout.IdentityPath(folder);
        if (storage.Read(path) is not { } text) return false;
        var note = await index.FindNoteAsync(assistant.Id, path, ct).ConfigureAwait(false);
        return note != null && IsUserOwned(NoteDocument.Parse(text), note);
    }

    /// <summary>
    /// Writes the identity note (who the assistant is) when it is missing or out of date, e.g. after the shortcut
    /// changed. A note whose generated block the user edited keeps their text: it then defines who the assistant is.
    /// Returns true when the file was written.
    /// </summary>
    public async Task<bool> WriteIdentityNoteAsync(long assistantId, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return false;
        await StepAsync("Identity note: checking it is up to date", ct).ConfigureAwait(false);
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        var path = VaultLayout.IdentityPath(folder);
        var block = NoteFormats.RenderIdentity(assistant, Path.GetFileNameWithoutExtension(VaultLayout.ProfilePath(folder)), Headings);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var note = await index.FindNoteAsync(assistant.Id, path, ct).ConfigureAwait(false);
            var current = storage.Read(path);
            var document = current != null ? NoteDocument.Parse(current) : null;
            // Without an index entry (e.g. a rebuilt index) the file's block counts as the app's, like any note.
            if (document != null && IsUserOwned(document, note ?? NewNoteFromFile(assistant.Id, folder, path, current!)))
                return false;

            var title = $"{assistant.Name} - {Headings.Identity}";
            var updated = (document ?? NoteDocument.Create([], title, block)) with { Generated = block };
            foreach (var (key, value) in NoteFormats.IdentityProperties(assistant)) updated = updated.WithFrontmatter(key, value);
            if (document == null)
            {
                updated = updated.WithFrontmatter("aliases", $"[{NoteDocument.Quote(title)}]")
                    .WithFrontmatter("tags", $"[local-ai, local-ai/{folder}]");
            }
            if (updated.Render() == current) return false;

            await StepAsync("Identity note: saving the note", ct).ConfigureAwait(false);

            var text = updated.WithFrontmatter("updated", Now()).Render();
            storage.Write(path, text);
            var saved = await index.SaveNoteAsync(new VaultNote
            {
                Id = note?.Id ?? 0,
                AssistantId = assistant.Id,
                Path = path,
                Kind = NoteKind.Identity,
                GeneratedText = block,
                UpdatedAt = time.GetLocalNow(),
            }, ct).ConfigureAwait(false);
            await IndexAsync(saved, text, ct).ConfigureAwait(false);
            logger.LogInformation("Wrote the identity note of assistant {Id}", assistant.Id);
        }
        finally
        {
            _writeGate.Release();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ---------------- Index ----------------

    /// <summary>
    /// Brings the index in line with the assistant's files: new and edited notes are indexed, notes whose file is
    /// gone leave the index (a deleted conversation note is then written again), and passages embedded with another
    /// model are embedded again.
    /// </summary>
    public async Task ScanAsync(long assistantId, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return;
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        var known = (await index.ListNotesAsync(assistantId, ct).ConfigureAwait(false)).ToDictionary(n => n.Path, StringComparer.Ordinal);
        var stale = embeddings.IsAvailable
            ? await index.NotesEmbeddedWithAnotherModelAsync(assistantId, embeddings.ModelId, ct).ConfigureAwait(false)
            : new HashSet<long>();
        var changed = await ForgetMissingNotesAsync(known.Values, ct).ConfigureAwait(false) > 0;
        await StepAsync("Index: checking the notes for changes", ct).ConfigureAwait(false);
        foreach (var path in storage.ListNotes(folder).Where(p => !VaultLayout.IsLearnPath(p)))
        {
            if (storage.Read(path) is not { } text) continue;
            known.TryGetValue(path, out var note);
            if (note != null && note.FileHash == NoteDocument.Hash(text) && !stale.Contains(note.Id)) continue;
            note ??= await index.SaveNoteAsync(NewNoteFromFile(assistantId, folder, path, text), ct).ConfigureAwait(false);
            await IndexAsync(note, text, ct).ConfigureAwait(false);
            changed = true;
        }
        if (changed) NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes from the index the assistant's notes whose file is gone (deleted in Obsidian, or while the app was
    /// closed), without the AI. Update memory does this first, so a conversation whose note was deleted counts as
    /// needing one and gets it again in the same update. Returns the number of notes removed.
    /// </summary>
    public async Task<int> ForgetMissingNotesAsync(long assistantId, CancellationToken ct) =>
        await ForgetMissingNotesAsync(await index.ListNotesAsync(assistantId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private async Task<int> ForgetMissingNotesAsync(IEnumerable<VaultNote> notes, CancellationToken ct)
    {
        var removed = 0;
        // The learned documents change only with Learn, all at once, even when their files are edited or deleted.
        foreach (var note in notes.Where(n => n.Kind != NoteKind.Document && !storage.Exists(n.Path)).ToList())
        {
            await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
            removed++;
        }
        if (removed > 0)
        {
            IndexChanged();
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    /// <summary>Re-indexes one note after the user changed it (or removes it from the index if it was deleted).</summary>
    public async Task IndexNoteAsync(long assistantId, string path, CancellationToken ct)
    {
        if (VaultLayout.IsLearnPath(path)) return; // learned only with Learn
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return;
        var note = await index.FindNoteAsync(assistantId, path, ct).ConfigureAwait(false);
        var text = storage.Read(path);
        if (text == null)
        {
            if (note != null) await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
            IndexChanged();
        }
        else
        {
            if (note?.FileHash == NoteDocument.Hash(text)) return; // written by the app, already indexed
            var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
            note ??= await index.SaveNoteAsync(NewNoteFromFile(assistantId, folder, path, text), ct).ConfigureAwait(false);
            await IndexAsync(note, text, ct).ConfigureAwait(false);
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The assistant whose folder holds <paramref name="path"/>, if any.</summary>
    public async Task<long?> AssistantOfPathAsync(string path, CancellationToken ct)
    {
        var folder = path.Split('/')[0];
        var all = await assistants.ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(a => VaultLayout.AssistantFolder(a, all) == folder)?.Id;
    }

    private static VaultNote NewNoteFromFile(long assistantId, string folder, string path, string text)
    {
        var document = NoteDocument.Parse(text);
        var kind = path == VaultLayout.ProfilePath(folder) ? NoteKind.Profile
            : path == VaultLayout.IdentityPath(folder) ? NoteKind.Identity
            : NoteKind.Conversation;
        long? conversationId = long.TryParse(document.GetFrontmatter("conversation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
        return new VaultNote
        {
            AssistantId = assistantId,
            Path = path,
            Kind = kind,
            ConversationId = kind == NoteKind.Conversation ? conversationId : null,
            // A note the app did not write (or wrote before the index existed): its block counts as generated, so
            // only later edits count as the user's.
            GeneratedText = document.Generated,
            UpdatedAt = DateTimeOffset.MinValue,
        };
    }

    private async Task IndexAsync(VaultNote note, string text, CancellationToken ct)
    {
        var chunks = Chunk(note, text);
        var name = Path.GetFileNameWithoutExtension(note.Path);
        if (embeddings.IsAvailable)
        {
            for (var i = 0; i < chunks.Count; i += EmbedBatchSize)
            {
                var count = Math.Min(EmbedBatchSize, chunks.Count - i);
                await StepAsync(count == 1
                    ? $"Index: embedding passage {i + 1} of {chunks.Count} ({name})"
                    : $"Index: embedding passages {i + 1}-{i + count} of {chunks.Count} ({name})", ct).ConfigureAwait(false);
                try
                {
                    var texts = chunks.Skip(i).Take(count).Select(c => $"{c.NoteTitle}: {c.Heading}\n{c.Text}").ToList();
                    var vectors = await embeddings.EmbedManyAsync(texts, EmbeddingPurpose.Document, ct).ConfigureAwait(false);
                    for (var k = 0; k < count; k++)
                        chunks[i + k] = chunks[i + k] with { Embedding = vectors[k], EmbeddingModel = embeddings.ModelId };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // a timeout is a failure, not a stop
                catch (Exception ex) { logger.LogWarning(ex, "Embedding note passages failed; they are found by keywords only"); }
            }
        }
        await StepAsync($"Index: saving {chunks.Count} passages to the search index ({name})", ct).ConfigureAwait(false);
        await index.ReplaceChunksAsync(note.Id, chunks, ct).ConfigureAwait(false);
        await index.SaveNoteAsync(note with { FileHash = NoteDocument.Hash(text) }, ct).ConfigureAwait(false);
        IndexChanged();
    }

    /// <summary>The note's sections as passages (frontmatter and markers left out, long sections split at paragraphs).</summary>
    internal static List<NoteChunk> Chunk(VaultNote note, string text)
    {
        var document = NoteDocument.Parse(text);
        var body = document.Before + (document.Generated ?? "") + "\n" + document.After;
        var title = Path.GetFileNameWithoutExtension(note.Path); // what [[links]] point to
        var chunks = new List<NoteChunk>();
        foreach (var section in NoteSections.Parse(body))
        {
            var content = string.Join('\n', section.Body.Split('\n').Where(l => !l.StartsWith("# ", StringComparison.Ordinal))).Trim();
            if (content.Length == 0) continue;
            // A paragraph without blank lines is cut too: a passage longer than the embedding model reads would fail
            // its whole batch.
            foreach (var piece in SplitLong(content).SelectMany(HardSplit))
                chunks.Add(new NoteChunk { NoteId = note.Id, NoteTitle = title, Heading = section.Heading, Text = piece });
        }
        return chunks;
    }

    private static IEnumerable<string> SplitLong(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(paragraph).Append("\n\n");
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    // ---------------- Recall ----------------

    /// <summary>
    /// The active assistant's profile and the passages of its other notes most relevant to <paramref name="query"/>:
    /// meaning (embeddings) and words (full-text search) ranked together, within the memory token budget. Only the
    /// newest <see cref="MemoryOptions.MaxConversationNotes"/> conversation notes are searched.
    /// </summary>
    public async Task<MemoryContext> RecallAsync(string query, CancellationToken ct)
    {
        if (!_options.Enabled || context.Current is not { } assistant) return MemoryContext.Empty;
        var searchText = string.IsNullOrWhiteSpace(query) ? null : SearchQuery(query);
        // The query is embedded while the notes are read.
        var queryVector = searchText != null && embeddings.IsAvailable
            ? EmbedQueryAsync(searchText, ct)
            : Task.FromResult<float[]?>(null);
        try
        {
            var budget = _options.MemoryTokenBudget;
            var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
            var profilePath = VaultLayout.ProfilePath(folder);
            await _activity.StepAsync(WorkKind.Reply, "Memory (RAG): reading the identity and the profile", ct).ConfigureAwait(false);
            var identity = NoteText(storage.Read(VaultLayout.IdentityPath(folder)));
            if (identity?.Length > MaxIdentityChars) identity = identity[..MaxIdentityChars] + "…";
            var profile = NoteText(storage.Read(profilePath));
            if (profile != null)
            {
                var max = (int)(budget * 0.4 * 3.5);
                if (profile.Length > max) profile = profile[..max] + "…";
                budget -= PromptBuilder.EstimateTokens(profile);
            }

            var set = await RecallSetAsync(assistant.Id, ct).ConfigureAwait(false);
            var passages = new List<NoteChunk>();
            var documents = new List<NoteChunk>();
            if (set.Chunks.Count + set.Documents.Count > 0 && searchText != null)
            {
                await _activity.StepAsync(WorkKind.Reply,
                    $"Memory (RAG): searching {set.Chunks.Count + set.Documents.Count} passages by meaning and words", ct).ConfigureAwait(false);
                var vector = await queryVector.ConfigureAwait(false);
                // The learned documents and the conversation notes each get half of what is left: neither crowds out the other.
                var share = budget / 2;
                var left = Take(await RankAsync(assistant.Id, searchText, vector, set.Documents, documents: true, ct).ConfigureAwait(false),
                    _options.RetrieveTopKDocuments, share, documents);
                Take(await RankAsync(assistant.Id, searchText, vector, set.Chunks, documents: false, ct).ConfigureAwait(false),
                    _options.RetrieveTopKChunks, budget - share + left, passages);
            }
            if (profile != null || passages.Count + documents.Count > 0)
                logger.LogInformation("Recalled the profile ({Profile}), {Count} note passages and {Documents} document passages",
                    profile != null, passages.Count, documents.Count);
            return new MemoryContext(profile, passages, identity, documents);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // a timeout is a failure, not a stop
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory recall failed; continuing without memory");
            return MemoryContext.Empty;
        }
    }

    /// <summary>The best passages that fit: at most <paramref name="max"/> within <paramref name="budget"/> tokens. Returns the budget left.</summary>
    private static int Take(IReadOnlyList<NoteChunk> ranked, int max, int budget, List<NoteChunk> taken)
    {
        foreach (var chunk in ranked)
        {
            if (taken.Count >= max) break;
            var cost = PromptBuilder.EstimateTokens(chunk.Text) + 8;
            if (cost > budget) continue;
            budget -= cost;
            taken.Add(chunk);
        }
        return budget;
    }

    /// <summary>
    /// The active assistant's conversations whose note recall searches (the conversation list marks them). Empty when
    /// the memory is off.
    /// </summary>
    public async Task<IReadOnlySet<long>> ConversationsInMemoryAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || context.Current is not { } assistant) return new HashSet<long>();
        try
        {
            return (await RecallSetAsync(assistant.Id, ct).ConfigureAwait(false)).Conversations;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // a timeout is a failure, not a stop
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read which conversations are in memory");
            return new HashSet<long>();
        }
    }

    /// <summary>
    /// The passages recall searches: the user's own notes and the newest conversation notes (profile and identity are
    /// in the prompt already). Read from the index once, then kept until the index changes.
    /// </summary>
    private async Task<RecallSet> RecallSetAsync(long assistantId, CancellationToken ct)
    {
        long version;
        lock (_recallGate)
        {
            if (_recallSets.TryGetValue(assistantId, out var cached)) return cached;
            version = _indexVersion;
        }

        var notes = await index.ListNotesAsync(assistantId, ct).ConfigureAwait(false);
        var conversationNotes = notes
            .Where(n => n is { Kind: NoteKind.Conversation, ConversationId: not null })
            .OrderByDescending(n => n.SourceUpdatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(n => n.ConversationId)
            .Take(Math.Max(0, _options.MaxConversationNotes))
            .ToList();
        var searched = notes.Where(n => n is { Kind: NoteKind.Conversation, ConversationId: null }).Select(n => n.Id)
            .Concat(conversationNotes.Select(n => n.Id))
            .ToHashSet();
        var documentNotes = notes.Where(n => n.Kind == NoteKind.Document).Select(n => n.Id).ToHashSet();
        // Vectors of length 1: comparing the question with a passage is then one dot product (RankAsync).
        var all = (await index.ListChunksAsync(assistantId, ct).ConfigureAwait(false))
            .Select(c => c.Embedding == null ? c : c with { Embedding = VectorMath.Normalize(c.Embedding) })
            .ToList();
        var chunks = all.Where(c => searched.Contains(c.NoteId)).ToList();
        var withPassages = chunks.Select(c => c.NoteId).ToHashSet();
        var set = new RecallSet(chunks, conversationNotes.Where(n => withPassages.Contains(n.Id)).Select(n => n.ConversationId!.Value).ToHashSet(),
            all.Where(c => documentNotes.Contains(c.NoteId)).ToList());

        lock (_recallGate)
        {
            if (_indexVersion == version) _recallSets[assistantId] = set;
        }
        return set;
    }

    /// <summary>The index changed: recall reads it again.</summary>
    private void IndexChanged()
    {
        lock (_recallGate)
        {
            _indexVersion++;
            _recallSets.Clear();
        }
    }

    private async Task<float[]?> EmbedQueryAsync(string query, CancellationToken ct)
    {
        try
        {
            return await embeddings.EmbedAsync(query, EmbeddingPurpose.Query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // a timeout is a failure, not a stop
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Semantic note search failed; using words only");
            return null;
        }
    }

    /// <summary>A note's content for the prompt (profile, identity): the generated block and the user's own text, without frontmatter.</summary>
    private static string? NoteText(string? text)
    {
        if (text == null) return null;
        var document = NoteDocument.Parse(text);
        // Only the note's title goes: a "# " heading the user wrote in the text (e.g. in the style) stays.
        var lines = (document.Before + (document.Generated ?? "") + "\n" + document.After).Split('\n').ToList();
        var title = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (title >= 0) lines.RemoveAt(title);
        var body = string.Join('\n', lines).Trim();
        return body.Length == 0 ? null : body;
    }

    /// <summary>The query as searched: at most <see cref="MaxQueryChars"/>, its start and its end.</summary>
    internal static string SearchQuery(string query)
    {
        if (query.Length <= MaxQueryChars) return query;
        var half = MaxQueryChars / 2;
        return query[..half] + "\n...\n" + query[^half..];
    }

    /// <summary>
    /// The words of <paramref name="query"/> worth searching for: common words of every language ("the", "que",
    /// "para"...) match almost every passage, so they are left out.
    /// </summary>
    internal string Keywords(string query)
    {
        var stop = _stopWords ??= languages.Languages.Values
            .SelectMany(l => l.CommonWords)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', Word().Matches(query)
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !stop.Contains(w.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Reciprocal rank fusion of the semantic and the full-text ranking.</summary>
    private async Task<IReadOnlyList<NoteChunk>> RankAsync(long assistantId, string query, float[]? queryVector,
        IReadOnlyList<NoteChunk> chunks, bool documents, CancellationToken ct)
    {
        const double k = 60;
        if (chunks.Count == 0) return [];
        var byId = chunks.ToDictionary(c => c.Id);
        var scores = new Dictionary<long, double>();
        if (queryVector != null)
        {
            var question = VectorMath.Normalize(queryVector);
            var semantic = chunks
                .Where(c => c.Embedding != null && c.EmbeddingModel == embeddings.ModelId)
                .Select(c => (c.Id, Score: VectorMath.Dot(question, c.Embedding)))
                .Where(s => s.Score >= _options.MinRelevance)
                .OrderByDescending(s => s.Score)
                .Take(50)
                .ToList();
            for (var i = 0; i < semantic.Count; i++) scores[semantic[i].Id] = 1 / (k + i + 1);
        }
        var keywords = Keywords(query);
        if (keywords.Length > 0)
        {
            // Only passages recall searches count, each at its place among them (the profile, the identity and
            // conversation notes too old to be searched match too, and would push the others down).
            var words = (await index.SearchChunksAsync(assistantId, keywords, KeywordMatches, documents, ct).ConfigureAwait(false))
                .Where(byId.ContainsKey).ToList();
            for (var i = 0; i < words.Count; i++) scores[words[i]] = scores.GetValueOrDefault(words[i]) + 1 / (k + i + 1);
        }

        return scores.OrderByDescending(s => s.Value).Where(s => byId.ContainsKey(s.Key)).Select(s => byId[s.Key]).ToList();
    }

    // ---------------- Learn ----------------

    private readonly SemaphoreSlim _learnGate = new(1, 1);
    private volatile bool _learning;

    /// <summary>Learn or Update memory is running: replies wait for it (the chat is blocked).</summary>
    public bool IsLearning => _learning;

    /// <summary>Raised when Learn or Update memory starts and ends.</summary>
    public event EventHandler? LearningChanged;

    /// <summary>
    /// Starts work that blocks the chat (Learn, Update memory); null when such work already runs. Disposing ends it.
    /// </summary>
    public IDisposable? TryBeginExclusiveWork()
    {
        if (!_learnGate.Wait(0)) return null;
        _learning = true;
        LearningChanged?.Invoke(this, EventArgs.Empty);
        return new ExclusiveWork(this);
    }

    private sealed class ExclusiveWork(MemoryVault vault) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;
            vault._learning = false;
            vault._learnGate.Release();
            vault.LearningChanged?.Invoke(vault, EventArgs.Empty);
        }
    }

    /// <summary>Learn needs the embedding model, which runs while an AI is on.</summary>
    public bool CanLearn => embeddings.IsAvailable;

    /// <summary>The assistant's learn folder (relative), created when missing.</summary>
    public async Task<string> LearnFolderAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        var folder = VaultLayout.LearnFolder(await AssistantFolderAsync(assistant, ct).ConfigureAwait(false));
        storage.CreateFolder(folder);
        return folder;
    }

    /// <summary>The learn folder's files (subfolders included) and what the last Learn indexed of them.</summary>
    public async Task<LearnStatus> LearnStatusAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        var folder = await LearnFolderAsync(assistant, ct).ConfigureAwait(false);
        var files = storage.ListFiles(folder, VaultLayout.LearnExtensions);
        var learned = (await index.ListNotesAsync(assistant.Id, ct).ConfigureAwait(false)).Where(n => n.Kind == NoteKind.Document).ToList();
        var passages = learned.Count == 0 ? 0 : await index.CountDocumentChunksAsync(assistant.Id, ct).ConfigureAwait(false);
        // Changed: a file was added, removed or edited since the last Learn. Only a file modified after it is read
        // (a learn folder can hold thousands of files).
        var learnedNotes = learned.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var changed = files.Count != learned.Count
                      || files.Any(f => !learnedNotes.TryGetValue(f.Path, out var note)
                                        || (f.Modified > note.UpdatedAt && (storage.Read(f.Path) is not { } text || NoteDocument.Hash(text) != note.FileHash)));
        return new LearnStatus(files, learned.Count == 0 ? null : learned.Max(n => n.UpdatedAt), learned.Count, passages,
            changed && (files.Count > 0 || learned.Count > 0));
    }

    /// <summary>The path a file named <paramref name="fileName"/> gets in the learn folder (the name as it is, without folders).</summary>
    public async Task<string> LearnPathAsync(AssistantProfile assistant, string fileName, CancellationToken ct = default) =>
        $"{await LearnFolderAsync(assistant, ct).ConfigureAwait(false)}/{Path.GetFileName(fileName)}";

    /// <summary>Copies a text file into the learn folder (replacing a file of the same name). It is learned at the next Learn.</summary>
    public async Task AddLearnFileAsync(AssistantProfile assistant, string fileName, string text, CancellationToken ct = default)
    {
        if (!VaultLayout.LearnExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only .md and .txt files can be learned.", nameof(fileName));
        storage.Write(await LearnPathAsync(assistant, fileName, ct).ConfigureAwait(false), text);
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Learns the assistant's learn folder again from scratch: every file is split into passages and embedded, then
    /// all of it replaces what was learned before in one step. If anything fails, what was learned before stays.
    /// Needs the embedding model (an AI on). Replies wait while it runs.
    /// </summary>
    public async Task<int> LearnAsync(AssistantProfile assistant, IProgress<(int Done, int Total)>? progress, CancellationToken ct = default)
    {
        if (!embeddings.IsAvailable) throw new InvalidOperationException("Turn the AI on to learn.");
        using var work = TryBeginExclusiveWork() ?? throw new InvalidOperationException("The assistant is already learning.");
        {
            var folder = await LearnFolderAsync(assistant, ct).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var documents = new List<(VaultNote Note, List<NoteChunk> Chunks)>();
            foreach (var file in storage.ListFiles(folder, VaultLayout.LearnExtensions))
            {
                if (storage.Read(file.Path) is not { } text) continue;
                var note = new VaultNote { AssistantId = assistant.Id, Path = file.Path, Kind = NoteKind.Document, FileHash = NoteDocument.Hash(text), UpdatedAt = now };
                documents.Add((note, ChunkDocument(note, text)));
            }
            var total = documents.Sum(d => d.Chunks.Count);
            progress?.Report((0, total));

            var done = 0;
            foreach (var (_, chunks) in documents)
            {
                for (var i = 0; i < chunks.Count; i += EmbedBatchSize)
                {
                    var count = Math.Min(EmbedBatchSize, chunks.Count - i);
                    var texts = chunks.Skip(i).Take(count).Select(c => $"{c.NoteTitle}: {c.Heading}\n{c.Text}").ToList();
                    var vectors = await embeddings.EmbedManyAsync(texts, EmbeddingPurpose.Document, ct).ConfigureAwait(false);
                    for (var k = 0; k < count; k++)
                        chunks[i + k] = chunks[i + k] with { Embedding = vectors[k], EmbeddingModel = embeddings.ModelId };
                    done += count;
                    progress?.Report((done, total));
                }
            }

            await index.ReplaceDocumentsAsync(assistant.Id, documents.Select(d => new LearnedDocument(d.Note, d.Chunks)).ToList(), ct)
                .ConfigureAwait(false);
            IndexChanged();
            logger.LogInformation("Assistant {Id} learned {Files} documents ({Passages} passages)", assistant.Id, documents.Count, total);
            NotesChanged?.Invoke(this, EventArgs.Empty);
            return total;
        }
    }

    /// <summary>
    /// A learned document's passages: a Markdown file by its sections (like a note), a text file by its paragraphs. Long
    /// paragraphs are cut at lines, then at words, so no passage is longer than the embedding model reads.
    /// </summary>
    internal static List<NoteChunk> ChunkDocument(VaultNote note, string text)
    {
        var title = VaultLayout.PassageTitle(note.Path);
        var sections = Path.GetExtension(note.Path).Equals(".md", StringComparison.OrdinalIgnoreCase)
            ? Chunk(note, text).Select(c => (c.Heading, c.Text))
            : [("", text.ReplaceLineEndings("\n").Trim())];
        var chunks = new List<NoteChunk>();
        foreach (var (heading, body) in sections)
        {
            foreach (var piece in SplitLong(body).SelectMany(HardSplit))
            {
                if (piece.Trim().Length > 0)
                    chunks.Add(new NoteChunk { NoteId = note.Id, NoteTitle = title, Heading = heading, Text = piece.Trim() });
            }
        }
        return chunks;
    }

    /// <summary>A piece still longer than a passage (a paragraph without blank lines): cut at lines, then at spaces.</summary>
    private static IEnumerable<string> HardSplit(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }
        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split([' ', '\n']))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > MaxChunkChars)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word.Length > MaxChunkChars ? word[..MaxChunkChars] : word);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    // ---------------- Removal ----------------

    /// <summary>Deletes a conversation's note (file and index), when the conversation is deleted in the app.</summary>
    public async Task DeleteConversationNoteAsync(long conversationId, CancellationToken ct = default)
    {
        if (await index.FindConversationNoteAsync(conversationId, ct).ConfigureAwait(false) is not { } note) return;
        storage.Delete(note.Path);
        await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
        IndexChanged();
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the assistant's whole folder of notes. Call before the assistant itself is deleted.</summary>
    public async Task DeleteAssistantNotesAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        storage.DeleteFolder(await AssistantFolderAsync(assistant, ct).ConfigureAwait(false));
        foreach (var note in await index.ListNotesAsync(assistant.Id, ct).ConfigureAwait(false))
            await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
        IndexChanged();
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The active assistant's notes, newest first (the Memory page).</summary>
    public async Task<IReadOnlyList<VaultNote>> ListNotesAsync(CancellationToken ct = default) =>
        context.Current is { } a
            ? (await index.ListNotesAsync(a.Id, ct).ConfigureAwait(false))
                .OrderBy(n => n.Kind).ThenByDescending(n => n.Path, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// What an assistant knows about the user, for display: its profile note without the title, frontmatter and
    /// markers. Null before the first profile is written.
    /// </summary>
    public async Task<string?> ReadProfileAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        var folder = await AssistantFolderAsync(assistant, ct).ConfigureAwait(false);
        if (storage.Read(VaultLayout.ProfilePath(folder)) is not { } text) return null;
        var document = NoteDocument.Parse(text);
        var lines = (document.Before + (document.Generated ?? "") + "\n" + document.After).Split('\n').ToList();
        var heading = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (heading >= 0) lines.RemoveAt(heading);
        var body = string.Join('\n', lines).Trim();
        return body.Length > 0 ? body : null;
    }

    /// <summary>Absolute path of a note file.</summary>
    public string FullPath(string path) => Path.Combine(storage.Root, path.Replace('/', Path.DirectorySeparatorChar));
}
