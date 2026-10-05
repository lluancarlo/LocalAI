using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Voice;

public enum VoiceState { Off, Ready, Listening, Recording, Transcribing, Thinking, Speaking, Error }

/// <summary>
/// Voice interaction: push-to-talk and continuous (hands-free) conversation with barge-in.
/// One loop consumes microphone frames; VAD + <see cref="UtteranceSegmenter"/> detect utterances, Whisper transcribes
/// them, <see cref="AssistantSession"/> answers and speaks. While the assistant thinks or speaks, the loop keeps
/// listening; sustained user speech (echo-gated by <see cref="BargeInDetector"/>) cancels the turn, and the speech
/// that interrupted becomes the next utterance. In continuous mode the user can also type: the microphone is ignored
/// while text is being typed, and the typed message is answered aloud like a spoken one.
/// </summary>
public sealed class VoiceConversationController : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IVoiceActivityDetectorFactory _vadFactory;
    private readonly ISpeechToText _stt;
    private readonly AssistantSession _session;
    private readonly IOptionsMonitor<LocalAiOptions> _options;
    private readonly LanguageData _languages;
    private readonly ILogger<VoiceConversationController> _logger;
    private readonly Lock _gate = new();

    private IAudioCaptureSession? _captureSession;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _continuous;
    private bool _pttRecording;
    private bool _monitoring;
    private volatile bool _typing;
    private int _discardAudio;
    private readonly List<float> _pttBuffer = [];
    private int _turnGeneration;
    private string? _inputDeviceId;

    public VoiceConversationController(
        IAudioCapture capture,
        IVoiceActivityDetectorFactory vadFactory,
        ISpeechToText stt,
        AssistantSession session,
        IOptionsMonitor<LocalAiOptions> options,
        LanguageData languages,
        ILogger<VoiceConversationController> logger)
    {
        _capture = capture;
        _vadFactory = vadFactory;
        _stt = stt;
        _session = session;
        _options = options;
        _languages = languages;
        _logger = logger;
        _inputDeviceId = NullIfEmpty(options.CurrentValue.Audio.InputDeviceId);
        _session.Speech.SpeakingStarted += (_, latency) =>
        {
            if (State == VoiceState.Thinking) SetState(VoiceState.Speaking);
            _logger.LogInformation("Speech started {Latency:F0} ms after generation began", latency.TotalMilliseconds);
        };
    }

    public VoiceState State { get; private set; } = VoiceState.Off;
    public bool IsContinuous => _continuous;
    public bool IsTyping => _typing;
    public string? LastError { get; private set; }
    public string? ActiveInputDevice => _captureSession?.DeviceName;
    public bool IsCapturing => _captureSession != null;

    public event EventHandler<VoiceState>? StateChanged;
    public event EventHandler<Transcription>? Transcribed;
    public event EventHandler? BargeIn;
    /// <summary>Microphone level in dBFS (peak RMS over ~66 ms), raised ~15×/s while the microphone is open.</summary>
    public event EventHandler<float>? InputLevel;
    /// <summary>Raised when the microphone is opened (true) or closed (false).</summary>
    public event EventHandler<bool>? CaptureChanged;
    /// <summary>Raised when the microphone delivers pure digital silence (typically a muted headset).</summary>
    public event EventHandler<string>? Warning;

    private const float DigitalSilenceRms = 1e-5f;

    private VoiceOptions Voice => _options.CurrentValue.Voice;

    /// <summary>Selects the microphone; an open capture is reopened on the new device.</summary>
    public async Task SetInputDeviceAsync(string? deviceId)
    {
        var id = NullIfEmpty(deviceId);
        if (id == _inputDeviceId) return;
        _inputDeviceId = id;
        await ReopenCaptureAsync().ConfigureAwait(false);
    }

    /// <summary>Reopens the microphone if it is open (device or capture settings changed).</summary>
    public async Task ReopenCaptureAsync()
    {
        if (_captureSession == null) return;
        await CloseCaptureAsync().ConfigureAwait(false);
        await EnsureCaptureAsync().ConfigureAwait(false);
    }

    // ---------- Level monitor (settings: test the microphone without talking to the assistant) ----------

    public async Task StartMonitorAsync()
    {
        _monitoring = true;
        if (!await EnsureCaptureAsync().ConfigureAwait(false)) _monitoring = false;
    }

    public async Task StopMonitorAsync()
    {
        _monitoring = false;
        if (!_continuous && !_pttRecording) await CloseCaptureAsync().ConfigureAwait(false);
    }

    // ---------- Continuous mode ----------

    public async Task StartContinuousAsync()
    {
        if (_continuous) return;
        if (!EnsureSpeechReady()) return;
        _continuous = true;
        if (!await EnsureCaptureAsync().ConfigureAwait(false)) { _continuous = false; return; }
        SetState(VoiceState.Listening);
        _logger.LogInformation("Continuous voice mode started");
    }

    public async Task StopContinuousAsync()
    {
        if (!_continuous) return;
        _continuous = false;
        Interlocked.Increment(ref _turnGeneration);
        _session.CancelCurrentTurn();
        if (!_monitoring) await CloseCaptureAsync().ConfigureAwait(false);
        SetState(VoiceState.Ready);
        _logger.LogInformation("Continuous voice mode stopped");
    }

    // ---------- Push to talk ----------

    public async Task BeginPushToTalkAsync()
    {
        if (_pttRecording) return;
        if (!EnsureSpeechReady()) return;
        Interrupt(); // pressing the mic while the assistant talks interrupts it
        lock (_gate) { _pttBuffer.Clear(); _pttRecording = true; }
        if (!await EnsureCaptureAsync().ConfigureAwait(false)) { _pttRecording = false; return; }
        SetState(VoiceState.Recording);
    }

    public async Task EndPushToTalkAsync()
    {
        if (!_pttRecording) return;
        float[] samples;
        lock (_gate) { _pttRecording = false; samples = _pttBuffer.ToArray(); _pttBuffer.Clear(); }
        if (!_continuous && !_monitoring) await CloseCaptureAsync().ConfigureAwait(false);

        if (samples.Length < ISpeechToText.SampleRate * 3 / 10)
        {
            SetState(IdleState);
            return;
        }
        if (AudioMath.Rms(samples) < DigitalSilenceRms)
        {
            Warning?.Invoke(this, $"No sound from the microphone ({ActiveInputDevice ?? "default device"}). Is it muted?");
            SetState(IdleState);
            return;
        }
        var generation = Interlocked.Increment(ref _turnGeneration);
        _ = ProcessUtteranceAsync(samples, generation);
    }

    // ---------- Typing in continuous mode ----------

    /// <summary>
    /// While the user types, the microphone is ignored (nothing said becomes a message, no barge-in), and a
    /// transcription that has not been submitted yet is dropped.
    /// </summary>
    public void SetTyping(bool typing)
    {
        if (_typing == typing) return;
        _typing = typing;
        if (typing && State == VoiceState.Transcribing) Interrupt();
    }

    /// <summary>
    /// Answers a typed message aloud, as in a spoken conversation. Audio captured before sending is discarded; the
    /// microphone counts again from now on, so the user can interrupt the reply or keep talking.
    /// </summary>
    public async Task SubmitTextAsync(string text, bool speak)
    {
        _typing = false;
        Interlocked.Exchange(ref _discardAudio, 1);
        var generation = Interlocked.Increment(ref _turnGeneration);
        try
        {
            SetState(VoiceState.Thinking);
            await _session.SubmitAsync(new TurnRequest(text, InputSource.Text, Speak: speak)).ConfigureAwait(false);
        }
        finally
        {
            if (generation == Volatile.Read(ref _turnGeneration)) SetState(IdleState);
        }
    }

    /// <summary>Stops the assistant (LLM + TTS + playback) without leaving voice mode.</summary>
    public void Interrupt()
    {
        Interlocked.Increment(ref _turnGeneration);
        _session.CancelCurrentTurn();
        if (State is VoiceState.Thinking or VoiceState.Speaking or VoiceState.Transcribing) SetState(IdleState);
    }

    private VoiceState IdleState => _continuous ? VoiceState.Listening : VoiceState.Ready;

    private bool EnsureSpeechReady()
    {
        if (_stt.State == ComponentState.Ready) return true;
        LastError = _stt.LastError ?? "Speech recognition is not available.";
        SetState(VoiceState.Error);
        return false;
    }

    // ---------- Capture loop ----------

    private async Task<bool> EnsureCaptureAsync()
    {
        if (_captureSession != null) return true;
        try
        {
            _captureSession = _capture.Start(_inputDeviceId);
        }
        catch (Exception ex)
        {
            LastError = $"Microphone unavailable: {ex.Message}";
            _logger.LogError(ex, "Could not open microphone");
            SetState(VoiceState.Error);
            return false;
        }
        _loopCts = new CancellationTokenSource();
        var session = _captureSession;
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => CaptureLoopAsync(session, token), CancellationToken.None);
        CaptureChanged?.Invoke(this, true);
        await Task.Yield();
        return true;
    }

    private async Task CloseCaptureAsync()
    {
        var session = _captureSession;
        _captureSession = null;
        if (_loopCts != null) await _loopCts.CancelAsync().ConfigureAwait(false);
        if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        if (_loopTask != null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
        if (session != null) CaptureChanged?.Invoke(this, false);
    }

    private async Task CaptureLoopAsync(IAudioCaptureSession session, CancellationToken ct)
    {
        using var vad = _vadFactory.IsAvailable ? _vadFactory.Create() : null;
        var frameSize = vad?.FrameSize ?? 512;
        var frameMs = frameSize * 1000 / ISpeechToText.SampleRate;
        var segmenter = new UtteranceSegmenter(Voice);
        var bargeIn = new BargeInDetector(Voice, frameMs);
        var pending = new List<float>(frameSize * 4);
        var player = _session.Speech.Player;
        var wasBusy = false;
        var silentSamples = 0;
        var silenceWarned = false;
        var levelPeak = 0f;
        var levelSamples = 0;
        const int levelWindow = ISpeechToText.SampleRate / 15;

        try
        {
            await foreach (var chunk in session.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                levelPeak = Math.Max(levelPeak, AudioMath.Rms(chunk));
                levelSamples += chunk.Length;
                if (levelSamples >= levelWindow)
                {
                    InputLevel?.Invoke(this, AudioMath.ToDecibels(levelPeak));
                    levelPeak = 0;
                    levelSamples = 0;
                }

                lock (_gate)
                {
                    if (_pttRecording)
                    {
                        if (_pttBuffer.Count < Voice.MaxUtteranceSeconds * ISpeechToText.SampleRate) _pttBuffer.AddRange(chunk);
                        continue;
                    }
                }
                if (!_continuous || vad == null) continue;

                // A muted headset delivers exact zeros: tell the user once instead of silently never hearing them.
                silentSamples = AudioMath.Rms(chunk) < DigitalSilenceRms ? silentSamples + chunk.Length : 0;
                if (!silenceWarned && silentSamples > ISpeechToText.SampleRate * 5)
                {
                    silenceWarned = true;
                    Warning?.Invoke(this, $"No sound from the microphone ({session.DeviceName}) for 5 seconds. Is it muted?");
                }
                else if (silentSamples == 0) silenceWarned = false;

                if (_typing || Interlocked.Exchange(ref _discardAudio, 0) == 1)
                {
                    pending.Clear();
                    segmenter.Reset();
                    bargeIn.ResetTrigger();
                    if (_typing) continue;
                }

                pending.AddRange(chunk);
                while (pending.Count >= frameSize)
                {
                    var frame = pending.GetRange(0, frameSize).ToArray();
                    pending.RemoveRange(0, frameSize);

                    var state = State;
                    var busy = state is VoiceState.Transcribing or VoiceState.Thinking or VoiceState.Speaking;
                    if (wasBusy && !busy && state == VoiceState.Listening)
                    {
                        // Turn finished normally: drop whatever echo the segmenter collected while the assistant talked.
                        segmenter.Reset();
                        bargeIn.ResetTrigger();
                    }
                    wasBusy = busy;

                    var isSpeech = vad.IsSpeech(frame);
                    var gated = bargeIn.Gate(isSpeech, AudioMath.Rms(frame), player.RecentOutputRms);

                    if (busy && Voice.BargeInEnabled && bargeIn.Update(gated))
                    {
                        _logger.LogInformation("Barge-in detected (state {State}, echo coupling {Coupling:F3})", state, bargeIn.Coupling);
                        // Keep only the interrupting speech; earlier audio in the buffer is assistant echo.
                        segmenter.BeginSpeechFromRecent(bargeIn.FramesSinceOnset * frameSize);
                        bargeIn.ResetTrigger();
                        Interrupt();
                        BargeIn?.Invoke(this, EventArgs.Empty);
                        wasBusy = false; // don't reset the segmenter on the next frame: it holds the user's speech
                    }

                    var ev = segmenter.Process(frame, gated);
                    if (ev == SegmenterEvent.UtteranceCompleted)
                    {
                        var utterance = segmenter.TakeUtterance();
                        if (utterance != null && State == VoiceState.Listening)
                        {
                            var generation = Interlocked.Increment(ref _turnGeneration);
                            SetState(VoiceState.Transcribing);
                            _ = ProcessUtteranceAsync(utterance, generation);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice capture loop failed");
            LastError = ex.Message;
            SetState(VoiceState.Error);
        }
    }

    // ---------- Turn processing ----------

    private async Task ProcessUtteranceAsync(float[] samples, int generation)
    {
        try
        {
            SetState(VoiceState.Transcribing);
            var sw = Stopwatch.StartNew();
            var transcription = await _stt.TranscribeAsync(samples).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _turnGeneration)) return;

            _logger.LogInformation("Transcribed {Seconds:F1}s of audio in {Ms:F0} ms (language {Lang} p={P:F2})",
                transcription.AudioDuration.TotalSeconds, sw.Elapsed.TotalMilliseconds, transcription.Language, transcription.LanguageProbability);

            if (TranscriptFilter.IsNoise(transcription.Text, _languages.TranscriptNoise))
            {
                SetState(IdleState);
                return;
            }
            Transcribed?.Invoke(this, transcription);

            SetState(VoiceState.Thinking);
            await _session.SubmitAsync(new TurnRequest(transcription.Text, InputSource.Voice, transcription.Language, Speak: true))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice turn failed");
            LastError = ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _turnGeneration)) SetState(IdleState);
        }
    }

    private void SetState(VoiceState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void MarkReady()
    {
        if (State == VoiceState.Off) SetState(VoiceState.Ready);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public async ValueTask DisposeAsync()
    {
        _continuous = false;
        _pttRecording = false;
        _monitoring = false;
        await CloseCaptureAsync().ConfigureAwait(false);
    }
}

/// <summary>Filters empty transcripts and well-known Whisper hallucinations on silence/noise.</summary>
public static class TranscriptFilter
{
    /// <param name="hallucinations">Lower-case phrases Whisper produces on silence (languages.json).</param>
    public static bool IsNoise(string text, IReadOnlyList<string> hallucinations)
    {
        var t = text.Trim();
        if (!t.Any(char.IsLetter)) return true;
        var lower = t.ToLowerInvariant();
        return hallucinations.Any(lower.Contains) || (lower.StartsWith('[') && lower.EndsWith(']'));
    }
}
