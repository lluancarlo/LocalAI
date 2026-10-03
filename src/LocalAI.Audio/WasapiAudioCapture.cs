using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalAI.Audio;

/// <summary>
/// WASAPI shared-mode microphone capture, converted to 16 kHz mono float. When echo cancellation is enabled the stream
/// is opened in Windows communications mode with the output device as AEC reference, so the assistant's own speech is
/// removed from the signal where the driver supports it. Audio stays in memory only; it is never written to disk or logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture(IOptionsMonitor<LocalAiOptions> options, ILogger<WasapiAudioCapture> logger) : IAudioCapture
{
    public const int TargetSampleRate = 16000;

    public IAudioCaptureSession Start(string? deviceId)
    {
        var audio = options.CurrentValue.Audio;
        Exception? lastError = null;
        // Most capable configuration first; degrade if the endpoint does not support it.
        var modes = audio.EchoCancellation
            ? new[] { CaptureMode.EchoCancelled, CaptureMode.Communications, CaptureMode.Plain }
            : new[] { CaptureMode.Plain };
        foreach (var mode in modes)
        {
            try
            {
                return new Session(deviceId, NullIfEmpty(audio.OutputDeviceId), mode, logger);
            }
            catch (AudioDeviceException) { throw; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                lastError = ex;
                logger.LogWarning("Microphone mode {Mode} unavailable: {Error}", mode, ex.Message);
            }
        }
        throw new AudioDeviceException($"Could not open the microphone: {lastError?.Message}", lastError);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal enum CaptureMode { EchoCancelled, Communications, Plain }

    private sealed class Session : IAudioCaptureSession
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly MMDevice _device;
        private readonly MMDevice? _reference;
        private readonly WasapiRecorder _recorder;
        private readonly Channel<float[]> _frames = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private StreamingResampler? _resampler;
        private WaveFormat? _format;
        private readonly ILogger _logger;
        private int _disposed;

        public Session(string? deviceId, string? outputDeviceId, CaptureMode mode, ILogger logger)
        {
            _logger = logger;
            _device = WasapiAudioDeviceProvider.OpenDevice(_enumerator, deviceId, DataFlow.Capture);
            try
            {
                DeviceName = _device.FriendlyName;
                var builder = new WasapiRecorderBuilder().WithDevice(_device).WithSharedMode().WithEventSync().WithBufferLength(20);
                if (mode != CaptureMode.Plain) builder = builder.WithCommunicationsMode();
                if (mode == CaptureMode.EchoCancelled)
                {
                    _reference = WasapiAudioDeviceProvider.OpenDevice(_enumerator, outputDeviceId, DataFlow.Render);
                    builder = builder.WithEchoCancellationReferenceEndpoint(_reference);
                }
                _recorder = builder.Build();
                _recorder.DataAvailable += OnData;
                _recorder.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) _logger.LogError(e.Exception, "Microphone capture stopped with an error");
                    _frames.Writer.TryComplete(e.Exception);
                };
                _recorder.StartRecording();
                _format = _recorder.WaveFormat;
                _resampler = new StreamingResampler(_format.SampleRate, TargetSampleRate);
            }
            catch
            {
                _reference?.Dispose();
                _device.Dispose();
                _enumerator.Dispose();
                throw;
            }
            Mode = mode;
            _logger.LogInformation("Microphone opened: {Device} ({Rate} Hz, {Channels} ch, {Bits}-bit {Encoding}, mode {Mode})",
                DeviceName, _format!.SampleRate, _format.Channels, _format.BitsPerSample, _format.Encoding, mode);
        }

        public ChannelReader<float[]> Frames => _frames.Reader;
        public string DeviceName { get; }
        public CaptureMode Mode { get; }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            // _format/_resampler are assigned right after StartRecording; ignore any packet that races ahead of that.
            var resampler = _resampler;
            var format = _format;
            if (buffer.IsEmpty || resampler is null || format is null) return;
            var mono = AudioConversion.ToMono(buffer, format);
            var resampled = resampler.Process(mono);
            if (resampled.Length > 0) _frames.Writer.TryWrite(resampled);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _recorder.StopRecording(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            _recorder.DataAvailable -= OnData;
            await _recorder.DisposeAsync().ConfigureAwait(false);
            _reference?.Dispose();
            _device.Dispose();
            _enumerator.Dispose();
            _frames.Writer.TryComplete();
            _logger.LogInformation("Microphone closed");
        }
    }
}

internal static class AudioConversion
{
    /// <summary>Interleaved PCM/float (16/24/32-bit) → mono float.</summary>
    public static float[] ToMono(ReadOnlySpan<byte> data, WaveFormat format)
    {
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        var isFloat = format.BitsPerSample == 32 &&
                      (format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));

        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var offset = (i * channels + c) * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => MemoryMarshal.Read<float>(data[offset..]),
                    4 => MemoryMarshal.Read<int>(data[offset..]) / 2147483648f,
                    3 => ((data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) >> 8) / 8388608f,
                    2 => MemoryMarshal.Read<short>(data[offset..]) / 32768f,
                    _ => 0f,
                };
            }
            mono[i] = sum / channels;
        }
        return mono;
    }
}
