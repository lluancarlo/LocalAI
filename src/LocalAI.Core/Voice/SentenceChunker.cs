using System.Text;

namespace LocalAI.Core.Voice;

/// <summary>
/// Turns an LLM token stream into speakable chunks as early as possible: TTS can start on the first sentence
/// while the model is still generating. Fenced code blocks are dropped (never read aloud).
/// </summary>
public sealed class SentenceChunker
{
    private readonly StringBuilder _buffer = new();
    private readonly HashSet<string> _abbreviations;
    private readonly int _firstChunkMinChars;
    private readonly int _maxChunkChars;
    private bool _inCodeBlock;
    private bool _emittedAny;

    /// <param name="abbreviations">Lower-case abbreviations whose period does not end a sentence (languages.json).</param>
    /// <param name="firstChunkMinChars">The first chunk may break at a comma once it is this long (lower latency).</param>
    /// <param name="maxChunkChars">Force a break at whitespace beyond this length.</param>
    public SentenceChunker(IEnumerable<string> abbreviations, int firstChunkMinChars = 40, int maxChunkChars = 220)
    {
        _abbreviations = new HashSet<string>(abbreviations, StringComparer.Ordinal);
        _firstChunkMinChars = firstChunkMinChars;
        _maxChunkChars = maxChunkChars;
    }

    /// <summary>Appends streamed text and returns any chunks that are now complete.</summary>
    public IReadOnlyList<string> Append(string text)
    {
        var result = new List<string>();
        foreach (var ch in text)
        {
            _buffer.Append(ch);
            if (EndsWithFence())
            {
                if (!_inCodeBlock)
                {
                    // Emit what preceded the code block, then start skipping.
                    _buffer.Length -= 3;
                    Flush(result);
                }
                else
                {
                    _buffer.Clear();
                }
                _inCodeBlock = !_inCodeBlock;
                continue;
            }
            if (_inCodeBlock)
            {
                // Keep only a small tail so the closing fence can be detected.
                if (_buffer.Length > 8) _buffer.Remove(0, _buffer.Length - 3);
                continue;
            }
            TryEmit(result);
        }
        return result;
    }

    /// <summary>Returns whatever remains at end of stream.</summary>
    public IReadOnlyList<string> Complete()
    {
        var result = new List<string>();
        if (_inCodeBlock) _buffer.Clear();
        Flush(result);
        _inCodeBlock = false;
        return result;
    }

    private bool EndsWithFence() =>
        _buffer.Length >= 3 && _buffer[^1] == '`' && _buffer[^2] == '`' && _buffer[^3] == '`';

    private void TryEmit(List<string> result)
    {
        var len = _buffer.Length;
        if (len < 2) return;
        var last = _buffer[^1];
        var prev = _buffer[^2];

        // Sentence end: terminal punctuation followed by whitespace, or a newline.
        var boundary = last == '\n' || (char.IsWhiteSpace(last) && prev is '.' or '!' or '?' or '…' or ';' or ':');
        if (boundary && prev == '.' && EndsWithAbbreviation()) boundary = false;

        if (!boundary && !_emittedAny && len >= _firstChunkMinChars && char.IsWhiteSpace(last) && prev == ',')
            boundary = true;

        if (!boundary && len >= _maxChunkChars && char.IsWhiteSpace(last))
            boundary = true;

        if (boundary) Flush(result);
    }

    private bool EndsWithAbbreviation()
    {
        var text = _buffer.ToString().TrimEnd();
        var lastSpace = text.LastIndexOfAny([' ', '\n', '(']);
        var word = text[(lastSpace + 1)..].ToLowerInvariant();
        return _abbreviations.Contains(word) || (word.Length == 2 && char.IsLetter(word[0]) && word[1] == '.');
    }

    private void Flush(List<string> result)
    {
        var text = SpeechTextSanitizer.Clean(_buffer.ToString());
        _buffer.Clear();
        if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) return;
        result.Add(text);
        _emittedAny = true;
    }
}
