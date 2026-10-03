using System.Text.Json;
using LocalAI.Configuration;
using LocalAI.Core.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Memory;

/// <summary>
/// Long-term memory: extracts durable facts from exchanges with the local LLM, de-duplicates them with embeddings,
/// and recalls relevant ones for new prompts. Every failure degrades to "no memory" rather than breaking chat.
/// </summary>
public sealed class MemoryService(
    IMemoryStore store,
    IMemoryRetriever retriever,
    IEmbeddingService embeddings,
    ILanguageModel llm,
    IOptions<LocalAiOptions> options,
    ILogger<MemoryService> logger)
{
    private readonly MemoryOptions _options = options.Value.Memory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public event EventHandler? MemoriesChanged;

    internal const string ExtractionPrompt =
        "You maintain the long-term memory of a personal assistant. From the exchange below, extract durable facts " +
        "about the USER worth remembering in future conversations: stable preferences, personal details, skills, " +
        "ongoing projects, or anything the user explicitly asks you to remember.\n" +
        "Rules: output ONLY a JSON array of strings. Each string is one short fact in English, third person, " +
        "starting with \"User\" (e.g. \"User prefers C# for software development.\"). Do not include questions, " +
        "one-off requests, general world knowledge, or anything about the assistant. If nothing qualifies, output [].";

    public async Task<IReadOnlyList<ScoredMemory>> RecallAsync(string query, CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            return await retriever.RetrieveAsync(query, _options.RetrieveTopK, _options.MinRelevance, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory retrieval failed; continuing without memories");
            return [];
        }
    }

    /// <summary>Runs extraction on one exchange and stores new facts. Returns the facts stored.</summary>
    public async Task<IReadOnlyList<MemoryItem>> ExtractAndStoreAsync(
        long conversationId, string userText, string assistantText, CancellationToken ct)
    {
        if (!_options.Enabled || !_options.AutoExtract || llm.State != LanguageModelState.Ready) return [];

        var assistantContext = assistantText.Length > 600 ? assistantText[..600] + "…" : assistantText;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ExtractionPrompt),
            new(ChatRole.User, $"USER said:\n{userText}\n\nASSISTANT replied (context only):\n{assistantContext}\n\nJSON array:"),
        };

        string raw;
        try
        {
            raw = await llm.GenerateAsync(messages,
                new GenerationOptions { Temperature = 0.1f, MaxTokens = 200, Background = true }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory extraction failed");
            return [];
        }

        var stored = new List<MemoryItem>();
        foreach (var fact in ParseFacts(raw))
        {
            var item = await RememberAsync(fact, conversationId, ct).ConfigureAwait(false);
            if (item != null) stored.Add(item);
        }
        if (stored.Count > 0) logger.LogInformation("Stored {Count} new long-term memories", stored.Count);
        return stored;
    }

    /// <summary>Stores a fact unless a near-duplicate already exists.</summary>
    public async Task<MemoryItem?> RememberAsync(string fact, long? conversationId, CancellationToken ct)
    {
        fact = fact.Trim();
        if (fact.Length < 4) return null;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            float[]? vector = null;
            if (embeddings.IsAvailable)
            {
                try { vector = await embeddings.EmbedAsync(fact, EmbeddingPurpose.Document, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Embedding failed; storing memory without vector"); }
            }

            var existing = await store.ListAsync(ct).ConfigureAwait(false);
            foreach (var m in existing)
            {
                if (string.Equals(m.Content, fact, StringComparison.OrdinalIgnoreCase)) return null;
                if (vector != null && m.Embedding != null && VectorMath.Cosine(vector, m.Embedding) >= _options.DuplicateThreshold)
                    return null;
            }

            var item = await store.AddAsync(new MemoryItem
            {
                Content = fact,
                CreatedAt = DateTimeOffset.Now,
                SourceConversationId = conversationId,
                Embedding = vector,
                EmbeddingModel = vector != null ? embeddings.ModelId : null,
            }, ct).ConfigureAwait(false);
            MemoriesChanged?.Invoke(this, EventArgs.Empty);
            return item;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await store.DeleteAsync(id, ct).ConfigureAwait(false);
        MemoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    /// <summary>Computes missing embeddings (e.g. memories saved while the embedding model was unavailable).</summary>
    public async Task BackfillEmbeddingsAsync(CancellationToken ct)
    {
        if (!embeddings.IsAvailable) return;
        foreach (var m in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (m.Embedding != null && m.EmbeddingModel == embeddings.ModelId) continue;
            var v = await embeddings.EmbedAsync(m.Content, EmbeddingPurpose.Document, ct).ConfigureAwait(false);
            await store.UpdateEmbeddingAsync(m.Id, v, embeddings.ModelId, ct).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> ParseFacts(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            var facts = JsonSerializer.Deserialize<List<string>>(raw[start..(end + 1)]) ?? [];
            return facts.Select(f => f.Trim()).Where(f => f.Length > 3 && f.Length < 300).Distinct().Take(5).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
