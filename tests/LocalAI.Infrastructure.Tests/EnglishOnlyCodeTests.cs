using LocalAI.Tests;
using LocalAI.Configuration;

namespace LocalAI.Infrastructure.Tests;

/// <summary>
/// Rule: application code, comments and UI text are English. Text in other languages is data and lives in
/// languages.json. Accented letters are the reliable sign of foreign text; symbols (→, …, ⚙) are allowed.
/// </summary>
public sealed class EnglishOnlyCodeTests
{
    [Fact]
    public void Application_code_contains_no_foreign_letters()
    {
        var src = Path.Combine(TestPaths.Repository, "src");
        var offenders =
            from file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            where file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal)
            where !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            from line in File.ReadLines(file).Select((text, index) => (text, number: index + 1))
            where line.text.Any(c => c > 127 && char.IsLetter(c))
            select $"{Path.GetRelativePath(src, file)}:{line.number}: {line.text.Trim()}";

        Assert.Empty(offenders);
    }

    [Fact]
    public void Language_data_covers_every_supported_language()
    {
        var data = LanguageData.LoadDefault();
        Assert.Equal(["en", "it", "pt"], data.Languages.Keys.Order());
        Assert.All(data.Languages.Values, l =>
        {
            Assert.NotEmpty(l.Name);
            Assert.NotEmpty(l.VoiceSample);
            Assert.NotEmpty(l.CommonWords);
        });
        Assert.NotEmpty(data.TranscriptNoise);
        Assert.Contains("e.g.", data.Abbreviations);
    }
}
