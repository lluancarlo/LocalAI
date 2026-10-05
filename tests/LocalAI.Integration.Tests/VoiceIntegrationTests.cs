using System.Collections.Concurrent;
using System.Diagnostics;
using LocalAI.Audio;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>
/// End-to-end voice pipeline with real VAD, Whisper, LLM and Piper, using <see cref="SimulatedRoom"/> as microphone
/// and speaker (including speaker→mic echo).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class VoiceIntegrationTests(LocalAiFixture fx, ITestOutputHelper output)
{
    private T Get<T>() where T : notnull => fx.Services.GetRequiredService<T>();

    private async Task<float[]> SpeechAsync(string text, string language)
    {
        var clip = await Get<ITextToSpeech>().SynthesizeAsync(text, LocalAiFixture.VoiceFor(language), VoiceStyle.Default);
        return StreamingResampler.Convert(clip.Samples, clip.SampleRate, ISpeechToText.SampleRate);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Continuous_mode_detects_end_of_speech_answers_aloud_and_listens_again()
    {
        var voice = Get<VoiceConversationController>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        var states = new ConcurrentQueue<(VoiceState State, long Ms)>();
        var sw = Stopwatch.StartNew();
        Transcription? heard = null;
        TurnResult? turn = null;
        voice.StateChanged += (_, s) => states.Enqueue((s, sw.ElapsedMilliseconds));
        voice.Transcribed += (_, t) => heard = t;
        session.TurnCompleted += (_, r) => turn = r;

        var question = await SpeechAsync("Ciao! Qual è la capitale della Francia? Rispondi con una frase breve.", "it");
        var clipsBefore = fx.Room.ClipsPlayed;
        await voice.StartContinuousAsync();
        try
        {
            sw.Restart();
            fx.Room.SaySilence(TimeSpan.FromMilliseconds(500));
            fx.Room.Say(question);
            var speechEndMs = 500 + question.Length * 1000L / ISpeechToText.SampleRate; // queued after the silence

            await WaitUntilAsync(() => turn != null, TimeSpan.FromSeconds(60), "assistant turn");
            await WaitUntilAsync(() => voice.State == VoiceState.Listening, TimeSpan.FromSeconds(30), "back to listening");

            output.WriteLine($"Heard [{heard?.Language}]: {heard?.Text}");
            output.WriteLine($"Reply: {turn!.AssistantMessage?.Content}");
            output.WriteLine("States: " + string.Join(" → ", states.Select(s => $"{s.State}@{s.Ms}")));
            var speakingAt = states.FirstOrDefault(s => s.State == VoiceState.Speaking).Ms;
            output.WriteLine($"Voice round trip (end of user speech → assistant audio starts): {speakingAt - speechEndMs} ms");

            Assert.Equal("it", heard?.Language);
            Assert.Contains("Francia", heard!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TurnOutcome.Completed, turn.Outcome);
            Assert.Contains("Paris", turn.AssistantMessage!.Content, StringComparison.OrdinalIgnoreCase);
            Assert.True(fx.Room.ClipsPlayed > clipsBefore, "reply should have been spoken");
            Assert.Contains(states, s => s.State == VoiceState.Speaking);
            Assert.Equal(VoiceState.Listening, voice.State);
        }
        finally
        {
            await voice.StopContinuousAsync();
        }
    }

    [Fact]
    public async Task User_can_interrupt_assistant_while_it_speaks_and_echo_alone_does_not()
    {
        var voice = Get<VoiceConversationController>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        var bargeIns = 0;
        var transcripts = new ConcurrentQueue<Transcription>();
        var turns = new ConcurrentQueue<TurnResult>();
        voice.BargeIn += (_, _) => Interlocked.Increment(ref bargeIns);
        voice.Transcribed += (_, t) => transcripts.Enqueue(t);
        session.TurnCompleted += (_, r) => turns.Enqueue(r);

        // Loud speakers, no headset, no hardware AEC: the mic hears the assistant at -9 dB (gain 0.35).
        // The user sits closer to the mic than the speakers, so their voice arrives ~3.5 dB above the playback level.
        fx.Room.EchoGain = 0.35f;
        var question = await SpeechAsync("Me conte a história do Império Romano com bastante detalhe, em pelo menos dez frases.", "pt");
        var interruption = (await SpeechAsync("Espera, pare. Qual é a capital da Itália?", "pt")).Select(s => s * 1.5f).ToArray();

        await voice.StartContinuousAsync();
        try
        {
            fx.Room.SaySilence(TimeSpan.FromMilliseconds(300));
            fx.Room.Say(question);
            await WaitUntilAsync(() => voice.State == VoiceState.Speaking, TimeSpan.FromSeconds(60), "assistant speaking");

            // Let the assistant talk for a while: its own echo must not trigger barge-in.
            await Task.Delay(2500);
            Assert.Equal(0, bargeIns);
            Assert.Equal(VoiceState.Speaking, voice.State);

            var sw = Stopwatch.StartNew();
            fx.Room.Say(interruption);
            await WaitUntilAsync(() => bargeIns > 0, TimeSpan.FromSeconds(5), "barge-in");
            var reaction = sw.ElapsedMilliseconds;
            await WaitUntilAsync(() => !fx.Room.IsPlaying, TimeSpan.FromSeconds(2), "playback stopped");
            output.WriteLine($"Barge-in detected {reaction} ms after the user started speaking; playback stopped {sw.ElapsedMilliseconds} ms");

            // The interrupting speech becomes the next turn.
            await WaitUntilAsync(() => transcripts.Count >= 2, TimeSpan.FromSeconds(30), "interruption transcribed");
            await WaitUntilAsync(() => turns.Count >= 2, TimeSpan.FromSeconds(60), "second turn");
            var t = transcripts.ToArray();
            var r = turns.ToArray();
            output.WriteLine($"1st heard: {t[0].Text}");
            output.WriteLine($"1st turn: {r[0].Outcome} ({r[0].AssistantMessage?.Content.Length} chars before interruption)");
            output.WriteLine($"2nd heard: {t[1].Text}");
            output.WriteLine($"2nd turn: {r[1].Outcome}: {r[1].AssistantMessage?.Content}");
            output.WriteLine($"Barge-ins: {bargeIns}");
            foreach (var line in fx.ReadLog().Split('\n').Where(l => l.Contains("Barge-in") || l.Contains("Turn ")).TakeLast(8))
                output.WriteLine("  log: " + line.Trim());

            Assert.True(reaction < 1500, "barge-in should react within 1.5 s");
            Assert.Equal(TurnOutcome.Cancelled, r[0].Outcome);
            Assert.Contains("Itália", t[1].Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rome", r[1].AssistantMessage!.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await voice.StopContinuousAsync();
            fx.Room.EchoGain = 0.3f;
        }
    }

    [Fact]
    public async Task Live_mode_ignores_speech_while_typing_and_answers_typed_message_aloud()
    {
        var voice = Get<VoiceConversationController>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        var states = new ConcurrentQueue<VoiceState>();
        var heard = new ConcurrentQueue<string>();
        TurnResult? turn = null;
        LocalAI.Core.Conversations.StoredMessage? userMessage = null;
        voice.StateChanged += (_, s) => states.Enqueue(s);
        voice.Transcribed += (_, t) => heard.Enqueue(t.Text);
        session.UserMessageAdded += (_, m) => userMessage = m;
        session.TurnCompleted += (_, r) => turn = r;

        var ignored = await SpeechAsync("What is the capital of Germany?", "en");
        var clipsBefore = fx.Room.ClipsPlayed;
        await voice.StartContinuousAsync();
        try
        {
            voice.SetTyping(true);
            fx.Room.Say(ignored);
            fx.Room.SaySilence(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(ignored.Length / (double)ISpeechToText.SampleRate));
            Assert.Empty(heard);
            Assert.Null(turn);

            await voice.SubmitTextAsync("What is the capital of Japan? Answer in one short sentence.", speak: true);
            await WaitUntilAsync(() => voice.State == VoiceState.Listening, TimeSpan.FromSeconds(30), "back to listening");

            output.WriteLine($"Reply: {turn?.AssistantMessage?.Content}");
            output.WriteLine("States: " + string.Join(" → ", states));
            Assert.Empty(heard);
            Assert.Equal(TurnOutcome.Completed, turn!.Outcome);
            Assert.Equal(LocalAI.Core.Conversations.InputSource.Text, userMessage!.Source);
            Assert.Contains("Tokyo", turn.AssistantMessage!.Content, StringComparison.OrdinalIgnoreCase);
            Assert.True(fx.Room.ClipsPlayed > clipsBefore, "typed message should be answered aloud");
            Assert.Contains(VoiceState.Speaking, states);
            Assert.False(voice.IsTyping);
        }
        finally
        {
            await voice.StopContinuousAsync();
        }
    }

    [Fact]
    public async Task Live_mode_reply_to_a_typed_message_can_be_interrupted_by_voice()
    {
        var voice = Get<VoiceConversationController>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        var bargeIns = 0;
        var transcripts = new ConcurrentQueue<Transcription>();
        var turns = new ConcurrentQueue<TurnResult>();
        voice.BargeIn += (_, _) => Interlocked.Increment(ref bargeIns);
        voice.Transcribed += (_, t) => transcripts.Enqueue(t);
        session.TurnCompleted += (_, r) => turns.Enqueue(r);
        var interruption = (await SpeechAsync("Wait, stop. What is the capital of Italy?", "en")).Select(s => s * 1.5f).ToArray();

        await voice.StartContinuousAsync();
        try
        {
            _ = voice.SubmitTextAsync("Tell me the history of the Roman Empire in great detail, in at least ten sentences.", speak: true);
            await WaitUntilAsync(() => voice.State == VoiceState.Speaking, TimeSpan.FromSeconds(60), "assistant speaking");
            await Task.Delay(1500);

            fx.Room.Say(interruption);
            await WaitUntilAsync(() => bargeIns > 0, TimeSpan.FromSeconds(5), "barge-in");
            await WaitUntilAsync(() => turns.Count >= 2, TimeSpan.FromSeconds(60), "second turn");

            var r = turns.ToArray();
            output.WriteLine($"Heard: {transcripts.FirstOrDefault()?.Text}");
            output.WriteLine($"2nd turn: {r[1].AssistantMessage?.Content}");
            Assert.Equal(TurnOutcome.Cancelled, r[0].Outcome);
            Assert.Contains("Italy", Assert.Single(transcripts).Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rome", r[1].AssistantMessage!.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await voice.StopContinuousAsync();
        }
    }

    [Fact]
    public async Task Push_to_talk_records_transcribes_and_answers()
    {
        var voice = Get<VoiceConversationController>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        TurnResult? turn = null;
        Transcription? heard = null;
        session.TurnCompleted += (_, r) => turn = r;
        voice.Transcribed += (_, t) => heard = t;

        var speech = await SpeechAsync("What is two plus three? Answer with just the number.", "en");
        await voice.BeginPushToTalkAsync();
        Assert.Equal(VoiceState.Recording, voice.State);
        fx.Room.Say(speech);
        await Task.Delay(TimeSpan.FromSeconds((double)speech.Length / ISpeechToText.SampleRate + 0.3));
        await voice.EndPushToTalkAsync();

        await WaitUntilAsync(() => turn != null, TimeSpan.FromSeconds(60), "turn");
        await WaitUntilAsync(() => voice.State == VoiceState.Ready, TimeSpan.FromSeconds(30), "ready");
        output.WriteLine($"Heard [{heard?.Language}]: {heard?.Text} → {turn!.AssistantMessage?.Content}");
        Assert.Equal("en", heard?.Language);
        Assert.Contains("5", turn.AssistantMessage!.Content);
    }
}
