using LocalAI.Configuration;

namespace LocalAI.Core.Voice;

/// <summary>
/// Guesses from the transcript whether the user paused mid-sentence ("I went to the store and...") rather than
/// finished. Whisper writes the pause as a comma or an ellipsis, or the last word is one a sentence rarely ends with.
/// </summary>
public static class SentenceCompleteness
{
    /// <param name="language">Whisper's language for the transcript; null checks the words of every language.</param>
    public static bool LooksUnfinished(string text, string? language, LanguageData languages)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith('?')) return false;
        if (t.EndsWith("...", StringComparison.Ordinal) || t[^1] is ',' or ';' or ':' or '-' or '–' or '…') return true;

        var end = t.Length;
        while (end > 0 && !char.IsLetterOrDigit(t[end - 1])) end--;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(t[start - 1]) || t[start - 1] == '\'')) start--;
        if (start == end) return false;
        var lastWord = t[start..end].ToLowerInvariant();

        var profiles = language != null && languages.Languages.TryGetValue(language, out var profile)
            ? [profile]
            : languages.Languages.Values;
        return profiles.Any(p => p.UnfinishedEndings.Contains(lastWord, StringComparer.Ordinal));
    }
}

/// <summary>
/// Decides when the assistant reacts ("mm-hmm") while the user talks: in a short pause after a stretch of speech,
/// not too often, and not at every pause. Fed one echo-gated VAD decision per frame.
/// </summary>
public sealed class ReactionPlanner
{
    private readonly VoiceOptions _options;
    private readonly int _frameMs;
    private readonly Random _random;
    private int _speechSinceReactionMs;
    private int _sinceReactionMs;
    private int _stretchMs;
    private int _silenceMs;

    public ReactionPlanner(VoiceOptions options, int frameMs, Random random)
    {
        _options = options;
        _frameMs = Math.Max(1, frameMs);
        _random = random;
        Reset();
    }

    /// <summary>Returns true when the assistant should react now.</summary>
    public bool Update(bool userSpeech)
    {
        _sinceReactionMs += _frameMs;
        if (userSpeech)
        {
            if (_silenceMs >= _options.ReactionPauseMs) _stretchMs = 0;
            _silenceMs = 0;
            _stretchMs += _frameMs;
            _speechSinceReactionMs += _frameMs;
            return false;
        }

        var wasBelow = _silenceMs < _options.ReactionPauseMs;
        _silenceMs += _frameMs;
        if (!wasBelow || _silenceMs < _options.ReactionPauseMs) return false; // only at the moment the pause qualifies

        var (minSpeechMs, minGapMs, chance) = _options.Reactions switch
        {
            ReactionLevel.Frequent => (3000, 5000, 0.85),
            ReactionLevel.Subtle => (5000, 10000, 0.5),
            _ => (int.MaxValue, int.MaxValue, 0.0),
        };
        // A pause after a short "uh" is not a moment to react; after a sentence or two of speech it is.
        if (_stretchMs < 1000 || _speechSinceReactionMs < minSpeechMs || _sinceReactionMs < minGapMs) return false;
        if (_random.NextDouble() >= chance) return false;

        _speechSinceReactionMs = 0;
        _sinceReactionMs = 0;
        return true;
    }

    /// <summary>The assistant took the turn: the next message starts from scratch.</summary>
    public void Reset()
    {
        _speechSinceReactionMs = 0;
        _sinceReactionMs = int.MaxValue / 2;
        _stretchMs = 0;
        _silenceMs = int.MaxValue / 2;
    }
}
