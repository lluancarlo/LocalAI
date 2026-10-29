using LocalAI.Speech;

namespace LocalAI.Infrastructure.Tests;

public sealed class PitchShifterTests
{
    private const int Rate = 22050;

    private static float[] Sine(double hz) =>
        Enumerable.Range(0, Rate).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    private static double Frequency(float[] samples)
    {
        var middle = samples.AsSpan(Rate / 4, Rate / 2);
        var crossings = 0;
        for (var i = 1; i < middle.Length; i++)
            if (middle[i - 1] < 0 != middle[i] < 0) crossings++;
        return crossings / 2.0 / 0.5;
    }

    [Theory]
    [InlineData(12, 880)]
    [InlineData(-12, 220)]
    [InlineData(7, 659)]
    public void Shifts_pitch_by_semitones_without_changing_duration(double semitones, double expectedHz)
    {
        var shifted = PitchShifter.Shift(Sine(440), Rate, semitones);
        Assert.Equal(Rate, shifted.Length);
        Assert.InRange(Frequency(shifted), expectedHz * 0.95, expectedHz * 1.05);
    }

    [Fact]
    public void Zero_semitones_returns_the_original_audio()
    {
        var samples = Sine(440);
        Assert.Same(samples, PitchShifter.Shift(samples, Rate, 0));
    }
}
