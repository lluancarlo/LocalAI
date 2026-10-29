using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAI.Models;

/// <summary>A file of a Hugging Face repository.</summary>
public sealed record HuggingFaceFile(string Path, long Size);

/// <summary>A model repository found on Hugging Face, with what its GGUF metadata says.</summary>
/// <param name="Parameters">Total parameters (0 when unknown).</param>
/// <param name="ContextLength">The context the model was trained for (0 when unknown).</param>
/// <param name="Gated">The repository asks visitors to sign in and accept terms: the app cannot download it.</param>
public sealed record HuggingFaceModel(string Repo, long Downloads, long Parameters, int ContextLength, string Architecture,
    string License, bool Gated)
{
    public string Author => Repo.Split('/')[0];
    public string Name => Repo.Split('/')[^1];
}

/// <summary>
/// The public Hugging Face API, anonymous (no account or key): search GGUF models for llama.cpp, list a repository's
/// files. Called only when the user searches or downloads.
/// </summary>
public sealed partial class HuggingFaceApi(ModelDownloader downloader)
{
    public const string Host = "huggingface.co";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoName();

    public static bool IsValidRepo(string repo) => RepoName().IsMatch(repo) && !repo.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The most downloaded GGUF repositories llama.cpp can run whose name matches <paramref name="text"/> (any, when
    /// empty), of <paramref name="author"/> when given.
    /// </summary>
    public async Task<IReadOnlyList<HuggingFaceModel>> SearchAsync(string text, int limit, CancellationToken ct, string? author = null)
    {
        var query = $"filter=gguf&apps=llama.cpp&sort=downloads&direction=-1&limit={limit}" +
                    "&expand[]=gguf&expand[]=downloads&expand[]=gated&expand[]=cardData";
        if (text.Trim().Length > 0) query += $"&search={Uri.EscapeDataString(text.Trim())}";
        if (author != null) query += $"&author={Uri.EscapeDataString(author)}";
        var url = new Uri($"https://{Host}/api/models?{query}");
        var (json, _) = await downloader.GetPageAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<HuggingFaceModel>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var repo = m.GetProperty("id").GetString() ?? "";
            if (!IsValidRepo(repo)) continue;
            var gguf = m.TryGetProperty("gguf", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
            result.Add(new HuggingFaceModel(
                repo,
                Long(m, "downloads"),
                gguf.ValueKind == JsonValueKind.Object ? Long(gguf, "total") : 0,
                gguf.ValueKind == JsonValueKind.Object ? (int)Math.Min(int.MaxValue, Long(gguf, "context_length")) : 0,
                gguf.ValueKind == JsonValueKind.Object ? Text(gguf, "architecture") : "",
                m.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object ? Text(card, "license") : "",
                m.TryGetProperty("gated", out var gated) && gated.ValueKind == JsonValueKind.String));
        }
        return result;
    }

    /// <summary>The repository's current commit, so its files are downloaded exactly as they were listed.</summary>
    public async Task<string> GetRevisionAsync(string repo, CancellationToken ct)
    {
        Check(repo);
        var (json, _) = await downloader.GetPageAsync(new Uri($"https://{Host}/api/models/{repo}?expand[]=sha"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return Text(doc.RootElement, "sha") is { Length: > 0 } sha ? sha : throw new InvalidDataException($"No revision for {repo}.");
    }

    /// <summary>Every file of the repository at the revision (the API returns it in pages).</summary>
    public async Task<IReadOnlyList<HuggingFaceFile>> ListFilesAsync(string repo, string revision, CancellationToken ct)
    {
        Check(repo);
        var result = new List<HuggingFaceFile>();
        Uri? page = new($"https://{Host}/api/models/{repo}/tree/{Uri.EscapeDataString(revision)}?recursive=true");
        while (page != null)
        {
            if (!ModelLibrary.IsHuggingFace(page)) throw new InvalidDataException($"Unexpected address while listing {repo}.");
            var (json, next) = await downloader.GetPageAsync(page, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("type").GetString() != "file") continue;
                result.Add(new HuggingFaceFile(entry.GetProperty("path").GetString()!, entry.GetProperty("size").GetInt64()));
            }
            page = next;
        }
        return result;
    }

    public static Uri FileUri(string repo, string revision, string path) =>
        new($"https://{Host}/{repo}/resolve/{Uri.EscapeDataString(revision)}/" +
            string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    private static void Check(string repo)
    {
        if (!IsValidRepo(repo)) throw new ArgumentException($"Not a Hugging Face repository: {repo}", nameof(repo));
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
