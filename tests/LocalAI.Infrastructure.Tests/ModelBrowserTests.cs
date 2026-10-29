using LocalAI.Configuration;
using LocalAI.Models;
using LocalAI.Tests;

namespace LocalAI.Infrastructure.Tests;

public sealed class ModelBrowserTests
{
    private static readonly HuggingFaceFile[] UnslothRepository =
    [
        new("BF16/Qwen3-27B-BF16-00001-of-00002.gguf", 50_000_000_000),
        new("BF16/Qwen3-27B-BF16-00002-of-00002.gguf", 4_600_000_000),
        new("MTP/mtp-Qwen3-27B-Q4_0.gguf", 1_300_000_000),
        new("Qwen3-27B-Q4_0.gguf", 16_000_000_000),
        new("Qwen3-27B-UD-Q4_K_XL.gguf", 17_500_000_000),
        new("Qwen3-27B-UD-IQ2_XXS.gguf", 7_200_000_000),
        new("Q8_0/Qwen3-27B-Q8_0-00001-of-00003.gguf", 10_000_000_000), // a part is missing: not offered
        new("Q8_0/Qwen3-27B-Q8_0-00002-of-00003.gguf", 10_000_000_000),
        new("imatrix_unsloth.gguf", 13_000_000),
        new("mmproj-F16.gguf", 900_000_000),
        new("eagle3-Qwen3-27B-Q8_0.gguf", 900_000_000),
        new("README.md", 10),
    ];

    [Fact]
    public void Quantizations_are_whole_models_smallest_first()
    {
        var quantizations = ModelBrowser.GroupQuantizations(UnslothRepository);

        Assert.Equal(["UD-IQ2_XXS", "Q4_0", "UD-Q4_K_XL", "BF16"], quantizations.Select(q => q.Label));
        var bf16 = quantizations[^1];
        Assert.Equal(["BF16/Qwen3-27B-BF16-00001-of-00002.gguf", "BF16/Qwen3-27B-BF16-00002-of-00002.gguf"], bf16.Files);
        Assert.Equal(54_600_000_000, bf16.SizeBytes);
    }

    [Theory]
    [InlineData(8_000_000_000, 16376, ModelFit.Gpu)]
    [InlineData(20_000_000_000, 16376, ModelFit.GpuAndCpu)]
    [InlineData(20_000_000_000, null, ModelFit.Cpu)]
    [InlineData(200_000_000_000, 16376, ModelFit.TooLarge)]
    public void A_model_runs_on_the_gpu_split_on_the_cpu_or_not_at_all(long size, int? vram, ModelFit fit) =>
        Assert.Equal(fit, ModelBrowser.Fit(size, 8192, vram, systemMemoryMb: 64_000));

    [Fact]
    public void The_largest_quantization_that_fits_the_gpu_is_suggested()
    {
        var quantizations = ModelBrowser.GroupQuantizations(UnslothRepository);
        Assert.Equal("UD-IQ2_XXS", ModelBrowser.Suggest(quantizations, 8192, 12_000, 64_000)!.Label);
        Assert.Equal("UD-Q4_K_XL", ModelBrowser.Suggest(quantizations, 8192, 24_000, 64_000)!.Label);
        // Nothing fits the GPU: the smallest that runs at all.
        Assert.Equal("UD-IQ2_XXS", ModelBrowser.Suggest(quantizations, 8192, 4_000, 64_000)!.Label);
        Assert.Null(ModelBrowser.Suggest(quantizations, 8192, 1_000, 4_000));
    }

    [Fact]
    public void A_new_model_gets_its_own_folder_a_safe_id_and_its_trained_context_within_limits()
    {
        var browser = new ModelBrowser(new ModelCatalog(), new HuggingFaceApi(new ModelDownloader()));
        var found = new FoundModel(new HuggingFaceModel("Qwen/Qwen3-8B-GGUF", 1000, 8_200_000_000, 40960, "qwen3", "apache-2.0", false), true);
        var q4 = new Quantization("Q4_K_M", ["Qwen3-8B-Q4_K_M.gguf"], 5_000_000_000);

        var entry = browser.CreateEntry(found, "abc123", q4);

        Assert.Equal("hf-qwen--qwen3-8b-gguf-q4_k_m", entry.Id);
        Assert.Equal("llm/hf/qwen--qwen3-8b-gguf/q4_k_m", entry.Dir);
        Assert.Equal("llm/hf/qwen--qwen3-8b-gguf/q4_k_m/Qwen3-8B-Q4_K_M.gguf", entry.File);
        Assert.Equal((16384, 8.2, true, "abc123"), (entry.DefaultContext, entry.ParametersB, entry.Official, entry.Revision));
        Assert.Equal(VramEstimate.Mb(5_000_000_000, 16384), entry.MinVramMb);
        Assert.True(entry.IsAdded);
    }

    [Fact]
    public void Added_models_are_kept_and_come_after_the_catalog()
    {
        var paths = new LocalAiPaths(TestPaths.New("added"));
        try
        {
            var added = new AddedLanguageModels(paths);
            var entry = new LlmCatalogEntry { Id = "hf-a", DisplayName = "A", File = "llm/hf/a/a.gguf", Repo = "org/a", Files = ["a.gguf"], Dir = "llm/hf/a" };
            added.Add(entry);
            added.Add(entry with { DisplayName = "A2" }); // replaces it

            var catalog = new ModelCatalog { Llm = [new LlmCatalogEntry { Id = "c", DisplayName = "C", File = "llm/c.gguf" }] };
            Assert.Equal(["c", "hf-a"], new AddedLanguageModels(paths).Merge(catalog).Llm.Select(e => e.Id));
            Assert.Equal("A2", Assert.Single(added.All).DisplayName);

            added.Remove("hf-a");
            Assert.Empty(added.All);
        }
        finally
        {
            if (Directory.Exists(paths.Home)) Directory.Delete(paths.Home, true);
        }
    }

    [Fact]
    public void Every_company_has_searches_and_its_own_organizations()
    {
        var companies = ModelCatalog.LoadDefault().Companies;
        Assert.True(companies.Count >= 15);
        Assert.All(companies, c => Assert.NotEmpty(c.Search));
        Assert.All(companies, c => Assert.NotEmpty(c.Orgs));
        Assert.Contains(companies, c => c.Orgs.Contains("Qwen"));
        Assert.Equal(companies.Count, companies.Select(c => c.Name).Distinct().Count());
    }
}
