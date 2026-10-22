using System.Threading.Channels;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Desktop;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

/// <summary>Turn-taking in Live mode, driven with scripted microphone frames and transcripts.</summary>
public sealed class LiveTurnTests
{
    [Fact]
    public async Task A_finished_sentence_is_answered_right_away()
    {
        await using var live = await LiveHarness.StartAsync(_ => "I went to the store.");
        live.Speech(20);
        live.Silence(12);
        Assert.Equal(["I went to the store."], await live.UserMessagesAsync(count: 1));
    }

    [Fact]
    public async Task An_unfinished_sentence_waits_for_a_longer_silence_before_the_answer()
    {
        await using var live = await LiveHarness.StartAsync(_ => "I went to the store and");
        live.Speech(20);
        live.Silence(12);
        await live.WaitUntilAsync(() => live.Stt.Calls == 1 && live.Voice.State == VoiceState.Listening);

        await live.SilenceForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(await live.UserMessagesAsync(count: 0));

        await live.SilenceForAsync(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(["I went to the store and"], await live.UserMessagesAsync(count: 1));
    }

    [Fact]
    public async Task Speech_after_an_unfinished_sentence_continues_the_same_message()
    {
        await using var live = await LiveHarness.StartAsync(call => call == 0 ? "I went to the store and" : "I went to the store and bought bread.");
        live.Speech(20);
        live.Silence(12);
        await live.WaitUntilAsync(() => live.Stt.Calls == 1 && live.Voice.State == VoiceState.Listening);

        live.Speech(20);
        live.Silence(12);
        Assert.Equal(["I went to the store and bought bread."], await live.UserMessagesAsync(count: 1));
        Assert.True(live.Stt.Lengths[1] > live.Stt.Lengths[0] + 15 * LiveHarness.Frame); // transcribed together
    }

    [Fact]
    public async Task Speech_while_transcribing_continues_the_same_message()
    {
        var first = new TaskCompletionSource();
        await using var live = await LiveHarness.StartAsync(call => call == 0 ? "First part." : "First part, second part.",
            call => call == 0 ? first.Task : Task.CompletedTask);
        live.Speech(20);
        live.Silence(12);
        await live.WaitUntilAsync(() => live.Stt.Calls == 1);

        live.Speech(20); // the user goes on before the first transcription is done
        live.Silence(12);
        Assert.Equal(["First part, second part."], await live.UserMessagesAsync(count: 1));

        first.SetResult(); // the first transcription finishes late: it is dropped
        await Task.Delay(200);
        Assert.Single(await live.UserMessagesAsync(count: 1));
    }

    [Fact]
    public async Task A_slow_reply_is_preceded_by_a_wait_phrase()
    {
        await using var live = await LiveHarness.StartAsync(_ => "Tell me about the project.", configure: v => v.FillerDelayMs = 150);
        live.Llm.TokenDelay = TimeSpan.FromMilliseconds(250);
        await live.CuesReadyAsync();
        live.Speech(20);
        live.Silence(12);
        await live.UserMessagesAsync(count: 1);
        await live.WaitUntilAsync(() => !live.Session.IsBusy);
        Assert.Equal(2, live.Player.Enqueued); // "hmm, let me see..." then the reply
    }

    [Fact]
    public async Task A_quick_reply_has_no_wait_phrase()
    {
        await using var live = await LiveHarness.StartAsync(_ => "Tell me about the project.", configure: v => v.FillerDelayMs = 1500);
        await live.CuesReadyAsync();
        live.Speech(20);
        live.Silence(12);
        await live.UserMessagesAsync(count: 1);
        await live.WaitUntilAsync(() => !live.Session.IsBusy);
        Assert.Equal(1, live.Player.Enqueued);
    }

    [Fact]
    public async Task Reacts_in_a_pause_of_a_long_message()
    {
        await using var live = await LiveHarness.StartAsync(_ => "unused", configure: v =>
        {
            v.Reactions = ReactionLevel.Frequent;
            v.ReactionPauseMs = 96;
        });
        await live.CuesReadyAsync();
        live.Speech(100); // 3.2 s
        live.Silence(4);  // a pause shorter than the end of the message
        live.Speech(10);
        await live.WaitUntilAsync(() => live.Player.Enqueued == 1);
        Assert.Equal(0, live.Stt.Calls);
    }

    [Fact]
    public async Task Without_waiting_for_unfinished_sentences_every_pause_is_answered()
    {
        await using var live = await LiveHarness.StartAsync(_ => "I went to the store and", configure: v => v.WaitForUnfinishedSentences = false);
        live.Speech(20);
        live.Silence(12);
        Assert.Equal(["I went to the store and"], await live.UserMessagesAsync(count: 1));
    }

    private sealed class LiveHarness : IAsyncDisposable
    {
        public const int Frame = 512; // 32 ms at 16 kHz

        private readonly FakeCapture _capture = new();
        private readonly InMemoryConversationStore _store = new();
        private readonly AssistantSession _session;
        public AssistantSession Session => _session;

        public ScriptedStt Stt { get; }
        public VoiceConversationController Voice { get; }
        public FakePlayer Player { get; } = new();
        public FakeLanguageModel Llm { get; }
        public ConversationCues Cues { get; }

        private LiveHarness(Func<int, string> transcripts, Func<int, Task>? delays, Action<VoiceOptions>? configure)
        {
            var options = new LocalAiOptions();
            var v = options.Voice;
            v.PreRollMs = 64;
            v.MinSpeechMs = 96;
            v.EndOfSpeechSilenceMs = 320;
            v.UnfinishedSentenceSilenceMs = 1500;
            v.Reactions = ReactionLevel.Off;
            v.FillerDelayMs = 0;
            configure?.Invoke(v);
            var languages = LanguageData.LoadDefault();
            var wrapped = Microsoft.Extensions.Options.Options.Create(options);
            var memory = new MemoryHarness(conversations: _store, options: wrapped);
            Llm = memory.Llm;
            var tts = new FakeTts();
            var speech = new SpeechOutput(tts, Player, languages, NullLogger<SpeechOutput>.Instance);
            _session = new AssistantSession(memory.Llm, _store, new PromptBuilder(wrapped, memory.Context), new HeuristicLanguageDetector(languages),
                memory.Vault, speech, wrapped, NullLogger<AssistantSession>.Instance);
            var monitor = new FixedMonitor(options);
            Stt = new ScriptedStt(transcripts, delays);
            Cues = new ConversationCues(tts, speech, languages, NullLogger<ConversationCues>.Instance);
            Voice = new VoiceConversationController(_capture, new FakeVad(), Stt, _session,
                new LiveSelection(new NoSelection(), monitor, NullLogger<LiveSelection>.Instance), monitor, languages, Cues,
                NullLogger<VoiceConversationController>.Instance)
            {
                ReactionRandom = new FixedRandom(0), // always react when the planner allows it
            };
        }

        public static async Task<LiveHarness> StartAsync(Func<int, string> transcripts, Func<int, Task>? delays = null,
            Action<VoiceOptions>? configure = null)
        {
            var harness = new LiveHarness(transcripts, delays, configure);
            await harness.Voice.StartContinuousAsync();
            Assert.Equal(VoiceState.Listening, harness.Voice.State);
            return harness;
        }

        public void Speech(int frames) => Write(frames, 0.1f);
        public void Silence(int frames) => Write(frames, 0f);

        /// <summary>Silence in real time, as a microphone delivers it.</summary>
        public async Task SilenceForAsync(TimeSpan duration)
        {
            var end = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < end)
            {
                Silence(1);
                await Task.Delay(32);
            }
        }

        private void Write(int frames, float value)
        {
            for (var i = 0; i < frames; i++) _capture.Frames.Writer.TryWrite(Enumerable.Repeat(value, Frame).ToArray());
        }

        /// <summary>The user's messages once <paramref name="count"/> of them are stored (or after a timeout).</summary>
        public async Task<List<string>> UserMessagesAsync(int count)
        {
            List<string> messages = [];
            for (var i = 0; i < 300; i++)
            {
                messages = [];
                foreach (var c in await _store.ListAsync())
                    messages.AddRange((await _store.GetMessagesAsync(c.Id)).Where(m => m.Role == ChatRole.User).Select(m => m.Content));
                if (messages.Count >= count && !_session.IsBusy) break;
                await Task.Delay(10);
            }
            return messages;
        }

        /// <summary>Waits for the reactions and wait phrases, synthesized in the background when Live mode starts.</summary>
        public Task CuesReadyAsync()
        {
            var english = LanguageData.LoadDefault().Languages["en"];
            return WaitUntilAsync(() => Cues.ReadyCount == english.Reactions.Count + english.Fillers.Count);
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
            Assert.True(condition());
        }

        public async ValueTask DisposeAsync()
        {
            await Voice.DisposeAsync();
            _session.Dispose();
        }
    }

    private sealed class ScriptedStt(Func<int, string> transcripts, Func<int, Task>? delays) : ISpeechToText
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<int> Lengths { get; } = [];
        public ComponentState State => ComponentState.Ready;
        public string? Description => "scripted";
        public string? LastError => null;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Unload() { }
        public void Dispose() { }

        public async Task<Transcription> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
        {
            int call;
            lock (Lengths)
            {
                call = _calls;
                Lengths.Add(samples.Length);
                Interlocked.Increment(ref _calls);
            }
            if (delays != null) await delays(call);
            return new Transcription(transcripts(call), "en", 1f, TimeSpan.FromSeconds(samples.Length / 16000.0), TimeSpan.Zero);
        }
    }

    private sealed class FakeCapture : IAudioCapture, IAudioCaptureSession
    {
        public Channel<float[]> Frames { get; } = Channel.CreateUnbounded<float[]>();
        ChannelReader<float[]> IAudioCaptureSession.Frames => Frames.Reader;
        public string DeviceName => "test microphone";
        public IAudioCaptureSession Start(string? deviceId) => this;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVad : IVoiceActivityDetectorFactory, IVoiceActivityDetector
    {
        public bool IsAvailable => true;
        public IVoiceActivityDetector Create() => this;
        public int FrameSize => LiveHarness.Frame;
        public bool IsSpeech(float[] frame) => frame[0] > 0.05f;
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class NoSelection : ITextSelectionMonitor
    {
        public bool IsSupported => false;
        public IDisposable Start(Action<string> selected) => new MemoryStream();
    }

    private sealed class FixedMonitor(LocalAiOptions options) : Microsoft.Extensions.Options.IOptionsMonitor<LocalAiOptions>
    {
        public LocalAiOptions CurrentValue => options;
        public LocalAiOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<LocalAiOptions, string?> listener) => null;
    }
}
