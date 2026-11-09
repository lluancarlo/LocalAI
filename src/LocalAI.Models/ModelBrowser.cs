using System.Text.RegularExpressions;
using LocalAI.Configuration;

namespace LocalAI.Models;

/// <summary>A repository in the search results; <paramref name="Official"/> when the company that made it publishes it.</summary>
public sealed record FoundModel(HuggingFaceModel Model, bool Official);

/// <summary>One quantization of a model: its file, or the parts of a split one.</summary>
public sealed record Quantization(string Label, IReadOnlyList<string> Files, long SizeBytes);

/// <summary>
/// Finds language models on Hugging Face (Settings › Models): the models of a company from the catalog, or any name.
/// Only GGUF repositories llama.cpp can run are listed; the ones the company publishes itself are marked official.
/// </summary>
public sealed partial class ModelBrowser(ModelCatalog catalog, HuggingFaceApi hub)
{
    private const int ResultsPerSearch = 30;
    /// <summary>Context of an added model when its metadata does not say, and the most it starts with.</summary>
    public const int DefaultContext = 8192;
    public const int MaxDefaultContext = 16384;

    public IReadOnlyList<CompanyCatalogEntry> Companies => catalog.Companies;

    /// <summary>The company's own GGUF repositories, then the most downloaded ones that match its models' names.</summary>
    public async Task<IReadOnlyList<FoundModel>> SearchAsync(CompanyCatalogEntry company, CancellationToken ct)
    {
        var found = new List<HuggingFaceModel>();
        foreach (var org in company.Orgs)
            found.AddRange(await hub.SearchAsync("", ResultsPerSearch, ct, org).ConfigureAwait(false));
        foreach (var term in company.Search)
            found.AddRange(await hub.SearchAsync(term, ResultsPerSearch, ct).ConfigureAwait(false));
        return Rank(found);
    }

    public async Task<IReadOnlyList<FoundModel>> SearchAsync(string text, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(text) ? [] : Rank(await hub.SearchAsync(text, ResultsPerSearch, ct).ConfigureAwait(false));

    /// <summary>Official repositories first, then the most downloaded.</summary>
    private List<FoundModel> Rank(IEnumerable<HuggingFaceModel> models) =>
        models.DistinctBy(m => m.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(m => new FoundModel(m, IsOfficial(m.Author)))
            .OrderByDescending(f => f.Official).ThenByDescending(f => f.Model.Downloads)
            .ToList();

    public bool IsOfficial(string author) =>
        catalog.Companies.Any(c => c.Orgs.Contains(author, StringComparer.OrdinalIgnoreCase));

    /// <summary>The repository's quantizations at its current commit, smallest first.</summary>
    public async Task<(string Revision, IReadOnlyList<Quantization> Quantizations)> GetQuantizationsAsync(string repo, CancellationToken ct)
    {
        var revision = await hub.GetRevisionAsync(repo, ct).ConfigureAwait(false);
        var files = await hub.ListFilesAsync(repo, revision, ct).ConfigureAwait(false);
        return (revision, GroupQuantizations(files));
    }

    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();

    [GeneratedRegex(@"(?:^|[-_.])((?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32|MXFP4[A-Z0-9_]*))$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantName();

    /// <summary>
    /// The GGUF files that are a whole model, one per quantization (the parts of a split model together), in
    /// alphabetical order. Vision projectors, importance matrices and draft models (MTP, EAGLE) are not chat models and
    /// are left out.
    /// </summary>
    public static IReadOnlyList<Quantization> GroupQuantizations(IEnumerable<HuggingFaceFile> files) =>
        files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !IsAuxiliary(f.Path))
            .GroupBy(f => SplitPart().Replace(f.Path, ".gguf"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var parts = g.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
                // A split model is whole only with all its parts.
                if (SplitPart().Match(parts[0].Path) is { Success: true } m && int.Parse(m.Groups[2].Value) != parts.Count) return null;
                return new Quantization(Label(g.Key), parts.Select(f => f.Path).ToList(), parts.Sum(f => f.Size));
            })
            .OfType<Quantization>()
            .OrderBy(q => q.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsAuxiliary(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
               || name.Contains("imatrix", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("eagle", StringComparison.OrdinalIgnoreCase)
               || name.Contains("-draft", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("MTP/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Qwen3-8B-UD-Q4_K_XL.gguf" is "UD-Q4_K_XL"; a name without a known quantization stays whole.</summary>
    private static string Label(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return QuantName().Match(name) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : name;
    }

    /// <summary>The context an added model starts with: what it was trained for, within 4K and 16K.</summary>
    public static int ContextFor(HuggingFaceModel model) =>
        model.ContextLength > 0 ? Math.Clamp(model.ContextLength, 4096, MaxDefaultContext) : DefaultContext;

    public static ModelFit Fit(long sizeBytes, int context, int? gpuVramMb, long systemMemoryMb) =>
        ContextPlanner.Fit(VramEstimate.Mb(sizeBytes, context), gpuVramMb, systemMemoryMb);

    /// <summary>The quantization offered first: the largest that fits the GPU, else the smallest that runs at all.</summary>
    public static Quantization? Suggest(IReadOnlyList<Quantization> quantizations, int context, int? gpuVramMb, long systemMemoryMb) =>
        quantizations.Where(q => Fit(q.SizeBytes, context, gpuVramMb, systemMemoryMb) is ModelFit.Gpu).MaxBy(q => q.SizeBytes)
        ?? quantizations.Where(q => Fit(q.SizeBytes, context, gpuVramMb, systemMemoryMb) is not ModelFit.TooLarge).MinBy(q => q.SizeBytes);

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex Unsafe();

    /// <summary>The catalog entry of a model about to be downloaded: where its files go and how it starts.</summary>
    public LlmCatalogEntry CreateEntry(FoundModel found, string revision, Quantization quantization)
    {
        var model = found.Model;
        string Safe(string s) => Unsafe().Replace(s.ToLowerInvariant(), "-").Trim('-', '.');
        var dir = $"llm/hf/{Safe(model.Author)}--{Safe(model.Name)}/{Safe(quantization.Label)}";
        var context = ContextFor(model);
        return new LlmCatalogEntry
        {
            Id = $"hf-{Safe(model.Author)}--{Safe(model.Name)}-{Safe(quantization.Label)}",
            DisplayName = $"{model.Name} · {quantization.Label}",
            File = $"{dir}/{quantization.Files[0]}",
            Url = $"https://{HuggingFaceApi.Host}/{model.Repo}",
            SizeBytes = quantization.SizeBytes,
            Quantization = quantization.Label,
            ParametersB = Math.Round(model.Parameters / 1e9, 1),
            DefaultContext = context,
            TrainedContext = model.ContextLength,
            MinVramMb = VramEstimate.Mb(quantization.SizeBytes, context),
            Repo = model.Repo,
            Revision = revision,
            Files = quantization.Files,
            Dir = dir,
            Official = found.Official,
        };
    }
}
