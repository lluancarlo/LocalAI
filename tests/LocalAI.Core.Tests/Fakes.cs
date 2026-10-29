using System.Runtime.CompilerServices;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Tests;

internal sealed class FakeLanguageModel : ILanguageModel
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public List<GenerationOptions> Options { get; } = [];
    /// <summary>What the model thinks before replying, when the request lets it (<see cref="GenerationOptions.Thinking"/>).</summary>
    public IReadOnlyList<string> Thoughts { get; set; } = [];
    public Func<IReadOnlyList<ChatMessage>, IEnumerable<string>> Responder { get; set; } = _ => ["Hello", " there", "."];
    public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;
    public LanguageModelState State { get; set; } = LanguageModelState.Ready;
    public ModelInfo? Info { get; set; } = new() { Id = "fake", DisplayName = "Fake", FilePath = "fake.gguf" };
    /// <summary>When set, loading picks this model id (like the real engine reading the assistant's model from the options).</summary>
    public Func<string>? LoadsModel { get; set; }
    /// <summary>The context and cache a load starts with (like the real engine reading them from the options).</summary>
    public Func<(int Context, bool Compact)>? LoadsContext { get; set; }
    public string? LastError { get; set; }
    public event EventHandler<LanguageModelState>? StateChanged;

    public int LoadCount { get; private set; }
    /// <summary>Time a load takes; the model id is read at its end, like the real engine.</summary>
    public TimeSpan LoadDelay { get; set; }
    /// <summary>The model ids loaded, in order.</summary>
    public List<string> Loaded { get; } = [];
    /// <summary>Loading fails (e.g. the model is not downloaded): the state becomes Failed.</summary>
    public bool FailLoad { get; set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCount++;
        if (LoadDelay > TimeSpan.Zero) await Task.Delay(LoadDelay, cancellationToken);
        if (FailLoad)
        {
            State = LanguageModelState.Failed;
            StateChanged?.Invoke(this, State);
            return;
        }
        if (LoadsModel != null)
        {
            var (context, compact) = LoadsContext?.Invoke() ?? (0, false);
            Info = new ModelInfo { Id = LoadsModel(), DisplayName = "Fake", FilePath = "fake.gguf", RequestedContextSize = context, CompactCache = compact };
            lock (Loaded) Loaded.Add(Info.Id);
        }
        State = LanguageModelState.Ready;
        StateChanged?.Invoke(this, State);
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        State = LanguageModelState.NotLoaded;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(messages);
            Options.Add(options);
        }
        if (options.Thinking)
        {
            foreach (var thought in Thoughts) yield return new LlmChunk("", Reasoning: thought);
        }
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

/// <summary>Note files in memory, keyed by relative path.</summary>
internal sealed class InMemoryVaultStorage : IVaultStorage
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public string Root => "vault";
    public event EventHandler<string>? Changed;

    public string? Read(string path) => Files.GetValueOrDefault(path);
    public void Write(string path, string text) => Files[path] = text;
    public void Delete(string path) => Files.Remove(path);
    public bool Exists(string path) => Files.ContainsKey(path);
    public IReadOnlyList<string> ListNotes(string folder) =>
        Files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
    public IReadOnlyList<VaultFile> ListFiles(string folder, IReadOnlyCollection<string> extensions) =>
        Files.Where(f => f.Key.StartsWith(folder + "/", StringComparison.Ordinal) && !f.Key[(folder.Length + 1)..].Contains('/') &&
                         extensions.Contains(Path.GetExtension(f.Key), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new VaultFile(f.Key, f.Value.Length, DateTimeOffset.UnixEpoch))
            .ToList();

    public string FullPath(string path) => Root + "/" + path;
    public void CreateFolder(string folder) { }

    public void DeleteFolder(string folder)
    {
        foreach (var key in ListNotes(folder)) Files.Remove(key);
    }

    /// <summary>Simulates the user editing a note in Obsidian.</summary>
    public void Edit(string path, string text)
    {
        Files[path] = text;
        Changed?.Invoke(this, path);
    }
}

/// <summary>The vault index in memory: notes, passages (word search instead of FTS5), the job queue and state.</summary>
internal sealed class InMemoryVaultIndex : IVaultIndex
{
    private readonly Lock _gate = new();
    private long _next = 1;
    public List<VaultNote> Notes { get; } = [];
    public List<NoteChunk> Chunks { get; } = [];
    public List<MemoryJob> Jobs { get; } = [];
    public Dictionary<long, DateTimeOffset> JobTimes { get; } = [];
    public Dictionary<string, string> State { get; } = [];
    /// <summary>Old-memory facts per assistant (taken once by the migration).</summary>
    public Dictionary<long, List<string>> LegacyFacts { get; } = [];
    /// <summary>What <see cref="ConversationsNeedingNotesAsync"/> returns.</summary>
    public List<(long ConversationId, long AssistantId)> NeedingNotes { get; } = [];

    public Task<VaultNote?> FindNoteAsync(long assistantId, string path, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Notes.FirstOrDefault(n => n.AssistantId == assistantId && n.Path == path));
    }

    public Task<VaultNote?> FindConversationNoteAsync(long conversationId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Notes.FirstOrDefault(n => n.ConversationId == conversationId));
    }

    public Task<IReadOnlyList<VaultNote>> ListNotesAsync(long assistantId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<VaultNote>>(Notes.Where(n => n.AssistantId == assistantId).ToList());
    }

    public Task<VaultNote> SaveNoteAsync(VaultNote note, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = Notes.FindIndex(n => (note.Id != 0 && n.Id == note.Id) || (n.AssistantId == note.AssistantId && n.Path == note.Path));
            var saved = note with { Id = existing >= 0 ? Notes[existing].Id : _next++ };
            if (existing >= 0) Notes[existing] = saved;
            else Notes.Add(saved);
            return Task.FromResult(saved);
        }
    }

    public Task DeleteNoteAsync(long noteId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Notes.RemoveAll(n => n.Id == noteId);
            Chunks.RemoveAll(c => c.NoteId == noteId);
        }
        return Task.CompletedTask;
    }

    public Task ReplaceChunksAsync(long noteId, IReadOnlyList<NoteChunk> chunks, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Chunks.RemoveAll(c => c.NoteId == noteId);
            foreach (var c in chunks) Chunks.Add(c with { Id = _next++, NoteId = noteId });
        }
        return Task.CompletedTask;
    }

    /// <summary>Makes the next <see cref="ReplaceDocumentsAsync"/> fail (Learn keeps what it learned before).</summary>
    public bool FailReplaceDocuments { get; set; }

    public Task ReplaceDocumentsAsync(long assistantId, IReadOnlyList<LearnedDocument> documents, CancellationToken ct = default)
    {
        if (FailReplaceDocuments) throw new IOException("The database is busy.");
        lock (_gate)
        {
            var old = Notes.Where(n => n.AssistantId == assistantId && n.Kind == NoteKind.Document).Select(n => n.Id).ToHashSet();
            Notes.RemoveAll(n => old.Contains(n.Id));
            Chunks.RemoveAll(c => old.Contains(c.NoteId));
            foreach (var d in documents)
            {
                var note = d.Note with { Id = _next++ };
                Notes.Add(note);
                foreach (var c in d.Chunks) Chunks.Add(c with { Id = _next++, NoteId = note.Id });
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NoteChunk>> ListChunksAsync(long assistantId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var notes = Notes.Where(n => n.AssistantId == assistantId).Select(n => n.Id).ToHashSet();
            return Task.FromResult<IReadOnlyList<NoteChunk>>(Chunks.Where(c => notes.Contains(c.NoteId)).ToList());
        }
    }

    public async Task<IReadOnlyList<long>> SearchChunksAsync(long assistantId, string query, int limit, CancellationToken ct = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        return (await ListChunksAsync(assistantId, ct))
            .Select(c => (c.Id, Hits: words.Count(w => c.Text.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).Take(limit).Select(x => x.Id).ToList();
    }

    public Task<IReadOnlyList<(long ConversationId, long AssistantId)>> ConversationsNeedingNotesAsync(
        DateTimeOffset since, DateTimeOffset idleBefore, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<(long, long)>>(NeedingNotes.ToList());
    }

    public Task<IReadOnlyList<string>> TakeLegacyFactsAsync(long assistantId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var facts = LegacyFacts.GetValueOrDefault(assistantId) ?? [];
            LegacyFacts.Remove(assistantId);
            return Task.FromResult<IReadOnlyList<string>>(facts);
        }
    }

    public Task EnqueueAsync(MemoryJobKind kind, long assistantId, long? conversationId, string? path, DateTimeOffset notBefore,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var same = Jobs.FirstOrDefault(j => j.Kind == kind && j.AssistantId == assistantId && j.ConversationId == conversationId && j.Path == path);
            if (same != null)
            {
                if (notBefore < JobTimes[same.Id]) JobTimes[same.Id] = notBefore;
                return Task.CompletedTask;
            }
            var job = new MemoryJob(_next++, kind, assistantId, conversationId, path, 0);
            Jobs.Add(job);
            JobTimes[job.Id] = notBefore;
        }
        return Task.CompletedTask;
    }

    public Task<MemoryJob?> NextJobAsync(DateTimeOffset now, CancellationToken ct = default, long? assistantId = null)
    {
        lock (_gate)
            return Task.FromResult(Jobs.Where(j => JobTimes[j.Id] <= now && (assistantId == null || j.AssistantId == assistantId)).OrderBy(j => JobTimes[j.Id]).ThenBy(j => j.Id).FirstOrDefault());
    }

    public Task CompleteJobAsync(long jobId, CancellationToken ct = default)
    {
        lock (_gate) Jobs.RemoveAll(j => j.Id == jobId);
        return Task.CompletedTask;
    }

    public Task PostponeJobAsync(long jobId, DateTimeOffset notBefore, bool failed, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var i = Jobs.FindIndex(j => j.Id == jobId);
            if (i < 0) return Task.CompletedTask;
            Jobs[i] = Jobs[i] with { Attempts = Jobs[i].Attempts + (failed ? 1 : 0) };
            JobTimes[jobId] = notBefore;
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(State.GetValueOrDefault(key));
    }

    public Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        lock (_gate) State[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>A <see cref="MemoryVault"/> over in-memory fakes.</summary>
internal sealed class MemoryHarness
{
    public InMemoryVaultStorage Storage { get; } = new();
    public InMemoryVaultIndex Index { get; } = new();
    public InMemoryAssistantStore Assistants { get; }
    public InMemoryConversationStore Conversations { get; }
    public AssistantContext Context { get; }
    public FakeLanguageModel Llm { get; }
    public FakeEmbeddings Embeddings { get; } = new();
    public Microsoft.Extensions.Options.IOptions<LocalAiOptions> Options { get; }
    public MemoryVault Vault { get; }
    public LocalAI.Core.Assistant.WorkActivity Work { get; }

    public MemoryHarness(FakeLanguageModel? llm = null, InMemoryConversationStore? conversations = null, AssistantContext? context = null,
        InMemoryAssistantStore? assistants = null, Microsoft.Extensions.Options.IOptions<LocalAiOptions>? options = null,
        LocalAI.Core.Assistant.WorkActivity? work = null)
    {
        Work = work ?? new LocalAI.Core.Assistant.WorkActivity();
        Llm = llm ?? new FakeLanguageModel();
        Conversations = conversations ?? new InMemoryConversationStore();
        Context = context ?? new AssistantContext();
        Assistants = assistants ?? new InMemoryAssistantStore();
        Options = options ?? TestOptions.Create();
        Vault = new MemoryVault(Storage, Index, Assistants, Conversations, Llm, Embeddings, Context, LanguageData.LoadDefault(), Options,
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryVault>.Instance, Work);
    }
}

// <summary>Bag-of-words "embedding" over a tiny vocabulary: enough to test de-duplication deterministically.</summary>
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
    /// <summary>The installed voices (the ones the tests' assistants use).</summary>
    public IReadOnlyList<VoiceInfo> AvailableVoices { get; } =
        [new("v", "en", "Voice"), new("voice-a", "it", "A"), new("voice-n", "en", "N"), new("voice-m", "en", "M")];
    public VoiceStyle Style { get; set; } = VoiceStyle.Default;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        Voice = new VoiceInfo(voiceId, "en", voiceId);
        return Task.CompletedTask;
    }

    public int Releases;

    public void Release()
    {
        Voice = null;
        Releases++;
    }

    public int UnusedReleases;

    public void ReleaseUnused() => UnusedReleases++;

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

/// <summary>Speech recognition and embeddings without models: counts how often they were loaded and unloaded.</summary>
internal sealed class FakeAiCompanions : IAiCompanions
{
    public int Starts;
    public int Stops;
    public bool IsRunning { get; private set; }

    /// <summary>Speech recognition is loaded.</summary>
    public bool IsHearing { get; private set; }

    public Task StartAsync(bool hearing, CancellationToken ct = default)
    {
        Starts++;
        IsRunning = true;
        IsHearing = hearing;
        return Task.CompletedTask;
    }

    public Task SetHearingAsync(bool on, CancellationToken ct = default)
    {
        IsHearing = on;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Stops++;
        IsRunning = false;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAssistantStore : IAssistantStore
{
    private readonly List<AssistantProfile> _items = [];

    public Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AssistantProfile>>(_items.ToList());

    public Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default)
    {
        var profile = new AssistantProfile(_items.Count + 1, assistant.Name, assistant.StylePrompt, assistant.ModelId,
            assistant.VoiceId, assistant.Language, voiceStyle, DateTimeOffset.Now, Avatar: assistant.Avatar, Behavior: assistant.Behavior);
        _items.Add(profile);
        return Task.FromResult(profile);
    }

    public Task SetAvatarAsync(long id, string avatar, CancellationToken ct = default)
    {
        var i = _items.FindIndex(a => a.Id == id);
        _items[i] = _items[i] with { Avatar = avatar };
        return Task.CompletedTask;
    }

    public Task SetSensesAsync(long id, bool hearing, bool speaker, CancellationToken ct = default)
    {
        var i = _items.FindIndex(a => a.Id == id);
        _items[i] = _items[i] with { Hearing = hearing, Speaker = speaker };
        return Task.CompletedTask;
    }

    public Task SetBehaviorAsync(long id, AssistantBehavior behavior, CancellationToken ct = default)
    {
        var i = _items.FindIndex(a => a.Id == id);
        _items[i] = _items[i] with { Behavior = behavior };
        return Task.CompletedTask;
    }

    public Task SetHotkeyAsync(long id, HotkeyGesture? hotkey, CancellationToken ct = default)
    {
        var i = _items.FindIndex(a => a.Id == id);
        _items[i] = _items[i] with { Hotkey = hotkey };
        return Task.CompletedTask;
    }

    public Task<AssistantDataSummary> GetDataSummaryAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(new AssistantDataSummary(0, 0, 0));

    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        _items.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }
}

/// <summary>Global shortcuts without Windows: <see cref="Press"/> simulates a key press, <see cref="Taken"/> other apps' shortcuts.</summary>
internal sealed class FakeGlobalHotkeys : IGlobalHotkeys
{
    private readonly Dictionary<HotkeyGesture, Action> _registered = [];

    public HashSet<HotkeyGesture> Taken { get; } = [];
    public IReadOnlyCollection<HotkeyGesture> Registered => _registered.Keys;

    public IDisposable Register(HotkeyGesture gesture, Action pressed)
    {
        if (Taken.Contains(gesture)) throw new HotkeyUnavailableException(gesture, $"{gesture} is already used by another application or by Windows.");
        if (!_registered.TryAdd(gesture, pressed)) throw new HotkeyUnavailableException(gesture, $"{gesture} is registered twice.");
        return new Registration(() => _registered.Remove(gesture));
    }

    /// <summary>True if the shortcut was registered (and its handler ran).</summary>
    public bool Press(HotkeyGesture gesture)
    {
        if (!_registered.TryGetValue(gesture, out var pressed)) return false;
        pressed();
        return true;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeLiveVoice : ILiveVoice
{
    public bool IsContinuous { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>When set, starting fails with this error (e.g. no microphone).</summary>
    public string? FailToStart { get; set; }
    public event EventHandler<VoiceState>? StateChanged;

    public Task StartContinuousAsync()
    {
        if (IsContinuous) return Task.CompletedTask;
        if (FailToStart != null)
        {
            LastError = FailToStart;
            StateChanged?.Invoke(this, VoiceState.Error);
            return Task.CompletedTask;
        }
        IsContinuous = true;
        StateChanged?.Invoke(this, VoiceState.Listening);
        return Task.CompletedTask;
    }

    public Task StopContinuousAsync(bool keepReply = false)
    {
        if (!IsContinuous) return Task.CompletedTask;
        IsContinuous = false;
        StateChanged?.Invoke(this, VoiceState.Ready);
        return Task.CompletedTask;
    }
}
