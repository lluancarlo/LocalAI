using LocalAI.Configuration;
using LocalAI.Core.Voice;

namespace LocalAI.Core.Tests;

public sealed class UtteranceSegmenterTests
{
    private const int Frame = 512; // 32 ms at 16 kHz
    private static readonly VoiceOptions Options = new() { PreRollMs = 96, MinSpeechMs = 192, EndOfSpeechSilenceMs = 320, MaxUtteranceSeconds = 2 };

    private static float[] F(float value = 0.1f) => Enumerable.Repeat(value, Frame).ToArray();

    private static List<SegmenterEvent> Run(UtteranceSegmenter s, string pattern)
    {
        // pattern: 's' = speech frame, '.' = silence frame
        return pattern.Select(c => s.Process(F(c == 's' ? 0.1f : 0f), c == 's')).ToList();
    }

    [Fact]
    public void Confirms_speech_after_minimum_duration_and_completes_after_silence()
    {
        var s = new UtteranceSegmenter(Options);
        var events = Run(s, "...ssssss..........");
        Assert.Equal(SegmenterEvent.SpeechStarted, events[3 + 5]); // 6th speech frame = 192 ms
        Assert.Contains(SegmenterEvent.UtteranceCompleted, events);
        var utterance = s.TakeUtterance()!;
        // pre-roll (3 frames) + 6 speech frames + ≤200 ms trailing silence kept
        Assert.InRange(utterance.Length, 9 * Frame, 9 * Frame + 3200);
        Assert.Null(s.TakeUtterance());
    }

    [Fact]
    public void Includes_pre_roll_so_onset_is_not_clipped()
    {
        var s = new UtteranceSegmenter(Options);
        Run(s, "..........ssssss..........");
        var u = s.TakeUtterance()!;
        Assert.Equal(0f, u[0]); // starts with pre-roll silence
        Assert.Equal(3 * Frame, Array.FindIndex(u, x => x > 0)); // exactly 96 ms of pre-roll
    }

    [Fact]
    public void Discards_blips_shorter_than_minimum_speech()
    {
        var s = new UtteranceSegmenter(Options);
        var events = Run(s, "..ss..........");
        Assert.Contains(SegmenterEvent.UtteranceDiscarded, events);
        Assert.DoesNotContain(SegmenterEvent.SpeechStarted, events);
        Assert.Null(s.TakeUtterance());
    }

    [Fact]
    public void Short_pauses_inside_an_utterance_do_not_end_it()
    {
        var s = new UtteranceSegmenter(Options);
        var events = Run(s, "ssssss....ssssss..........");
        Assert.Single(events, e => e == SegmenterEvent.UtteranceCompleted);
    }

    [Fact]
    public void Caps_utterance_length()
    {
        var s = new UtteranceSegmenter(Options);
        var events = Run(s, new string('s', 100));
        Assert.Contains(SegmenterEvent.UtteranceCompleted, events);
        Assert.True(s.TakeUtterance()!.Length <= 2 * 16000 + Frame);
    }

    [Fact]
    public void Barge_in_keeps_only_recent_audio()
    {
        var s = new UtteranceSegmenter(Options);
        Run(s, new string('s', 30)); // a long stretch (echo) accumulated
        s.BeginSpeechFromRecent(5 * Frame);
        Run(s, "..........");
        var u = s.TakeUtterance()!;
        Assert.True(u.Length <= (5 + 3) * Frame + 3200, $"length {u.Length}");
    }
}

public sealed class BargeInDetectorTests
{
    private static readonly VoiceOptions Options = new() { BargeInMinSpeechMs = 300, BargeInEchoMargin = 1.5f };

    /// <summary>Speech-like envelope: syllables with quiet gaps.</summary>
    private static float Envelope(int frame) => 0.02f + 0.08f * MathF.Abs(MathF.Sin(frame * 0.7f));

    [Fact]
    public void Echo_alone_never_triggers()
    {
        var d = new BargeInDetector(Options, 32);
        for (var i = 0; i < 2000; i++)
        {
            var output = Envelope(i);
            var mic = 0.35f * output * (0.9f + 0.1f * MathF.Sin(i)); // echo, VAD says speech
            var gated = d.Gate(vadSpeech: true, mic, output);
            Assert.False(d.Update(gated), $"false barge-in at frame {i} (coupling {d.Coupling})");
        }
    }

    [Fact]
    public void User_speech_over_echo_triggers_within_a_second()
    {
        var d = new BargeInDetector(Options, 32);
        for (var i = 0; i < 200; i++) d.Update(d.Gate(true, 0.35f * Envelope(i), Envelope(i))); // learn echo
        var triggeredAt = -1;
        for (var i = 0; i < 40 && triggeredAt < 0; i++)
        {
            var output = Envelope(200 + i);
            var user = 0.06f * (0.5f + MathF.Abs(MathF.Sin(i * 0.9f)));
            var mic = MathF.Sqrt(MathF.Pow(0.35f * output, 2) + user * user);
            if (d.Update(d.Gate(true, mic, output))) triggeredAt = i;
        }
        Assert.InRange(triggeredAt, 8, 31); // ≥ ~300 ms of speech, < 1 s
        Assert.True(d.FramesSinceOnset > 0);
    }

    [Fact]
    public void Without_playback_vad_decides_directly()
    {
        var d = new BargeInDetector(Options, 32);
        Assert.True(d.Gate(true, 0.05f, 0f));
        Assert.False(d.Gate(false, 0.05f, 0f));
        var fired = Enumerable.Range(0, 20).Any(_ => d.Update(true));
        Assert.True(fired);
    }
}

public sealed class TranscriptFilterTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("...", true)]
    [InlineData("[Música]", true)]
    [InlineData("Legendas pela comunidade Amara.org", true)]
    [InlineData("Sottotitoli creati dalla comunità Amara.org", true)]
    [InlineData("Espera.", false)]
    [InlineData("Obrigado!", false)]
    public void Filters_noise_and_known_hallucinations(string text, bool noise) =>
        Assert.Equal(noise, TranscriptFilter.IsNoise(text));
}
