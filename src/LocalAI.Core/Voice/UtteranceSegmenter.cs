using LocalAI.Configuration;

namespace LocalAI.Core.Voice;

public enum SegmenterEvent { None, SpeechStarted, UtteranceCompleted, UtteranceDiscarded }

/// <summary>
/// Endpointing state machine on top of a frame-level VAD decision. Keeps a pre-roll so speech onset is not clipped,
/// confirms speech after <see cref="VoiceOptions.MinSpeechMs"/>, and completes the utterance after
/// <see cref="VoiceOptions.EndOfSpeechSilenceMs"/> of silence (or at the maximum length).
/// </summary>
public sealed class UtteranceSegmenter
{
    private enum Phase { Silence, Candidate, Speech }

    private readonly int _preRollSamples;
    private readonly int _minSpeechSamples;
    private readonly int _endSilenceSamples;
    private readonly int _maxSamples;
    private readonly int _keepTrailingSamples;

    private readonly Queue<float[]> _preRoll = new();
    private int _preRollCount;
    private readonly List<float> _utterance = [];
    private Phase _phase = Phase.Silence;
    private int _speechSamples;
    private int _silenceRun;
    private float[]? _completed;

    public UtteranceSegmenter(VoiceOptions options, int sampleRate = 16000)
    {
        _preRollSamples = options.PreRollMs * sampleRate / 1000;
        _minSpeechSamples = options.MinSpeechMs * sampleRate / 1000;
        _endSilenceSamples = options.EndOfSpeechSilenceMs * sampleRate / 1000;
        _maxSamples = options.MaxUtteranceSeconds * sampleRate;
        _keepTrailingSamples = Math.Min(_endSilenceSamples, 200 * sampleRate / 1000);
    }

    public bool InSpeech => _phase == Phase.Speech;

    public SegmenterEvent Process(float[] frame, bool isSpeech)
    {
        switch (_phase)
        {
            case Phase.Silence:
                if (!isSpeech)
                {
                    PushPreRoll(frame);
                    return SegmenterEvent.None;
                }
                _utterance.Clear();
                foreach (var f in _preRoll) _utterance.AddRange(f);
                ClearPreRoll();
                _utterance.AddRange(frame);
                _speechSamples = frame.Length;
                _silenceRun = 0;
                _phase = Phase.Candidate;
                return CheckConfirmed();

            case Phase.Candidate:
                _utterance.AddRange(frame);
                if (isSpeech)
                {
                    _speechSamples += frame.Length;
                    _silenceRun = 0;
                    return CheckConfirmed();
                }
                _silenceRun += frame.Length;
                if (_silenceRun >= _endSilenceSamples)
                {
                    // Too short to be speech (click, cough): drop it, keep the tail as pre-roll.
                    ResetToSilence();
                    return SegmenterEvent.UtteranceDiscarded;
                }
                return SegmenterEvent.None;

            case Phase.Speech:
                _utterance.AddRange(frame);
                if (isSpeech) { _speechSamples += frame.Length; _silenceRun = 0; }
                else _silenceRun += frame.Length;

                if (_silenceRun >= _endSilenceSamples || _utterance.Count >= _maxSamples)
                {
                    var trim = Math.Max(0, _silenceRun - _keepTrailingSamples);
                    _completed = _utterance.GetRange(0, _utterance.Count - trim).ToArray();
                    ResetToSilence();
                    return SegmenterEvent.UtteranceCompleted;
                }
                return SegmenterEvent.None;
        }
        return SegmenterEvent.None;
    }

    /// <summary>Returns the last completed utterance once.</summary>
    public float[]? TakeUtterance()
    {
        var u = _completed;
        _completed = null;
        return u;
    }

    /// <summary>
    /// Barge-in: the user is speaking now. Keep only the most recent audio (the interrupting speech plus pre-roll),
    /// dropping any assistant echo collected earlier, and treat it as confirmed speech.
    /// </summary>
    public void BeginSpeechFromRecent(int recentSamples)
    {
        var recent = new List<float>();
        foreach (var f in _preRoll) recent.AddRange(f);
        recent.AddRange(_utterance);
        var keep = Math.Min(recent.Count, recentSamples + _preRollSamples);
        _utterance.Clear();
        _utterance.AddRange(recent.GetRange(recent.Count - keep, keep));
        ClearPreRoll();
        _phase = Phase.Speech;
        _speechSamples = Math.Max(_speechSamples, _minSpeechSamples);
        _silenceRun = 0;
    }

    public void Reset()
    {
        ResetToSilence();
        ClearPreRoll();
        _completed = null;
    }

    private SegmenterEvent CheckConfirmed()
    {
        if (_phase == Phase.Candidate && _speechSamples >= _minSpeechSamples)
        {
            _phase = Phase.Speech;
            return SegmenterEvent.SpeechStarted;
        }
        return SegmenterEvent.None;
    }

    private void ResetToSilence()
    {
        _phase = Phase.Silence;
        _utterance.Clear();
        _speechSamples = 0;
        _silenceRun = 0;
    }

    private void PushPreRoll(float[] frame)
    {
        _preRoll.Enqueue(frame);
        _preRollCount += frame.Length;
        while (_preRoll.Count > 0 && _preRollCount - _preRoll.Peek().Length >= _preRollSamples)
            _preRollCount -= _preRoll.Dequeue().Length;
    }

    private void ClearPreRoll()
    {
        _preRoll.Clear();
        _preRollCount = 0;
    }
}
