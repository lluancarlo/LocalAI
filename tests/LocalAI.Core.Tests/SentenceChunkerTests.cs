using LocalAI.Core.Voice;

namespace LocalAI.Core.Tests;

public sealed class SentenceChunkerTests
{
    private static readonly IReadOnlyList<string> Abbreviations = Configuration.LanguageData.LoadDefault().Abbreviations;

    private static List<string> Feed(SentenceChunker chunker, string text, int tokenSize = 3)
    {
        var result = new List<string>();
        for (var i = 0; i < text.Length; i += tokenSize)
            result.AddRange(chunker.Append(text.Substring(i, Math.Min(tokenSize, text.Length - i))));
        result.AddRange(chunker.Complete());
        return result;
    }

    [Fact]
    public void Emits_each_sentence_as_soon_as_it_ends()
    {
        var chunker = new SentenceChunker(Abbreviations);
        Assert.Empty(chunker.Append("Claro. "[..6]));
        var first = chunker.Append(" ");
        Assert.Equal(["Claro."], first);
        Assert.Empty(chunker.Append("Async e await"));
        Assert.Equal(["Async e await funcionam juntos."], chunker.Append(" funcionam juntos. "));
    }

    [Fact]
    public void Splits_question_and_exclamation_and_newlines()
    {
        var chunks = Feed(new SentenceChunker(Abbreviations), "Is it fast? Yes! Very\nfast");
        Assert.Equal(["Is it fast?", "Yes!", "Very", "fast"], chunks);
    }

    [Fact]
    public void First_chunk_may_break_at_comma_for_latency_but_later_ones_do_not()
    {
        var chunks = Feed(new SentenceChunker(Abbreviations, firstChunkMinChars: 20), "A simple way to understand this, is to think of tasks, that run later, without blocking.");
        Assert.Equal("A simple way to understand this,", chunks[0]);
        Assert.Equal("is to think of tasks, that run later, without blocking.", chunks[1]);
    }

    [Fact]
    public void Does_not_split_on_abbreviations_or_decimals()
    {
        var chunks = Feed(new SentenceChunker(Abbreviations), "Use e.g. records. Pi is 3.14 roughly.");
        Assert.Equal(["Use e.g. records.", "Pi is 3.14 roughly."], chunks);
    }

    [Fact]
    public void Drops_fenced_code_blocks_and_strips_markdown()
    {
        var text = "Here is **an example**:\n```csharp\nawait Task.Delay(1000);\n```\nThat's `await` in action.";
        var chunks = Feed(new SentenceChunker(Abbreviations), text, tokenSize: 2);
        Assert.DoesNotContain(chunks, c => c.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.Contains("Here is an example:", chunks);
        Assert.Contains("That's await in action.", chunks);
    }

    [Fact]
    public void Forces_a_break_in_very_long_sentences()
    {
        var longText = string.Join(' ', Enumerable.Repeat("sentence", 80));
        var chunks = Feed(new SentenceChunker(Abbreviations, maxChunkChars: 100), longText);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Length <= 110));
    }

    [Theory]
    [InlineData("## Title", "Title")]
    [InlineData("- item one", "item one")]
    [InlineData("See [the docs](https://example.com) now", "See the docs now")]
    [InlineData("Go to https://example.com please", "Go to please")]
    [InlineData("Great job 🎉!", "Great job !")]
    public void Sanitizer_removes_unspeakable_markup(string input, string expected) =>
        Assert.Equal(expected, SpeechTextSanitizer.Clean(input));
}
