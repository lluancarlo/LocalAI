using LocalAI.Core.Audio;
using LocalAI.Core.Speech;

namespace LocalAI.Infrastructure;

// Used on platforms without an audio backend yet (the WASAPI backend is Windows-only). Text chat keeps working;
// voice features report the microphone as unavailable.

internal sealed class NullAudioDeviceProvider : IAudioDeviceProvider
{
    public IReadOnlyList<AudioDevice> GetInputDevices() => [];
    public IReadOnlyList<AudioDevice> GetOutputDevices() => [];
}

internal sealed class NullAudioCapture : IAudioCapture
{
    public IAudioCaptureSession Start(string? deviceId) =>
        throw new AudioDeviceException("No audio backend is available on this platform.");
}

internal sealed class NullAudioPlayer : IAudioPlayer
{
    public void Enqueue(AudioClip clip) { }
    public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Stop() { }
    public bool IsPlaying => false;
    public float RecentOutputRms => 0;
    public void SetDevice(string? deviceId) { }
    public void Dispose() { }
}
