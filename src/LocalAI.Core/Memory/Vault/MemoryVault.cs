using System.Globalization;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Memory.Vault;

/// <summary>
/// What the memory adds to a prompt: the user's profile, the most relevant passages of past notes and the identity
/// note (who the assistant is; null until it is written, then the assistant's style is used instead).
/// </summary>
public sealed record MemoryContext(string? Profile, IReadOnlyList<NoteChunk> Passages, string? Identity = null)
{
    public static readonly MemoryContext Empty = new(null, []);
    public bool IsEmpty => string.IsNullOrWhiteSpace(Profile) && Passages.Count == 0 && string.IsNullOrWhiteSpace(Identity);
}

/// <summary>
/// Each assistant's memory as Markdown notes (its profile of the user and one note per conversation), the source of
/// truth, plus their index for retrieval. The user's edits win: a generated block the user changed is never
/// overwritten, items the user wrote in the profile always survive, and text outside the markers is never touched.
/// Note and conversation text is never logged.
/// </summary>
public sealed class MemoryVault(
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
    /// <summary>Longest identity sent to the model (the style the user writes is at most 2000 characters).</summary>
    private const int MaxIdentityChars = 6000;
    private readonly MemoryOptions _options = options.Value.Memory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Raised after a note was written, re-indexed or removed (the Memory page refreshes).</summary>
    public event EventHandler? NotesChanged;

    private MemoryHeadings Headings => NoteFormats.Headings(languages, _options.NotesLanguage);
    private IReadOnlyList<MemoryHeadings> AnyHeadings => NoteFormats.AllHeadings(languages);

    private static readonly GenerationOptions Background = new() { Temperature = 0.2f, MaxTokens = 800, Background = true };

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
            return false;
        }

        await StepAsync("Conversation note: the model summarizes the conversation", ct).ConfigureAwait(false);
        var raw = await llm.GenerateAsync(NoteFormats.ConversationPrompt(assistant.Name, _options.NotesLanguage, messages), Background, ct)
            .ConfigureAwait(false);
        var summary = NoteFormats.ParseConversation(raw) ?? throw new InvalidDataException("The conversation summary was not valid JSON.");
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
            var title = $"{assistant.Name} - {Headings.Profile}";
            var updated = (document ?? NoteDocument.Create(
            [
                ("type", "profile"),
                ("assistant", NoteDocument.Quote(assistant.Name)),
                ("updated", Now()),
                ("aliases", $"[{NoteDocument.Quote(title)}]"),
                ("tags", $"[local-ai, local-ai/{folder}]"),
            ], title, block)) with { Generated = block };
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

    // ---------------- Identity ----------------

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
            ? (await index.ListChunksAsync(assistantId, ct).ConfigureAwait(false))
                .Where(c => c.EmbeddingModel != embeddings.ModelId).Select(c => c.NoteId).ToHashSet()
            : [];
        var changed = false;

        foreach (var note in known.Values.Where(n => !storage.Exists(n.Path)))
        {
            await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
            changed = true;
        }
        await StepAsync("Index: checking the notes for changes", ct).ConfigureAwait(false);
        foreach (var path in storage.ListNotes(folder))
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

    /// <summary>Re-indexes one note after the user changed it (or removes it from the index if it was deleted).</summary>
    public async Task IndexNoteAsync(long assistantId, string path, CancellationToken ct)
    {
        if (await FindAssistantAsync(assistantId, ct).ConfigureAwait(false) is not { } assistant) return;
        var note = await index.FindNoteAsync(assistantId, path, ct).ConfigureAwait(false);
        var text = storage.Read(path);
        if (text == null)
        {
            if (note != null) await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
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
            for (var i = 0; i < chunks.Count; i++)
            {
                await StepAsync($"Index: embedding passage {i + 1} of {chunks.Count} ({name})", ct).ConfigureAwait(false);
                try
                {
                    var vector = await embeddings.EmbedAsync($"{chunks[i].NoteTitle}: {chunks[i].Heading}\n{chunks[i].Text}",
                        EmbeddingPurpose.Document, ct).ConfigureAwait(false);
                    chunks[i] = chunks[i] with { Embedding = vector, EmbeddingModel = embeddings.ModelId };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Embedding a note passage failed; it is found by keywords only"); }
            }
        }
        await StepAsync($"Index: saving {chunks.Count} passages to the search index ({name})", ct).ConfigureAwait(false);
        await index.ReplaceChunksAsync(note.Id, chunks, ct).ConfigureAwait(false);
        await index.SaveNoteAsync(note with { FileHash = NoteDocument.Hash(text) }, ct).ConfigureAwait(false);
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
            foreach (var piece in SplitLong(content))
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
    /// meaning (embeddings) and words (full-text search) ranked together, within the memory token budget.
    /// </summary>
    public async Task<MemoryContext> RecallAsync(string query, CancellationToken ct)
    {
        if (!_options.Enabled || context.Current is not { } assistant) return MemoryContext.Empty;
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

            // The profile and the identity are in the prompt already: passages come from the other notes.
            var inPrompt = (await index.ListNotesAsync(assistant.Id, ct).ConfigureAwait(false))
                .Where(n => n.Kind != NoteKind.Conversation).Select(n => n.Id).ToHashSet();
            var chunks = (await index.ListChunksAsync(assistant.Id, ct).ConfigureAwait(false)).Where(c => !inPrompt.Contains(c.NoteId)).ToList();
            var passages = new List<NoteChunk>();
            if (chunks.Count > 0 && !string.IsNullOrWhiteSpace(query))
            {
                await _activity.StepAsync(WorkKind.Reply, $"Memory (RAG): searching {chunks.Count} passages by meaning and words", ct)
                    .ConfigureAwait(false);
                foreach (var chunk in await RankAsync(assistant.Id, query, chunks, ct).ConfigureAwait(false))
                {
                    var cost = PromptBuilder.EstimateTokens(chunk.Text) + 8;
                    if (cost > budget) continue;
                    budget -= cost;
                    passages.Add(chunk);
                    if (passages.Count >= _options.RetrieveTopKChunks) break;
                }
            }
            if (profile != null || passages.Count > 0)
                logger.LogInformation("Recalled the profile ({Profile}) and {Count} note passages", profile != null, passages.Count);
            return new MemoryContext(profile, passages, identity);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory recall failed; continuing without memory");
            return MemoryContext.Empty;
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

    /// <summary>Reciprocal rank fusion of the semantic and the full-text ranking.</summary>
    private async Task<IReadOnlyList<NoteChunk>> RankAsync(long assistantId, string query, List<NoteChunk> chunks, CancellationToken ct)
    {
        query = SearchQuery(query);
        const double k = 60;
        var scores = new Dictionary<long, double>();
        if (embeddings.IsAvailable)
        {
            try
            {
                var q = await embeddings.EmbedAsync(query, EmbeddingPurpose.Query, ct).ConfigureAwait(false);
                var semantic = chunks
                    .Where(c => c.Embedding != null && c.EmbeddingModel == embeddings.ModelId)
                    .Select(c => (c.Id, Score: VectorMath.Cosine(q, c.Embedding)))
                    .Where(s => s.Score >= _options.MinRelevance)
                    .OrderByDescending(s => s.Score)
                    .Take(50)
                    .ToList();
                for (var i = 0; i < semantic.Count; i++) scores[semantic[i].Id] = 1 / (k + i + 1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Semantic note search failed; using words only"); }
        }
        var words = await index.SearchChunksAsync(assistantId, query, 50, ct).ConfigureAwait(false);
        for (var i = 0; i < words.Count; i++) scores[words[i]] = scores.GetValueOrDefault(words[i]) + 1 / (k + i + 1);

        var byId = chunks.ToDictionary(c => c.Id);
        return scores.OrderByDescending(s => s.Value).Where(s => byId.ContainsKey(s.Key)).Select(s => byId[s.Key]).ToList();
    }

    // ---------------- Removal ----------------

    /// <summary>Deletes a conversation's note (file and index), when the conversation is deleted in the app.</summary>
    public async Task DeleteConversationNoteAsync(long conversationId, CancellationToken ct = default)
    {
        if (await index.FindConversationNoteAsync(conversationId, ct).ConfigureAwait(false) is not { } note) return;
        storage.Delete(note.Path);
        await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the assistant's whole folder of notes. Call before the assistant itself is deleted.</summary>
    public async Task DeleteAssistantNotesAsync(AssistantProfile assistant, CancellationToken ct = default)
    {
        storage.DeleteFolder(await AssistantFolderAsync(assistant, ct).ConfigureAwait(false));
        foreach (var note in await index.ListNotesAsync(assistant.Id, ct).ConfigureAwait(false))
            await index.DeleteNoteAsync(note.Id, ct).ConfigureAwait(false);
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The active assistant's notes, newest first (the Memory page).</summary>
    public async Task<IReadOnlyList<VaultNote>> ListNotesAsync(CancellationToken ct = default) =>
        context.Current is { } a
            ? (await index.ListNotesAsync(a.Id, ct).ConfigureAwait(false))
                .OrderBy(n => n.Kind).ThenByDescending(n => n.Path, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>A note for display: its title and body, without frontmatter and markers. Null if the file is gone.</summary>
    public (string Title, string Body)? ReadForDisplay(VaultNote note)
    {
        if (storage.Read(note.Path) is not { } text) return null;
        var document = NoteDocument.Parse(text);
        var lines = (document.Before + (document.Generated ?? "") + "\n" + document.After).Split('\n').ToList();
        var heading = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        var title = heading >= 0 ? lines[heading][2..].Trim() : Path.GetFileNameWithoutExtension(note.Path);
        if (heading >= 0) lines.RemoveAt(heading);
        return (title, string.Join('\n', lines).Trim());
    }

    /// <summary>Absolute path of the active assistant's notes folder.</summary>
    public async Task<string?> AssistantFolderPathAsync(CancellationToken ct = default) =>
        context.Current is { } a ? FullPath(await AssistantFolderAsync(a, ct).ConfigureAwait(false)) : null;

    /// <summary>Absolute path of a note file.</summary>
    public string FullPath(string path) => Path.Combine(storage.Root, path.Replace('/', Path.DirectorySeparatorChar));
}
