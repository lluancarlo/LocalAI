using System.Diagnostics;
using System.Threading.Channels;
using LocalAI.Core.Audio;
using LocalAI.Core.Language;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Voice;

/// <summary>
/// Streams text to speech: LLM tokens → sentence chunks → TTS → queued playback. Synthesis of sentence N+1 overlaps
/// playback of sentence N. Cancelling the token stops synthesis and playback immediately.
/// </summary>
public sealed class SpeechOutput
{
    private readonly ITextToSpeech _tts;
    private readonly IAudioPlayer _player;
    private readonly ILanguageDetector _languageDetector;
    private readonly ILogger _logger;

    public SpeechOutput(ITextToSpeech tts, IAudioPlayer player, ILanguageDetector languageDetector, ILogger<SpeechOutput> logger)
    {
        _tts = tts;
        _player = player;
        _languageDetector = languageDetector;
        _logger = logger;
    }

    public bool IsAvailable => _tts.State == ComponentState.Ready;
    public IAudioPlayer Player => _player;

    /// <summary>Raised when the first audio of an utterance is queued for playback (latency = time since Begin).</summary>
    public event EventHandler<TimeSpan>? SpeakingStarted;

    public SpeechUtterance Begin(string? language, CancellationToken cancellationToken) =>
        new(this, language, cancellationToken);

    public void StopAll() => _player.Stop();

    public sealed class SpeechUtterance
    {
        private readonly SpeechOutput _owner;
        private readonly SentenceChunker _chunker = new();
        private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        private readonly CancellationToken _ct;
        private readonly Stopwatch _sinceBegin = Stopwatch.StartNew();
        private string? _language;

        internal SpeechUtterance(SpeechOutput owner, string? language, CancellationToken ct)
        {
            _owner = owner;
            _language = language;
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
                    var lang = ChooseLanguage(sentence);
                    AudioClip clip;
                    try
                    {
                        clip = await _owner._tts.SynthesizeAsync(sentence, lang, _ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _owner._logger.LogError(ex, "TTS synthesis failed; continuing without audio for this sentence");
                        continue;
                    }
                    _ct.ThrowIfCancellationRequested();
                    if (clip.Samples.Length == 0) continue;
                    _owner._player.Enqueue(clip);
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

        private string? ChooseLanguage(string sentence)
        {
            // The reply is normally in the user's language; switch only when a sentence is clearly in another one.
            var d = _owner._languageDetector.Detect(sentence);
            if (d.IsConfident && d.Confidence >= 0.75f && d.Language != _language) _language = d.Language;
            return _language;
        }
    }
}
