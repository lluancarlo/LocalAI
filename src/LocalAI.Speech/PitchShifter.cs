using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalAI.Speech;

/// <summary>Changes the pitch of synthesized speech without changing its duration.</summary>
public static class PitchShifter
{
    public static float[] Shift(float[] samples, int sampleRate, double semitones)
    {
        if (Math.Abs(semitones) < 0.01 || samples.Length == 0) return samples;

        var shifter = new SmbPitchShiftingSampleProvider(new ArraySampleProvider(samples, sampleRate))
        {
            PitchFactor = (float)Math.Pow(2, semitones / 12),
        };
        var result = new float[samples.Length];
        var read = 0;
        while (read < result.Length)
        {
            var n = shifter.Read(result.AsSpan(read));
            if (n == 0) break;
            read += n;
        }
        return result;
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
    }
}
