using LocalAI.Configuration;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class SentenceCompletenessTests
{
    private static readonly LanguageData Languages = LanguageData.LoadDefault();

    [Theory]
    [InlineData("I went to the store and", "en", true)]
    [InlineData("I went to the store and.", "en", true)] // Whisper often adds a period anyway
    [InlineData("I was thinking that,", "en", true)]
    [InlineData("I went there and...", "en", true)]
    [InlineData("What do you think about", "en", true)]
    [InlineData("I went to the store.", "en", false)]
    [InlineData("Is it done?", "en", false)]
    [InlineData("Okay", "en", false)]
    [InlineData("Eu fui no mercado e", "pt", true)]
    [InlineData("Eu fui no mercado porque", "pt", true)]
    [InlineData("Eu fui no mercado.", "pt", false)]
    [InlineData("Ho comprato il pane e", "it", true)]
    [InlineData("Ho comprato il pane.", "it", false)]
    [InlineData("I think and", null, true)] // unknown language: every language's words count
    [InlineData("", "en", false)]
    public void Detects_a_sentence_the_user_did_not_finish(string text, string? language, bool unfinished) =>
        Assert.Equal(unfinished, SentenceCompleteness.LooksUnfinished(text, language, Languages));
}

public sealed class ReactionPlannerTests
{
    private const int FrameMs = 32;
    private const int PauseFrames = 11; // 352 ms

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static ReactionPlanner Create(ReactionLevel level, double random = 0) =>
        new(new VoiceOptions { Reactions = level, ReactionPauseMs = PauseFrames * FrameMs }, FrameMs, new FixedRandom(random));

    /// <summary>Feeds the frames of a pattern ('s' = speech, '.' = silence); returns the indexes that reacted.</summary>
    private static List<int> Run(ReactionPlanner planner, string pattern) =>
        pattern.Select((c, i) => (Reacted: planner.Update(c == 's'), Index: i)).Where(x => x.Reacted).Select(x => x.Index).ToList();

    private static string Speech(double seconds) => new('s', (int)(seconds * 1000 / FrameMs));
    private static string Pause => new('.', PauseFrames + 5);

    [Fact]
    public void Reacts_once_when_a_pause_follows_enough_speech()
    {
        var planner = Create(ReactionLevel.Frequent);
        var speech = Speech(3.2);
        var reacted = Run(planner, speech + Pause);
        Assert.Equal([speech.Length + PauseFrames - 1], reacted); // the frame the pause reaches ReactionPauseMs
    }

    [Fact]
    public void Does_not_react_after_little_speech() =>
        Assert.Empty(Run(Create(ReactionLevel.Frequent), Speech(2) + Pause));

    [Fact]
    public void Off_never_reacts() =>
        Assert.Empty(Run(Create(ReactionLevel.Off), Speech(20) + Pause));

    [Fact]
    public void Subtle_needs_more_speech_than_frequent()
    {
        Assert.Empty(Run(Create(ReactionLevel.Subtle), Speech(4) + Pause));
        Assert.Single(Run(Create(ReactionLevel.Subtle), Speech(5.5) + Pause));
    }

    [Fact]
    public void Leaves_a_gap_between_reactions()
    {
        var planner = Create(ReactionLevel.Frequent);
        Assert.Single(Run(planner, Speech(3.2) + Pause));
        Assert.Empty(Run(planner, Speech(3.2) + Pause)); // less than 5 s since the last reaction
        Assert.Single(Run(planner, Speech(1.5) + Pause));
    }

    [Fact]
    public void Skips_some_pauses_at_random() =>
        Assert.Empty(Run(Create(ReactionLevel.Frequent, random: 0.99), Speech(10) + Pause));

    [Fact]
    public void A_short_sound_after_a_pause_is_not_a_moment_to_react()
    {
        var planner = Create(ReactionLevel.Frequent);
        Assert.Empty(Run(planner, Speech(2.9) + Pause));
        Assert.Empty(Run(planner, Speech(0.2) + Pause)); // enough speech in total, but only an "uh" before this pause
        Assert.Single(Run(planner, Speech(1.2) + Pause));
    }

    [Fact]
    public void Reset_starts_the_next_message_from_scratch()
    {
        var planner = Create(ReactionLevel.Frequent);
        Run(planner, Speech(2.5));
        planner.Reset();
        Assert.Empty(Run(planner, Speech(1) + Pause));
    }
}

public sealed class ConversationCuesTests
{
    private static readonly LanguageData Languages = LanguageData.LoadDefault();

    private static (ConversationCues Cues, FakeTts Tts, FakePlayer Player, SpeechOutput Speech) Create()
    {
        var tts = new FakeTts();
        var player = new FakePlayer();
        var speech = new SpeechOutput(tts, player, Languages, NullLogger<SpeechOutput>.Instance);
        return (new ConversationCues(tts, speech, Languages, NullLogger<ConversationCues>.Instance), tts, player, speech);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var met = condition();
        for (var i = 0; i < 200 && !met; i++)
        {
            await Task.Delay(10);
            met = condition();
        }
        Assert.True(met);
    }

    [Fact]
    public void Nothing_plays_before_the_sounds_are_ready()
    {
        var (cues, _, player, _) = Create();
        Assert.False(cues.TryPlayReaction("en"));
        Assert.False(cues.TryPlayFiller("en", 0));
        Assert.Equal(0, player.Enqueued);
    }

    [Fact]
    public async Task Prepares_the_reactions_and_fillers_of_the_language_once()
    {
        var (cues, tts, player, _) = Create();
        var en = Languages.Languages["en"];
        cues.Prepare("en");
        await WaitUntilAsync(() => cues.TryPlayReaction("en"));
        await WaitUntilAsync(() => tts.Spoken.Count == en.Reactions.Count + en.Fillers.Count);
        Assert.Contains(en.Fillers[0], tts.Spoken);

        cues.Prepare("en");
        await Task.Delay(50);
        Assert.Equal(en.Reactions.Count + en.Fillers.Count, tts.Spoken.Count); // cached
        Assert.Equal(1, player.Enqueued);
    }

    [Fact]
    public async Task A_language_without_phrases_uses_the_voice_language()
    {
        var (cues, tts, _, _) = Create();
        cues.Prepare("xx");
        await WaitUntilAsync(() => tts.Spoken.Contains(Languages.Languages["en"].Reactions[0]));
    }

    [Fact]
    public async Task A_filler_never_plays_after_the_reply_started_speaking()
    {
        var (cues, _, player, speech) = Create();
        cues.Prepare("en");
        await WaitUntilAsync(() => cues.TryPlayFiller("en", speech.ReplyClips));
        Assert.Equal(1, player.Enqueued);

        var before = speech.ReplyClips;
        var utterance = speech.Begin(CancellationToken.None);
        utterance.Append("The reply starts here.");
        utterance.Complete();
        await utterance.Completion;
        Assert.False(cues.TryPlayFiller("en", before));
        Assert.Equal(2, player.Enqueued); // the filler and the reply's sentence
    }

    [Fact]
    public void Shape_cuts_the_silence_around_a_phrase_and_applies_the_gain()
    {
        var samples = new float[16000];
        for (var i = 8000; i < 9000; i++) samples[i] = 0.5f;
        var shaped = ConversationCues.Shape(new AudioClip(samples, 16000), 0.5f);
        Assert.Equal(1000 + 2 * 480, shaped.Samples.Length); // 30 ms kept on each side
        Assert.Equal(0.25f, shaped.Samples.Max());
    }
}
