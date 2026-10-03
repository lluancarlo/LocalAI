using NAudio.Dsp;

namespace LocalAI.Audio;

/// <summary>Band-limited streaming mono resampler (WDL sinc) for converting device rates to/from model rates.</summary>
public sealed class StreamingResampler
{
    private readonly WdlResampler _resampler = new();
    private readonly double _ratio;
    private readonly bool _passthrough;

    public StreamingResampler(int inputRate, int outputRate)
    {
        _passthrough = inputRate == outputRate;
        _ratio = (double)outputRate / inputRate;
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true); // input-driven
        _resampler.SetRates(inputRate, outputRate);
    }

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (_passthrough) return input.ToArray();
        if (input.IsEmpty) return [];
        _resampler.ResamplePrepare(input.Length, 1, out var inBuffer);
        input.CopyTo(inBuffer);
        var output = new float[(int)(input.Length * _ratio) + 64];
        var produced = _resampler.ResampleOut(output, input.Length, output.Length, 1);
        return produced == output.Length ? output : output[..produced];
    }

    /// <summary>One-shot conversion of a complete clip (flushes the filter tail).</summary>
    public static float[] Convert(float[] samples, int inputRate, int outputRate)
    {
        if (inputRate == outputRate) return samples;
        var r = new StreamingResampler(inputRate, outputRate);
        var tailPad = new float[64];
        var a = r.Process(samples);
        var b = r.Process(tailPad);
        var expected = (int)Math.Round(samples.Length * (double)outputRate / inputRate);
        var all = new float[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all.Length > expected ? all[..expected] : all;
    }
}
