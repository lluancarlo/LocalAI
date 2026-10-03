using System.Diagnostics;
using LocalAI.Audio;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>
/// Real audio devices (WASAPI). The microphone test keeps audio in memory only; the playback test plays silence.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class AudioHardwareTests(ITestOutputHelper output)
{
    private sealed class Monitor(LocalAiOptions value) : IOptionsMonitor<LocalAiOptions>
    {
        public LocalAiOptions CurrentValue => value;
        public LocalAiOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LocalAiOptions, string?> listener) => null;
    }

    [Fact]
    public void Devices_are_enumerated()
    {
        var provider = new WasapiAudioDeviceProvider();
        var inputs = provider.GetInputDevices();
        var outputs = provider.GetOutputDevices();
        foreach (var d in inputs) output.WriteLine($"in : {(d.IsDefault ? "*" : " ")} {d.Name}");
        foreach (var d in outputs) output.WriteLine($"out: {(d.IsDefault ? "*" : " ")} {d.Name}");
        Assert.NotEmpty(inputs);
        Assert.NotEmpty(outputs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Microphone_delivers_16khz_frames(bool echoCancellation)
    {
        var options = new LocalAiOptions { Audio = { EchoCancellation = echoCancellation } };
        var capture = new WasapiAudioCapture(new Monitor(options), NullLogger<WasapiAudioCapture>.Instance);
        await using var session = capture.Start(null);
        var samples = 0;
        var peak = 0f;
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var frame in session.Frames.ReadAllAsync(cts.Token))
            {
                samples += frame.Length;
                peak = Math.Max(peak, AudioMath.Rms(frame));
            }
        }
        catch (OperationCanceledException) { }
        var rate = samples / sw.Elapsed.TotalSeconds;
        output.WriteLine($"{session.DeviceName} (AEC requested: {echoCancellation}): {samples} samples in {sw.Elapsed.TotalSeconds:F2}s ≈ {rate:F0} Hz, peak frame RMS {peak:F4}");
        Assert.InRange(rate, ISpeechToText.SampleRate * 0.85, ISpeechToText.SampleRate * 1.15);
    }

    [Fact]
    public async Task Player_opens_device_and_drains()
    {
        using var player = new WasapiAudioPlayer(NullLogger<WasapiAudioPlayer>.Instance);
        var sw = Stopwatch.StartNew();
        player.Enqueue(new AudioClip(new float[11025], 22050)); // 0.5 s of silence
        Assert.True(player.IsPlaying);
        await player.WaitForDrainAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        output.WriteLine($"0.5 s clip drained after {sw.ElapsedMilliseconds} ms");
        Assert.InRange(sw.ElapsedMilliseconds, 350, 2000);

        // Stop must clear immediately.
        player.Enqueue(new AudioClip(new float[22050 * 5], 22050));
        sw.Restart();
        player.Stop();
        await player.WaitForDrainAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.False(player.IsPlaying);
        output.WriteLine($"Stop drained in {sw.ElapsedMilliseconds} ms");
    }
}
