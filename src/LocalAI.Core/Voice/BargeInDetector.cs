using LocalAI.Configuration;

namespace LocalAI.Core.Voice;

/// <summary>
/// Decides whether the user is talking over the assistant. With speakers (no headset), the microphone also hears the
/// assistant, which the VAD classifies as speech. Windows AEC removes most of it when available; this detector is the
/// second line of defence: it learns the speaker→microphone coupling (mic RMS / peak output RMS) while the assistant
/// talks and only accepts frames clearly louder than that echo. With headphones the coupling stays near zero and
/// plain VAD decides.
/// </summary>
/// <remarks>
/// Measured on synthetic speech: echo-only frames have a ratio capped at the coupling (the reference is the peak block
/// RMS), while user speech over echo spreads widely because speech contains many quiet frames. Hence: the threshold
/// sits a small margin above a high percentile of the echo ratio, and the trigger counts clearly-user frames within a
/// sliding window instead of requiring consecutive frames.
/// </remarks>
public sealed class BargeInDetector
{
    private const float PlaybackActiveRms = 0.003f;
    private const float InitialCoupling = 0.25f;
    private const float AdaptUp = 0.05f;
    private const float AdaptDown = 0.002f;
    private const float MicNoiseFloorRms = 0.002f;
    private const int OnsetResetFrames = 16; // ~0.5 s without user frames ends a speech stretch

    private readonly float _margin;
    private readonly int _requiredFrames;
    private readonly bool[] _window;
    private int _windowPos;
    private int _windowCount;
    private float _coupling = InitialCoupling;
    private int _quietFrames = OnsetResetFrames;

    public BargeInDetector(VoiceOptions options, int frameMs)
    {
        _margin = Math.Max(1.05f, options.BargeInEchoMargin);
        _requiredFrames = Math.Max(1, options.BargeInMinSpeechMs / Math.Max(1, frameMs));
        _window = new bool[_requiredFrames * 8 / 3]; // need ≥ 37.5% clearly-user frames in the window
    }

    public float Coupling => _coupling;

    /// <summary>
    /// Frames since the current stretch of user speech began (valid right after <see cref="Update"/> returns true).
    /// Pauses shorter than ~0.5 s do not reset it.
    /// </summary>
    public int FramesSinceOnset { get; private set; }

    /// <summary>Filters a VAD decision through the echo model. Returns true when the frame is likely the user's voice.</summary>
    public bool Gate(bool vadSpeech, float micRms, float outputRms)
    {
        if (outputRms < PlaybackActiveRms) return vadSpeech;

        var ratio = micRms / outputRms;
        var echoLike = ratio < _coupling * _margin;
        if (echoLike && micRms > MicNoiseFloorRms)
        {
            // Track a high percentile of the echo ratio (faster up than down) so the quiet gaps between the
            // assistant's words do not drag the threshold down.
            var rate = ratio > _coupling ? AdaptUp : AdaptDown;
            _coupling = Math.Clamp(_coupling + rate * (ratio - _coupling), 0.001f, 4f);
        }
        return vadSpeech && !echoLike;
    }

    /// <summary>Feeds one gated decision; returns true once enough user speech accumulated in the window.</summary>
    public bool Update(bool gatedSpeech)
    {
        if (gatedSpeech)
        {
            if (_quietFrames >= OnsetResetFrames) FramesSinceOnset = 0;
            _quietFrames = 0;
        }
        else
        {
            _quietFrames++;
        }
        if (_quietFrames < OnsetResetFrames) FramesSinceOnset++;

        if (_window[_windowPos]) _windowCount--;
        _window[_windowPos] = gatedSpeech;
        if (gatedSpeech) _windowCount++;
        _windowPos = (_windowPos + 1) % _window.Length;

        if (_windowCount < _requiredFrames) return false;
        ClearWindow();
        return true;
    }

    public void ResetTrigger()
    {
        ClearWindow();
        FramesSinceOnset = 0;
        _quietFrames = OnsetResetFrames;
    }

    private void ClearWindow()
    {
        Array.Clear(_window);
        _windowCount = 0;
    }
}
