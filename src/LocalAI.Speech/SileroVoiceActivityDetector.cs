using LocalAI.Configuration;
using LocalAI.Core.Audio;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace LocalAI.Speech;

/// <summary>Silero VAD v5 (ONNX, CPU) via sherpa-onnx. Endpointing is done by the Core's UtteranceSegmenter.</summary>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly VoiceActivityDetector _vad;

    internal SileroVoiceActivityDetector(string modelPath, float threshold)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = threshold;
        // Short internal hysteresis: we only use the frame-level decision.
        config.SileroVad.MinSpeechDuration = 0.064f;
        config.SileroVad.MinSilenceDuration = 0.096f;
        config.SileroVad.MaxSpeechDuration = 60f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";
        _vad = new VoiceActivityDetector(config, 60);
    }

    public int FrameSize => 512;

    public bool IsSpeech(float[] frame)
    {
        _vad.AcceptWaveform(frame);
        var speech = _vad.IsSpeechDetected();
        while (!_vad.IsEmpty()) _vad.Pop(); // segments are not used; keep memory bounded
        return speech;
    }

    public void Reset() => _vad.Reset();

    public void Dispose() => _vad.Dispose();
}

public sealed class SileroVoiceActivityDetectorFactory(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog)
    : IVoiceActivityDetectorFactory
{
    private string? ModelPath
    {
        get
        {
            var entry = catalog.Vad.FirstOrDefault();
            if (entry == null) return null;
            var path = Path.Combine(paths.ModelsDirectory, entry.File);
            return File.Exists(path) ? path : null;
        }
    }

    public bool IsAvailable => ModelPath != null;

    public IVoiceActivityDetector Create() =>
        new SileroVoiceActivityDetector(
            ModelPath ?? throw new FileNotFoundException("Voice activity model not installed (Settings > Models)."),
            options.Value.Voice.VadThreshold);
}
