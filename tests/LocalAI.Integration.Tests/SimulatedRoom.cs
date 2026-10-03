using System.Diagnostics;
using System.Threading.Channels;
using LocalAI.Audio;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;

namespace LocalAI.Integration.Tests;

/// <summary>
/// A virtual room acting as both microphone and speaker, driven by one real-time clock (20 ms ticks).
/// Each tick "plays" 20 ms of queued assistant audio and produces a mic frame =
/// <see cref="EchoGain"/> × played audio (speaker→mic echo) + scripted user speech. This exercises VAD, endpointing,
/// echo gating and barge-in with real models, without physical audio devices.
/// </summary>
public sealed class SimulatedRoom : IAudioCapture, IAudioPlayer
{
    private const int Rate = 16000;
    private const int TickSamples = Rate / 50;

    private readonly Lock _gate = new();
    private readonly Queue<float> _playback = new();
    private readonly Queue<float> _user = new();
    private readonly Queue<float> _recentBlockRms = new(); // one entry per 20 ms tick, last 250 ms
    private Channel<float[]>? _mic;
    private TaskCompletionSource _drained = Done();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _clock;

    public SimulatedRoom()
    {
        _clock = Task.Run(RunClockAsync);
    }

    public float EchoGain { get; set; } = 0.3f;
    public int ClipsPlayed { get; private set; }
    public int StopCount { get; private set; }

    /// <summary>Schedules user speech (16 kHz mono) starting now.</summary>
    public void Say(float[] samples16k)
    {
        lock (_gate) foreach (var s in samples16k) _user.Enqueue(s);
    }

    public void SaySilence(TimeSpan duration) => Say(new float[(int)(duration.TotalSeconds * Rate)]);

    // ---- IAudioCapture ----
    public IAudioCaptureSession Start(string? deviceId)
    {
        lock (_gate)
        {
            _mic = Channel.CreateUnbounded<float[]>();
            return new Session(this, _mic);
        }
    }

    private sealed class Session(SimulatedRoom room, Channel<float[]> channel) : IAudioCaptureSession
    {
        public ChannelReader<float[]> Frames => channel.Reader;
        public string DeviceName => "Simulated microphone";
        public ValueTask DisposeAsync()
        {
            lock (room._gate) { if (room._mic == channel) room._mic = null; }
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    // ---- IAudioPlayer ----
    public void Enqueue(AudioClip clip)
    {
        var samples = StreamingResampler.Convert(clip.Samples, clip.SampleRate, Rate);
        lock (_gate)
        {
            foreach (var s in samples) _playback.Enqueue(s);
            if (_drained.Task.IsCompleted) _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ClipsPlayed++;
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return _drained.Task.WaitAsync(cancellationToken);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_playback.Count > 0) StopCount++;
            _playback.Clear();
            _drained.TrySetResult();
        }
    }

    public bool IsPlaying { get { lock (_gate) return _playback.Count > 0; } }

    public float RecentOutputRms
    {
        // Same semantics as WasapiAudioPlayer: peak 20 ms-block RMS over the last 250 ms.
        get { lock (_gate) return _recentBlockRms.Count == 0 ? 0 : _recentBlockRms.Max(); }
    }

    public void SetDevice(string? deviceId) { }

    private async Task RunClockAsync()
    {
        var sw = Stopwatch.StartNew();
        long tick = 0;
        while (!_cts.IsCancellationRequested)
        {
            var frame = new float[TickSamples];
            lock (_gate)
            {
                var wasPlaying = _playback.Count > 0;
                double sumSq = 0;
                for (var i = 0; i < TickSamples; i++)
                {
                    var played = _playback.Count > 0 ? _playback.Dequeue() : 0f;
                    var user = _user.Count > 0 ? _user.Dequeue() : 0f;
                    sumSq += played * played;
                    frame[i] = Math.Clamp(EchoGain * played + user + Noise(), -1f, 1f);
                }
                _recentBlockRms.Enqueue((float)Math.Sqrt(sumSq / TickSamples));
                while (_recentBlockRms.Count > 12) _recentBlockRms.Dequeue();
                if (wasPlaying && _playback.Count == 0) _drained.TrySetResult();
                _mic?.Writer.TryWrite(frame);
            }
            tick++;
            var due = TimeSpan.FromMilliseconds(tick * 20) - sw.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due).ConfigureAwait(false);
        }
    }

    private static float Noise() => (Random.Shared.NextSingle() - 0.5f) * 0.002f; // faint room noise

    private static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource();
        t.SetResult();
        return t;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _clock.Wait(1000); } catch (AggregateException) { }
    }
}
