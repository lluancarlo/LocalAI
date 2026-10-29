using LocalAI.Core.Language;

namespace LocalAI.Core.Tests;

public sealed class LanguageDetectorTests
{
    private readonly HeuristicLanguageDetector _detector = new(LocalAI.Configuration.LanguageData.LoadDefault());

    [Theory]
    [InlineData("Explique async/await em C# para mim, por favor.", "pt")]
    [InlineData("Eu prefiro escrever software em C#, mas também uso Python.", "pt")]
    [InlineData("Você pode me ajudar com uma questão de programação?", "pt")]
    [InlineData("Spiegami come funziona async/await in C#, per favore.", "it")]
    [InlineData("Preferisco scrivere software in C#, ma uso anche Python.", "it")]
    [InlineData("Puoi aiutarmi con una domanda di programmazione?", "it")]
    [InlineData("Che cos'è una variabile in programmazione? Rispondi in due frasi.", "it")]
    [InlineData("O que é uma variável em programação? Responda em duas frases.", "pt")]
    [InlineData("Explain how async/await works in C#, please.", "en")]
    [InlineData("I prefer writing software in C#, but I also use Python.", "en")]
    [InlineData("Can you help me with a programming question?", "en")]
    public void Detects_supported_languages(string text, string expected)
    {
        var result = _detector.Detect(text);
        Assert.Equal(expected, result.Language);
        Assert.True(result.IsConfident, $"confidence {result.Confidence}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345 !!!")]
    public void Returns_unknown_for_text_without_words(string text) =>
        Assert.Null(_detector.Detect(text).Language);

    [Fact]
    public void Short_ambiguous_input_is_not_confident() =>
        Assert.False(_detector.Detect("ok").IsConfident);
}
