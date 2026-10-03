using System.Text.RegularExpressions;

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
/// Lightweight offline detector for the supported languages (pt, it, en) based on function words and orthography.
/// Speech input uses Whisper's own language detection; this is for typed text and for choosing TTS voices.
/// </summary>
public sealed partial class HeuristicLanguageDetector : ILanguageDetector
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["pt"] = new(StringComparer.Ordinal)
        {
            "o", "os", "as", "um", "uma", "uns", "umas", "do", "da", "dos", "das", "no", "na", "nos", "nas",
            "em", "de", "que", "não", "nao", "é", "são", "com", "para", "pra", "por", "como", "mais", "mas",
            "ou", "se", "eu", "você", "voce", "vocês", "ele", "ela", "eles", "elas", "meu", "minha", "seu", "sua",
            "isso", "isto", "esse", "essa", "este", "esta", "também", "tambem", "muito", "muita", "quando", "onde",
            "porque", "qual", "quais", "ser", "estar", "está", "estou", "tem", "têm", "tenho", "foi", "vai",
            "então", "entao", "já", "aqui", "ao", "aos", "pelo", "pela", "sobre", "explique", "obrigado", "sim",
            "olá", "oi", "bom", "dia", "fazer", "faz", "posso", "pode", "preciso", "quero", "seria", "agora",
            "dois", "duas", "três", "responda", "explica", "diga", "me", "te", "lhe", "nós", "isso", "coisa",
        },
        ["it"] = new(StringComparer.Ordinal)
        {
            "il", "lo", "la", "i", "gli", "le", "un", "uno", "una", "del", "dello", "della", "dei", "degli",
            "delle", "nel", "nella", "nei", "di", "che", "non", "è", "sono", "con", "per", "come", "più", "piu",
            "ma", "o", "se", "io", "tu", "lui", "lei", "noi", "voi", "loro", "mio", "mia", "suo", "sua", "questo",
            "questa", "quello", "quella", "anche", "molto", "quando", "dove", "perché", "perche", "quale",
            "essere", "stare", "sto", "ho", "hai", "ha", "abbiamo", "fatto", "fare", "va", "allora", "già",
            "qui", "al", "allo", "alla", "ai", "sul", "sulla", "spiegami", "grazie", "sì", "ciao", "buongiorno",
            "posso", "puoi", "voglio", "vorrei", "adesso", "ora", "cosa", "c'è", "ne", "ci", "mi", "ti",
            "in", "due", "tre", "cos'è", "dell'", "nell'", "sei", "siamo", "dimmi", "rispondi", "spiega", "grazie",
        },
        ["en"] = new(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "to", "in", "on", "at", "and", "or", "but", "is", "are", "was", "were",
            "be", "been", "it", "this", "that", "these", "those", "with", "for", "from", "by", "as", "not", "no",
            "i", "you", "he", "she", "we", "they", "my", "your", "his", "her", "our", "their", "what", "which",
            "who", "when", "where", "why", "how", "do", "does", "did", "have", "has", "had", "can", "could",
            "would", "should", "will", "about", "explain", "thanks", "thank", "yes", "hello", "hi", "please",
            "want", "need", "now", "just", "there", "here", "if", "so", "me", "use", "into", "than",
        },
    };

    [GeneratedRegex(@"[\p{L}']+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public LanguageDetection Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new LanguageDetection(null, 0);

        var lower = text.ToLowerInvariant();
        var scores = new Dictionary<string, double> { ["pt"] = 0, ["it"] = 0, ["en"] = 0 };
        var words = 0;

        foreach (Match m in WordRegex().Matches(lower))
        {
            words++;
            var w = m.Value;
            foreach (var (lang, set) in StopWords)
                if (set.Contains(w)) scores[lang] += 1;
            if (w.Length < 5) continue;
            // Morphology: verb/noun endings that are frequent in one language and rare in the others.
            if (w.EndsWith("isco", StringComparison.Ordinal) || w.EndsWith("iamo", StringComparison.Ordinal) ||
                w.EndsWith("ere", StringComparison.Ordinal) || w.EndsWith("zione", StringComparison.Ordinal) ||
                w.EndsWith("zioni", StringComparison.Ordinal) || w.EndsWith("ggio", StringComparison.Ordinal))
                scores["it"] += 0.75;
            else if (w.EndsWith("ção", StringComparison.Ordinal) || w.EndsWith("ções", StringComparison.Ordinal) ||
                     w.EndsWith("ões", StringComparison.Ordinal) || w.EndsWith("amos", StringComparison.Ordinal) ||
                     w.EndsWith("agem", StringComparison.Ordinal) || w.EndsWith("inho", StringComparison.Ordinal))
                scores["pt"] += 0.75;
            else if (w.EndsWith("tion", StringComparison.Ordinal) || w.EndsWith("ness", StringComparison.Ordinal) ||
                     w.EndsWith("ing", StringComparison.Ordinal) || w.EndsWith("ould", StringComparison.Ordinal))
                scores["en"] += 0.75;
        }

        // Orthographic hints: strong, language-exclusive characters / digraphs.
        foreach (var ch in lower)
        {
            switch (ch)
            {
                case 'ã' or 'õ' or 'ç' or 'â' or 'ê' or 'ô': scores["pt"] += 1.5; break;
                case 'ò' or 'ù' or 'ì': scores["it"] += 1.5; break;
            }
        }
        if (lower.Contains("lh") || lower.Contains("nh")) scores["pt"] += 1.5;
        if (lower.Contains("gli") || lower.Contains("cch") || lower.Contains("ggi")) scores["it"] += 1.5;
        if (lower.Contains("th")) scores["en"] += 1;

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
