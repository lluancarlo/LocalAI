using System.Collections.Concurrent;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Voice;

/// <summary>
/// The short sounds that make a spoken conversation feel natural: reactions while the user talks ("mm-hmm") and a
/// phrase that fills a slow reply ("hmm, let me see..."). The phrases come from languages.json. They are synthesized
/// ahead of time (<see cref="Prepare"/>) with the active voice and style so they play at once; one that is not ready
/// is skipped, never synthesized on the spot.
/// </summary>
public sealed class ConversationCues
{
    private const float ReactionGain = 0.75f; // reactions sit a little under the assistant's normal voice
    private const float TrimThreshold = 0.01f;
    private const int TrimMarginMs = 30;

    private readonly ITextToSpeech _tts;
    private readonly SpeechOutput _speech;
    private readonly LanguageData _languages;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<CueKey, AudioClip> _clips = new();
    private readonly ConcurrentDictionary<CueKey, byte> _synthesizing = new();
    private string? _lastText;
    private long _reactionEndsAt;

    private sealed record CueKey(string VoiceId, VoiceStyle Style, string Text);

    public ConversationCues(ITextToSpeech tts, SpeechOutput speech, LanguageData languages, ILogger<ConversationCues> logger)
    {
        _tts = tts;
        _speech = speech;
        _languages = languages;
        _logger = logger;
    }

    /// <summary>
    /// Synthesizes the reactions and fillers of <paramref name="language"/> (or of the voice's language) in the
    /// background. Call it while the assistant is idle: synthesis shares the voice with the replies.
    /// </summary>
    public void Prepare(string? language)
    {
        if (_tts.State != ComponentState.Ready || _tts.Voice is not { } voice) return;
        if (Profile(language, voice) is not { } profile) return;
        var style = _tts.Style;
        var missing = profile.Reactions.Select(t => (Key: new CueKey(voice.Id, style, t), Gain: ReactionGain))
            .Concat(profile.Fillers.Select(t => (Key: new CueKey(voice.Id, style, t), Gain: 1f)))
            .Where(c => !_clips.ContainsKey(c.Key) && _synthesizing.TryAdd(c.Key, 0))
            .ToList();
        if (missing.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var (key, gain) in missing)
            {
                try
                {
                    var clip = await _tts.SynthesizeAsync(key.Text, key.VoiceId, key.Style).ConfigureAwait(false);
                    if (clip.Samples.Length > 0) _clips[key] = Shape(clip, gain);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not prepare a conversation sound for voice {Voice}", key.VoiceId);
                }
                finally
                {
                    _synthesizing.TryRemove(key, out _);
                }
            }
        });
    }

    /// <summary>How many phrases are synthesized and ready to play.</summary>
    internal int ReadyCount => _clips.Count;

    /// <summary>Plays a reaction if one is ready and nothing else is playing.</summary>
    public bool TryPlayReaction(string? language)
    {
        if (_speech.Player.IsPlaying) return false;
        if (Pick(language, p => p.Reactions) is not { } clip) return false;
        _speech.Player.Enqueue(clip);
        Interlocked.Exchange(ref _reactionEndsAt, Environment.TickCount64 + (long)clip.Duration.TotalMilliseconds);
        return true;
    }

    /// <summary>Plays a phrase that fills the wait, unless the reply started speaking since <paramref name="replyClips"/>.</summary>
    public bool TryPlayFiller(string? language, long replyClips) =>
        Pick(language, p => p.Fillers) is { } clip && _speech.TryPlayBeforeReply(clip, replyClips);

    /// <summary>Waits (at most a second) for a reaction that is still playing, so the next turn does not cut it off.</summary>
    public Task WaitForReactionAsync(CancellationToken cancellationToken = default)
    {
        var left = Interlocked.Read(ref _reactionEndsAt) - Environment.TickCount64;
        return left > 0 ? Task.Delay((int)Math.Min(left, 1000), cancellationToken) : Task.CompletedTask;
    }

    private AudioClip? Pick(string? language, Func<LanguageProfile, IReadOnlyList<string>> phrases)
    {
        if (_tts.Voice is not { } voice || Profile(language, voice) is not { } profile) return null;
        var style = _tts.Style;
        var ready = phrases(profile)
            .Select(t => (Text: t, Clip: _clips.GetValueOrDefault(new CueKey(voice.Id, style, t))))
            .Where(c => c.Clip != null)
            .ToList();
        if (ready.Count > 1) ready.RemoveAll(c => c.Text == _lastText); // never the same sound twice in a row
        if (ready.Count == 0) return null;
        var pick = ready[Random.Shared.Next(ready.Count)];
        _lastText = pick.Text;
        return pick.Clip;
    }

    /// <summary>The user's language when it has phrases, otherwise the voice's own language.</summary>
    private LanguageProfile? Profile(string? language, VoiceInfo voice)
    {
        foreach (var code in new[] { language, voice.Language })
        {
            if (code != null && _languages.Languages.TryGetValue(code, out var profile) && profile.Reactions.Count + profile.Fillers.Count > 0)
                return profile;
        }
        return null;
    }

    /// <summary>Cuts the silence Piper adds around a phrase (it would delay a reaction) and applies the gain.</summary>
    internal static AudioClip Shape(AudioClip clip, float gain)
    {
        var s = clip.Samples;
        var margin = clip.SampleRate * TrimMarginMs / 1000;
        var first = Array.FindIndex(s, x => Math.Abs(x) > TrimThreshold);
        if (first < 0) return clip;
        var last = Array.FindLastIndex(s, x => Math.Abs(x) > TrimThreshold);
        var start = Math.Max(0, first - margin);
        var end = Math.Min(s.Length, last + 1 + margin);
        var shaped = new float[end - start];
        for (var i = 0; i < shaped.Length; i++) shaped[i] = s[start + i] * gain;
        return new AudioClip(shaped, clip.SampleRate);
    }
}
