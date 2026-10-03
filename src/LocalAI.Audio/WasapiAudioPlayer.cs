using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalAI.Audio;

/// <summary>
/// Gapless queued playback through WASAPI shared mode. The output stream opens on the first clip and closes after a
/// short idle period. Clips are resampled to the device mix rate when queued; <see cref="Stop"/> clears the queue
/// instantly (barge-in).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(3);
    private const int LatencyMs = 60;

    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly Lock _gate = new();
    private readonly Timer _idleTimer;
    private string? _deviceId;
    private WasapiPlayer? _output;
    private QueueProvider? _provider;
    private TaskCompletionSource _drained = CompletedTcs();

    public WasapiAudioPlayer(ILogger<WasapiAudioPlayer> logger, string? deviceId = null)
    {
        _logger = logger;
        _deviceId = deviceId;
        _idleTimer = new Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsPlaying { get { lock (_gate) return _provider is { HasAudio: true }; } }
    public float RecentOutputRms => _provider?.RecentRms ?? 0f;

    public void SetDevice(string? deviceId)
    {
        lock (_gate)
        {
            _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            CloseOutput();
        }
    }

    public void Enqueue(AudioClip clip)
    {
        lock (_gate)
        {
            try
            {
                EnsureOutput();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or AudioDeviceException)
            {
                _logger.LogError(ex, "Audio output unavailable; dropping speech");
                return;
            }
            var samples = StreamingResampler.Convert(clip.Samples, clip.SampleRate, _provider!.SampleRate);
            if (_drained.Task.IsCompleted) _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _provider.Enqueue(samples);
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        Task t;
        lock (_gate) t = _drained.Task;
        return t.WaitAsync(cancellationToken);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _provider?.Clear();
            _drained.TrySetResult();
        }
    }

    private void EnsureOutput()
    {
        if (_output != null) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = WasapiAudioDeviceProvider.OpenDevice(enumerator, _deviceId, DataFlow.Render);
        var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(LatencyMs).Build();
        try
        {
            var mix = player.DeviceMixFormat;
            var provider = new QueueProvider(mix.SampleRate, mix.Channels, OnQueueDrained);
            player.Init(new SampleToWaveProvider(provider));
            player.Play();
            _output = player;
            _provider = provider;
            _logger.LogInformation("Audio output opened: {Device} ({Rate} Hz, {Channels} ch)", player.DeviceFriendlyName, mix.SampleRate, mix.Channels);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private void OnQueueDrained()
    {
        lock (_gate)
        {
            _drained.TrySetResult();
            _idleTimer.Change(IdleClose, Timeout.InfiniteTimeSpan);
        }
    }

    private void CloseIfIdle()
    {
        lock (_gate)
        {
            if (_provider is { HasAudio: false }) CloseOutput();
        }
    }

    private void CloseOutput()
    {
        if (_output == null) return;
        try { _output.Stop(); } catch (COMException) { }
        _output.Dispose();
        _output = null;
        _provider = null;
        _drained.TrySetResult();
    }

    private static TaskCompletionSource CompletedTcs()
    {
        var t = new TaskCompletionSource();
        t.SetResult();
        return t;
    }

    public void Dispose()
    {
        lock (_gate) CloseOutput();
        _idleTimer.Dispose();
    }

    /// <summary>Endless provider: plays queued mono clips (duplicated to all channels), silence when empty.</summary>
    private sealed class QueueProvider(int sampleRate, int channels, Action onDrained) : ISampleProvider
    {
        private readonly Lock _lock = new();
        private readonly Queue<float[]> _clips = new();
        private float[]? _current;
        private int _position;
        private readonly Queue<(long Ticks, float Rms)> _levels = new();

        public int SampleRate { get; } = sampleRate;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public bool HasAudio { get { lock (_lock) return _current != null || _clips.Count > 0; } }

        public float RecentRms
        {
            get
            {
                lock (_lock)
                {
                    var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 4; // 250 ms
                    while (_levels.Count > 0 && _levels.Peek().Ticks < cutoff) _levels.Dequeue();
                    return _levels.Count == 0 ? 0 : _levels.Max(l => l.Rms);
                }
            }
        }

        public void Enqueue(float[] samples)
        {
            lock (_lock) _clips.Enqueue(samples);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _clips.Clear();
                _current = null;
                _position = 0;
                _levels.Clear();
            }
        }

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / channels;
            var drainedNow = false;
            double sumSq = 0;
            lock (_lock)
            {
                var wasPlaying = _current != null || _clips.Count > 0;
                for (var f = 0; f < frames; f++)
                {
                    if (_current == null || _position >= _current.Length)
                    {
                        _current = _clips.Count > 0 ? _clips.Dequeue() : null;
                        _position = 0;
                    }
                    var s = _current != null ? _current[_position++] : 0f;
                    sumSq += s * s;
                    for (var c = 0; c < channels; c++) buffer[f * channels + c] = s;
                }
                if (_current != null && _position >= _current.Length && _clips.Count == 0) _current = null;
                if (wasPlaying && _current == null && _clips.Count == 0) drainedNow = true;
                _levels.Enqueue((Stopwatch.GetTimestamp(), frames == 0 ? 0 : (float)Math.Sqrt(sumSq / frames)));
                while (_levels.Count > 64) _levels.Dequeue();
            }
            if (drainedNow) onDrained();
            return frames * channels;
        }
    }
}
