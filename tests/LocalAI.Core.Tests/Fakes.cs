using System.Runtime.CompilerServices;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

internal sealed class FakeLanguageModel : ILanguageModel
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public Func<IReadOnlyList<ChatMessage>, IEnumerable<string>> Responder { get; set; } = _ => ["Hello", " there", "."];
    public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;
    public LanguageModelState State { get; set; } = LanguageModelState.Ready;
    public ModelInfo? Info { get; } = new() { Id = "fake", DisplayName = "Fake", FilePath = "fake.gguf" };
    public string? LastError { get; set; }
    public event EventHandler<LanguageModelState>? StateChanged;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        State = LanguageModelState.Ready;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        State = LanguageModelState.NotLoaded;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Requests) Requests.Add(messages);
        foreach (var t in Responder(messages))
        {
            if (TokenDelay > TimeSpan.Zero) await Task.Delay(TokenDelay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LlmChunk(t);
        }
        yield return new LlmChunk("", new GenerationStats { GeneratedTokens = 3, TokensPerSecond = 100 });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly List<Conversation> _conversations = [];
    private readonly List<StoredMessage> _messages = [];
    private long _nextId = 1;

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default)
    {
        var c = new Conversation(_nextId++, title, DateTimeOffset.Now, DateTimeOffset.Now);
        lock (_conversations) _conversations.Add(c);
        return Task.FromResult(c);
    }

    public Task<Conversation?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(_conversations.FirstOrDefault(c => c.Id == id));

    public Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Conversation>>(_conversations.ToList());

    public Task RenameAsync(long id, string title, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        _conversations.RemoveAll(c => c.Id == id);
        _messages.RemoveAll(m => m.ConversationId == id);
        return Task.CompletedTask;
    }

    public Task<StoredMessage> AddMessageAsync(StoredMessage message, CancellationToken ct = default)
    {
        var m = message with { Id = _nextId++ };
        lock (_messages) _messages.Add(m);
        return Task.FromResult(m);
    }

    public Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredMessage>>(_messages.Where(m => m.ConversationId == conversationId).ToList());

    public Task<IReadOnlyList<StoredMessage>> GetRecentMessagesAsync(long conversationId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredMessage>>(_messages.Where(m => m.ConversationId == conversationId).TakeLast(limit).ToList());

    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationSearchHit>>([]);
}

internal sealed class InMemoryMemoryStore : IMemoryStore
{
    public List<MemoryItem> Items { get; } = [];
    private long _next = 1;

    public Task<MemoryItem> AddAsync(MemoryItem item, CancellationToken ct = default)
    {
        var m = item with { Id = _next++ };
        Items.Add(m);
        return Task.FromResult(m);
    }

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryItem>>(Items.ToList());
    public Task DeleteAsync(long id, CancellationToken ct = default) { Items.RemoveAll(i => i.Id == id); return Task.CompletedTask; }
    public Task UpdateEmbeddingAsync(long id, float[] embedding, string model, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoMemoryRetriever : IMemoryRetriever
{
    public List<ScoredMemory> Results { get; } = [];
    public Task<IReadOnlyList<ScoredMemory>> RetrieveAsync(string query, int topK, float minScore, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScoredMemory>>(Results.ToList());
}

/// <summary>Bag-of-words "embedding" over a tiny vocabulary: enough to test de-duplication deterministically.</summary>
internal sealed class FakeEmbeddings : IEmbeddingService
{
    private static readonly string[] Vocabulary = ["c#", "prefers", "python", "coffee", "rome", "user", "software", "likes"];
    public bool IsAvailable { get; set; } = true;
    public string ModelId => "fake-embed";

    public Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        var lower = text.ToLowerInvariant();
        return Task.FromResult(Vocabulary.Select(w => lower.Contains(w) ? 1f : 0f).ToArray());
    }
}

internal sealed class FakeTts : ITextToSpeech
{
    public List<string> Spoken { get; } = [];
    public ComponentState State { get; set; } = ComponentState.Ready;
    public string? LastError => null;
    public VoiceInfo? Voice { get; private set; } = new("v", "en", "Voice");
    public IReadOnlyList<VoiceInfo> AvailableVoices => Voice == null ? [] : [Voice];
    public VoiceStyle Style { get; set; } = VoiceStyle.Default;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        Voice = new VoiceInfo(voiceId, "en", voiceId);
        return Task.CompletedTask;
    }

    public void RemoveVoice(string voiceId)
    {
        if (Voice?.Id == voiceId) Voice = null;
    }

    public Task<AudioClip> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (Spoken) Spoken.Add(text);
        return Task.FromResult(new AudioClip(new float[1600], 16000));
    }

    public Task<AudioClip> SynthesizeAsync(string text, string voiceId, VoiceStyle style, CancellationToken cancellationToken = default) =>
        SynthesizeAsync(text, cancellationToken);

    public void Dispose() { }
}

internal sealed class FakePlayer : IAudioPlayer
{
    public int Enqueued;
    public int Stops;
    public void Enqueue(AudioClip clip) => Interlocked.Increment(ref Enqueued);
    public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Stop() => Interlocked.Increment(ref Stops);
    public bool IsPlaying => false;
    public float RecentOutputRms => 0;
    public void SetDevice(string? deviceId) { }
    public void Dispose() { }
}

internal static class TestOptions
{
    public static IOptions<LocalAiOptions> Create(Action<LocalAiOptions>? configure = null)
    {
        var o = new LocalAiOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }
}
