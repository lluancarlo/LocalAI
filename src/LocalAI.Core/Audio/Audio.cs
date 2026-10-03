using System.Threading.Channels;
using LocalAI.Core.Speech;

namespace LocalAI.Core.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public interface IAudioDeviceProvider
{
    IReadOnlyList<AudioDevice> GetInputDevices();
    IReadOnlyList<AudioDevice> GetOutputDevices();
}

public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Microphone capture producing 16 kHz mono float frames.</summary>
public interface IAudioCapture
{
    /// <summary>Starts capturing. Throws <see cref="AudioDeviceException"/> if the device cannot be opened.</summary>
    IAudioCaptureSession Start(string? deviceId);
}

public interface IAudioCaptureSession : IAsyncDisposable
{
    /// <summary>Frames of 16 kHz mono samples, in capture order. Completed when capture stops or fails.</summary>
    ChannelReader<float[]> Frames { get; }
    string DeviceName { get; }
}

/// <summary>
/// Gapless queued playback. Clips are played in order; <see cref="Stop"/> discards everything immediately (barge-in).
/// </summary>
public interface IAudioPlayer : IDisposable
{
    void Enqueue(AudioClip clip);
    /// <summary>Completes when the queue is empty and the last clip finished playing (or <see cref="Stop"/> was called).</summary>
    Task WaitForDrainAsync(CancellationToken cancellationToken = default);
    void Stop();
    bool IsPlaying { get; }
    /// <summary>Peak RMS of the audio sent to the speaker over the last ~250 ms (used for echo-aware barge-in).</summary>
    float RecentOutputRms { get; }
    /// <summary>Selects the output device (null = default). Takes effect for the next playback.</summary>
    void SetDevice(string? deviceId);
}

/// <summary>Frame-level speech detector. Frames must be <see cref="FrameSize"/> samples at 16 kHz.</summary>
public interface IVoiceActivityDetector : IDisposable
{
    int FrameSize { get; }
    bool IsSpeech(float[] frame);
    void Reset();
}

public interface IVoiceActivityDetectorFactory
{
    bool IsAvailable { get; }
    IVoiceActivityDetector Create();
}

public static class AudioMath
{
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
