using System.Diagnostics;
using System.Threading.Channels;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Voice;

/// <summary>
/// Streams text to speech: LLM tokens → sentence chunks → TTS → queued playback. Synthesis of sentence N+1 overlaps
/// playback of sentence N, always with the active voice. Cancelling the token stops synthesis and playback immediately.
/// </summary>
public sealed class SpeechOutput
{
    private readonly ITextToSpeech _tts;
    private readonly IAudioPlayer _player;
    private readonly LanguageData _languages;
    private readonly ILogger _logger;

    public SpeechOutput(ITextToSpeech tts, IAudioPlayer player, LanguageData languages, ILogger<SpeechOutput> logger)
    {
        _tts = tts;
        _player = player;
        _languages = languages;
        _logger = logger;
    }

    private readonly Lock _enqueueGate = new();
    private long _replyClips;

    public bool IsAvailable => _tts.State == ComponentState.Ready;
    public IAudioPlayer Player => _player;

    /// <summary>Clips of spoken replies queued so far: compare two readings to know whether a reply started speaking.</summary>
    public long ReplyClips => Interlocked.Read(ref _replyClips);

    /// <summary>
    /// Plays <paramref name="clip"/> unless a reply clip was queued since <paramref name="replyClips"/> was read: a
    /// phrase that fills the wait must never play after the reply started.
    /// </summary>
    public bool TryPlayBeforeReply(AudioClip clip, long replyClips)
    {
        lock (_enqueueGate)
        {
            if (Interlocked.Read(ref _replyClips) != replyClips) return false;
            _player.Enqueue(clip);
            return true;
        }
    }

    /// <summary>Raised when the first audio of an utterance is queued for playback (latency = time since Begin).</summary>
    public event EventHandler<TimeSpan>? SpeakingStarted;

    public SpeechUtterance Begin(CancellationToken cancellationToken) => new(this, cancellationToken);

    public void StopAll() => _player.Stop();

    public sealed class SpeechUtterance
    {
        private readonly SpeechOutput _owner;
        private readonly SentenceChunker _chunker;
        private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        private readonly CancellationToken _ct;
        private readonly Stopwatch _sinceBegin = Stopwatch.StartNew();

        internal SpeechUtterance(SpeechOutput owner, CancellationToken ct)
        {
            _owner = owner;
            _chunker = new SentenceChunker(owner._languages.Abbreviations);
            _ct = ct;
            Completion = Task.Run(RunAsync, CancellationToken.None);
        }

        /// <summary>Completes after the last sentence finished playing, or on cancellation.</summary>
        public Task Completion { get; }

        public void Append(string text)
        {
            foreach (var s in _chunker.Append(text)) _sentences.Writer.TryWrite(s);
        }

        public void Complete()
        {
            foreach (var s in _chunker.Complete()) _sentences.Writer.TryWrite(s);
            _sentences.Writer.TryComplete();
        }

        private async Task RunAsync()
        {
            var first = true;
            try
            {
                await foreach (var sentence in _sentences.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
                {
                    AudioClip clip;
                    try
                    {
                        clip = await _owner._tts.SynthesizeAsync(sentence, _ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _owner._logger.LogError(ex, "TTS synthesis failed; continuing without audio for this sentence");
                        continue;
                    }
                    _ct.ThrowIfCancellationRequested();
                    if (clip.Samples.Length == 0) continue;
                    lock (_owner._enqueueGate)
                    {
                        _owner._player.Enqueue(clip);
                        Interlocked.Increment(ref _owner._replyClips);
                    }
                    if (first)
                    {
                        first = false;
                        _owner.SpeakingStarted?.Invoke(_owner, _sinceBegin.Elapsed);
                    }
                }
                if (!first) await _owner._player.WaitForDrainAsync(_ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _owner._player.Stop();
            }
            catch (Exception ex)
            {
                _owner._logger.LogError(ex, "Speech output failed");
                _owner._player.Stop();
            }
        }
    }
}
