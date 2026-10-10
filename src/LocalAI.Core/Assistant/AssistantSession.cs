using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalAI.Configuration;
using LocalAI.Core.Conversations;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Assistant;

/// <param name="SelectedText">Text the user selected in another application, sent as context (Read selection).</param>
public sealed record TurnRequest(string Text, InputSource Source, string? Language = null, bool Speak = false, string? SelectedText = null);

public enum TurnOutcome { Completed, Cancelled, Failed }

public sealed record TurnResult(TurnOutcome Outcome, StoredMessage? AssistantMessage, GenerationStats? Stats, string? Error);

public sealed class AssistantDeltaEventArgs(long conversationId, string delta) : EventArgs
{
    public long ConversationId { get; } = conversationId;
    public string Delta { get; } = delta;
}

/// <summary>
/// The conversation orchestrator. Text and voice input both go through <see cref="SubmitAsync"/>:
/// persist user message → recall memories → build prompt → stream LLM (→ optional speech) → persist reply →
/// extract memories in the background. Only one turn runs at a time; a new turn or <see cref="CancelCurrentTurn"/>
/// stops generation, synthesis and playback.
/// </summary>
public sealed class AssistantSession : IDisposable
{
    private readonly ILanguageModel _llm;
    private readonly IConversationStore _store;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILanguageDetector _languageDetector;
    private readonly MemoryService _memory;
    private readonly SpeechOutput _speech;
    private readonly LocalAiOptions _options;
    private readonly ILogger<AssistantSession> _logger;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _turnCts;
    private string? _lastUserLanguage;

    public AssistantSession(
        ILanguageModel llm,
        IConversationStore store,
        PromptBuilder promptBuilder,
        ILanguageDetector languageDetector,
        MemoryService memory,
        SpeechOutput speech,
        IOptions<LocalAiOptions> options,
        ILogger<AssistantSession> logger)
    {
        _llm = llm;
        _store = store;
        _promptBuilder = promptBuilder;
        _languageDetector = languageDetector;
        _memory = memory;
        _speech = speech;
        _options = options.Value;
        _logger = logger;
    }

    public long? CurrentConversationId { get; private set; }
    public bool IsBusy { get; private set; }

    public event EventHandler<long?>? ConversationChanged;
    public event EventHandler<Conversation>? ConversationCreated;
    public event EventHandler<StoredMessage>? UserMessageAdded;
    public event EventHandler<long>? AssistantMessageStarted;
    public event EventHandler<AssistantDeltaEventArgs>? AssistantDelta;
    public event EventHandler<TurnResult>? TurnCompleted;
    public event EventHandler<bool>? BusyChanged;

    public SpeechOutput Speech => _speech;

    /// <summary>Switches to an existing conversation (null = start a new one on next message).</summary>
    public void SelectConversation(long? conversationId)
    {
        CancelCurrentTurn();
        CurrentConversationId = conversationId;
        ConversationChanged?.Invoke(this, conversationId);
    }

    /// <summary>Stops the in-flight turn: LLM generation, TTS and playback.</summary>
    public void CancelCurrentTurn()
    {
        try { _turnCts?.Cancel(); } catch (ObjectDisposedException) { }
        _speech.StopAll();
    }

    public async Task<TurnResult> SubmitAsync(TurnRequest request, CancellationToken cancellationToken = default)
    {
        var text = request.Text.Trim();
        if (text.Length == 0) return new TurnResult(TurnOutcome.Cancelled, null, null, null);

        CancelCurrentTurn();
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _turnCts = turnCts;
        var ct = turnCts.Token;
        SetBusy(true);
        try
        {
            return await RunTurnAsync(request with { Text = text }, ct).ConfigureAwait(false);
        }
        finally
        {
            _turnCts = null;
            SetBusy(false);
            _turnGate.Release();
        }
    }

    private async Task<TurnResult> RunTurnAsync(TurnRequest request, CancellationToken ct)
    {
        if (_llm.State != LanguageModelState.Ready)
        {
            var reason = _llm.LastError ?? "The language model is not loaded.";
            var failed = new TurnResult(TurnOutcome.Failed, null, null, reason);
            TurnCompleted?.Invoke(this, failed);
            return failed;
        }

        // The user's language is stored with the message (Whisper's detection for voice, heuristic for text). The reply
        // language is the assistant's, set in the prompt. Uncertain detections fall back to the previous turn's language.
        var language = request.Language;
        if (language == null)
        {
            var detected = _languageDetector.Detect(request.Text);
            language = (detected.Confidence >= 0.3f ? detected.Language : null) ?? _lastUserLanguage ?? detected.Language;
        }
        if (language != null) _lastUserLanguage = language;

        var conversationId = await EnsureConversationAsync(request.Text, ct).ConfigureAwait(false);
        var history = await _store.GetRecentMessagesAsync(conversationId, 60, ct).ConfigureAwait(false);

        var userMessage = await _store.AddMessageAsync(new StoredMessage
        {
            ConversationId = conversationId,
            Role = ChatRole.User,
            Content = request.Text,
            CreatedAt = DateTimeOffset.Now,
            Language = language,
            Source = request.Source,
            SelectedText = string.IsNullOrWhiteSpace(request.SelectedText) ? null : request.SelectedText,
        }, CancellationToken.None).ConfigureAwait(false);
        UserMessageAdded?.Invoke(this, userMessage);

        var memories = await _memory.RecallAsync(request.Text, ct).ConfigureAwait(false);
        var speak = request.Speak && _speech.IsAvailable;
        var prompt = _promptBuilder.Build(history, request.Text, memories, speak, DateTimeOffset.Now, userMessage.SelectedText);

        var options = new GenerationOptions
        {
            Temperature = _options.Llm.Temperature,
            TopP = _options.Llm.TopP,
            MaxTokens = _options.Llm.MaxTokens,
        };

        AssistantMessageStarted?.Invoke(this, conversationId);
        var reply = new StringBuilder();
        GenerationStats? stats = null;
        var outcome = TurnOutcome.Completed;
        string? error = null;
        var utterance = speak ? _speech.Begin(ct) : null;
        var sw = Stopwatch.StartNew();

        try
        {
            await foreach (var chunk in _llm.StreamAsync(prompt, options, ct).ConfigureAwait(false))
            {
                if (chunk.Stats != null) stats = chunk.Stats;
                if (chunk.Text.Length == 0) continue;
                reply.Append(chunk.Text);
                utterance?.Append(chunk.Text);
                AssistantDelta?.Invoke(this, new AssistantDeltaEventArgs(conversationId, chunk.Text));
            }
            utterance?.Complete();
            if (utterance != null) await utterance.Completion.ConfigureAwait(false);
            if (ct.IsCancellationRequested) outcome = TurnOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            outcome = TurnOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = TurnOutcome.Failed;
            error = ex.Message;
            _logger.LogError(ex, "Generation failed");
        }
        finally
        {
            if (outcome != TurnOutcome.Completed) _speech.StopAll();
        }

        _logger.LogInformation(
            "Turn {Outcome}: {Tokens} tokens, TTFT {Ttft:F0} ms, {Tps:F1} tok/s, total {Total:F0} ms, memories {Memories}",
            outcome, stats?.GeneratedTokens, stats?.TimeToFirstTokenMs, stats?.TokensPerSecond, sw.Elapsed.TotalMilliseconds, memories.Count);

        StoredMessage? assistantMessage = null;
        if (reply.Length > 0)
        {
            assistantMessage = await _store.AddMessageAsync(new StoredMessage
            {
                ConversationId = conversationId,
                Role = ChatRole.Assistant,
                Content = reply.ToString(),
                CreatedAt = DateTimeOffset.Now,
                Language = language,
                Model = _llm.Info?.Id,
                Source = request.Source,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    interrupted = outcome != TurnOutcome.Completed,
                    spoken = speak,
                    stats,
                    memoriesUsed = memories.Select(m => m.Memory.Id).ToArray(),
                }),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        var result = new TurnResult(outcome, assistantMessage, stats, error);
        TurnCompleted?.Invoke(this, result);

        if (outcome == TurnOutcome.Completed && assistantMessage != null)
            _ = ExtractMemoriesInBackgroundAsync(conversationId, request.Text, assistantMessage.Content);

        return result;
    }

    private async Task ExtractMemoriesInBackgroundAsync(long conversationId, string userText, string reply)
    {
        try
        {
            await _memory.ExtractAndStoreAsync(conversationId, userText, reply, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background memory extraction failed");
        }
    }

    private async Task<long> EnsureConversationAsync(string firstMessage, CancellationToken ct)
    {
        if (CurrentConversationId is { } id && await _store.GetAsync(id, ct).ConfigureAwait(false) != null)
            return id;

        var conversation = await _store.CreateAsync(MakeTitle(firstMessage), ct).ConfigureAwait(false);
        CurrentConversationId = conversation.Id;
        ConversationCreated?.Invoke(this, conversation);
        ConversationChanged?.Invoke(this, conversation.Id);
        return conversation.Id;
    }

    internal static string MakeTitle(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "New conversation";
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "...";
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        BusyChanged?.Invoke(this, busy);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _turnGate.Dispose();
    }
}
