using System.Text.RegularExpressions;
using LocalAI.Configuration;

namespace LocalAI.Core.Language;

/// <param name="Language">ISO 639-1 code, or null when undetermined.</param>
/// <param name="Confidence">0..1; low for short or ambiguous text.</param>
public sealed record LanguageDetection(string? Language, float Confidence)
{
    public bool IsConfident => Language != null && Confidence >= 0.6f;
}

public interface ILanguageDetector
{
    LanguageDetection Detect(string text);
}

/// <summary>
/// Lightweight offline detector for the languages in languages.json, based on common words, word endings and
/// distinctive letters. Speech input uses Whisper's own language detection; this is for typed text and for choosing
/// TTS voices.
/// </summary>
public sealed partial class HeuristicLanguageDetector(LanguageData data) : ILanguageDetector
{
    private const double CommonWordScore = 1;
    private const double WordEndingScore = 0.75;
    private const double LetterScore = 1.5;
    private const int MinWordLengthForEndings = 5;

    private readonly Dictionary<string, HashSet<string>> _commonWords = data.Languages.ToDictionary(
        kv => kv.Key, kv => new HashSet<string>(kv.Value.CommonWords, StringComparer.Ordinal));

    [GeneratedRegex(@"[\p{L}']+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public LanguageDetection Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || data.Languages.Count < 2) return new LanguageDetection(null, 0);

        var lower = text.ToLowerInvariant();
        var scores = data.Languages.Keys.ToDictionary(code => code, _ => 0.0);
        var words = 0;

        foreach (Match m in WordRegex().Matches(lower))
        {
            words++;
            var word = m.Value;
            foreach (var (code, common) in _commonWords)
                if (common.Contains(word)) scores[code] += CommonWordScore;
            if (word.Length < MinWordLengthForEndings) continue;
            var ending = data.Languages.FirstOrDefault(kv => kv.Value.WordEndings.Any(e => word.EndsWith(e, StringComparison.Ordinal)));
            if (ending.Key != null) scores[ending.Key] += WordEndingScore;
        }

        foreach (var (code, profile) in data.Languages)
        {
            scores[code] += lower.Count(profile.Letters.Contains) * LetterScore;
            scores[code] += profile.LetterGroups.Count(g => lower.Contains(g, StringComparison.Ordinal)) * LetterScore;
        }

        var ordered = scores.OrderByDescending(kv => kv.Value).ToArray();
        var (best, bestScore) = (ordered[0].Key, ordered[0].Value);
        var second = ordered[1].Value;
        if (bestScore <= 0) return new LanguageDetection(null, 0);

        // Confidence: margin over the runner-up, damped for very short inputs.
        var margin = (bestScore - second) / bestScore;
        var lengthFactor = Math.Min(1.0, (words + 1) / 4.0);
        var confidence = (float)Math.Clamp(margin * lengthFactor + (bestScore >= 3 ? 0.2 : 0), 0, 1);
        return new LanguageDetection(best, confidence);
    }
}
