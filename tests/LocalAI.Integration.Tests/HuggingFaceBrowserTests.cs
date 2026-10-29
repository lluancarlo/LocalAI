using LocalAI.Configuration;
using LocalAI.Models;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>The model search against the real Hugging Face API (needs the Internet; nothing is downloaded).</summary>
[Trait("Category", "Integration")]
public sealed class HuggingFaceBrowserTests(ITestOutputHelper output)
{
    private readonly ModelBrowser _browser = new(ModelCatalog.LoadDefault(), new HuggingFaceApi(new ModelDownloader()));

    [Theory]
    [InlineData("Alibaba (Qwen)")]
    [InlineData("Google (Gemma)")]
    [InlineData("Mistral AI")]
    public async Task A_company_lists_its_official_models_first(string name)
    {
        var company = _browser.Companies.Single(c => c.Name == name);
        var found = await _browser.SearchAsync(company, CancellationToken.None);
        foreach (var f in found.Take(8))
            output.WriteLine($"{(f.Official ? "[OFFICIAL] " : "")}{f.Model.Repo} · {f.Model.Parameters / 1e9:0.#}B · {f.Model.Downloads} downloads");
        Assert.True(found[0].Official, $"{name}: {found[0].Model.Repo}");
    }

    [Fact]
    public async Task A_repository_lists_its_quantizations()
    {
        var found = await _browser.SearchAsync("gpt-oss-20b", CancellationToken.None);
        var repo = found.First(f => f.Model.Repo.Equals("ggml-org/gpt-oss-20b-GGUF", StringComparison.OrdinalIgnoreCase));
        var (revision, quantizations) = await _browser.GetQuantizationsAsync(repo.Model.Repo, CancellationToken.None);
        foreach (var q in quantizations) output.WriteLine($"{q.Label} · {q.SizeBytes / 1e9:0.0} GB · {string.Join(", ", q.Files)}");
        Assert.Matches("^[0-9a-f]{40}$", revision);
        Assert.Equal("MXFP4", Assert.Single(quantizations).Label); // the EAGLE draft models are not offered
    }
}
