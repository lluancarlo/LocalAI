using System.Text.Json;
using LocalAI.Configuration;

namespace LocalAI.Models;

/// <summary>
/// The models the app can use (from the catalog, plus the language models the user added from Hugging Face) and their
/// state on disk. Downloads are staged in <see cref="LocalAiPaths.DownloadsDirectory"/> and moved into place only when
/// complete, so an installed model is always whole. Language models and the speech model are accepted only from
/// Hugging Face.
/// </summary>
public sealed class ModelLibrary
{
    public const string HuggingFaceHost = "huggingface.co";
    private const int ParallelFiles = 4;

    private readonly LocalAiPaths _paths;
    private readonly ModelDownloader _downloader;
    private readonly HuggingFaceApi _hub;
    private readonly AddedLanguageModels _added;
    private readonly IReadOnlyList<ModelPackage> _catalogPackages;
    private readonly string _ttsDirectory;
    private readonly Lock _pendingGate = new();

    public ModelLibrary(ModelCatalog catalog, LocalAiPaths paths, ModelDownloader downloader, AddedLanguageModels? added = null)
    {
        _paths = paths;
        _downloader = downloader;
        _hub = new HuggingFaceApi(downloader);
        _added = added ?? new AddedLanguageModels(paths);
        _catalogPackages = BuildPackages(catalog, paths.ModelsDirectory);
        Voices = catalog.Voices;
        _ttsDirectory = Path.GetFullPath(Path.Combine(paths.ModelsDirectory, catalog.Tts.Dir));
    }

    /// <summary>The catalog's models, then the language models the user added.</summary>
    public IReadOnlyList<ModelPackage> Packages =>
        [.. _catalogPackages, .. _added.All.Where(e => _catalogPackages.All(p => p.Id != e.Id)).Select(e => AddedPackage(e, _paths.ModelsDirectory))];

    /// <summary>Adds a language model found on Hugging Face (not downloaded yet) and returns its package.</summary>
    public ModelPackage AddLanguageModel(LlmCatalogEntry entry)
    {
        if (!entry.IsAdded || !HuggingFaceApi.IsValidRepo(entry.Repo) || entry.Files.Count == 0)
            throw new ArgumentException("Not a Hugging Face model.", nameof(entry));
        _added.Add(entry);
        return AddedPackage(entry, _paths.ModelsDirectory);
    }

    /// <summary>The voices of the speech model (<see cref="ModelKind.Voice"/>): installed with it.</summary>
    public IReadOnlyList<VoiceCatalogEntry> Voices { get; }

    /// <summary>The model file of a language model (the first part of a split one), whether downloaded or not.</summary>
    public string LanguageModelPath(LlmCatalogEntry entry) => Path.GetFullPath(Path.Combine(_paths.ModelsDirectory, entry.File));

    public bool IsInstalled(ModelPackage package) =>
        package.Files != null ? Directory.Exists(package.InstallPath) : File.Exists(package.InstallPath);

    public async Task InstallAsync(ModelPackage package, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled(package)) return;
        var staging = StagingDirectory(package);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.GetDirectoryName(package.InstallPath)!);

        if (package.Files is { } files)
        {
            var content = Path.Combine(staging, "content");
            await DownloadFilesAsync(files, content, progress, ct).ConfigureAwait(false);
            Directory.Move(content, package.InstallPath);
        }
        else
        {
            var download = Path.Combine(staging, Path.GetFileName(package.Source.AbsolutePath));
            await _downloader.DownloadAsync(package.Source, download, package.SizeBytes, progress, ct).ConfigureAwait(false);
            File.Move(download, package.InstallPath);
        }

        Directory.Delete(staging, recursive: true);
    }

    /// <summary>Deletes the model's files; a model the user added also leaves the list.</summary>
    public void Uninstall(ModelPackage package)
    {
        if (package.Files != null && Directory.Exists(package.InstallPath)) Directory.Delete(package.InstallPath, recursive: true);
        else if (File.Exists(package.InstallPath)) File.Delete(package.InstallPath);

        var staging = StagingDirectory(package);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        if (package.IsAdded)
        {
            _added.Remove(package.Id);
            RemovePending(package);
            DeleteEmptyParents(package.InstallPath, Path.Combine(_paths.ModelsDirectory, "llm"));
        }
    }

    private static void DeleteEmptyParents(string path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); dir != null && dir.Length > stop.Length; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Deletes the speech models the app no longer uses (the old Piper voices, older Kokoro folders): every folder in
    /// the speech model's parent folder but the current one.
    /// </summary>
    public IReadOnlyList<string> RemoveObsoleteVoices()
    {
        var parent = Path.GetDirectoryName(_ttsDirectory);
        if (parent == null || !Directory.Exists(parent)) return [];
        var removed = new List<string>();
        foreach (var dir in Directory.GetDirectories(parent))
        {
            if (string.Equals(Path.GetFullPath(dir), _ttsDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(dir, recursive: true);
            removed.Add(Path.GetFileName(dir));
        }
        return removed;
    }

    /// <summary>
    /// The downloads the user started that have not finished (the app was closed, or the network failed), in the order
    /// they were asked for. Before this list existed, an interrupted download only left its staging folder: such a
    /// download is pending, and so is what every assistant needs when it is a language model (the rest of its set).
    /// </summary>
    public IReadOnlyList<ModelPackage> PendingDownloads()
    {
        lock (_pendingGate)
        {
            var ids = ReadPending();
            if (ids == null)
            {
                var interrupted = Packages.Where(p => Directory.Exists(StagingDirectory(p))).ToList();
                ids = interrupted.Any(p => p.Kind == ModelKind.Llm)
                    ? interrupted.Concat(Packages.Where(p => p.Kind is not ModelKind.Llm)).Select(p => p.Id).Distinct().ToList()
                    : interrupted.Select(p => p.Id).ToList();
            }
            return ids.Select(id => Packages.FirstOrDefault(p => p.Id == id)).OfType<ModelPackage>().Where(p => !IsInstalled(p)).ToList();
        }
    }

    /// <summary>Remembers downloads the user asked for until they finish (<see cref="RemovePending"/>).</summary>
    public void AddPending(IEnumerable<ModelPackage> packages)
    {
        lock (_pendingGate)
        {
            var ids = PendingIds();
            ids.AddRange(packages.Select(p => p.Id).Where(id => !ids.Contains(id)));
            WritePending(ids);
        }
    }

    public void RemovePending(ModelPackage package)
    {
        lock (_pendingGate)
        {
            var ids = PendingIds();
            if (ids.Remove(package.Id)) WritePending(ids);
        }
    }

    /// <summary>The user stopped a download: nothing is resumed until they start one again.</summary>
    public void ClearPending()
    {
        lock (_pendingGate) WritePending([]);
    }

    private List<string> PendingIds() => PendingDownloads().Select(p => p.Id).ToList();

    private List<string>? ReadPending()
    {
        try
        {
            return File.Exists(_paths.PendingDownloadsPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_paths.PendingDownloadsPath)) ?? []
                : null;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WritePending(List<string> ids)
    {
        Directory.CreateDirectory(_paths.DownloadsDirectory);
        File.WriteAllText(_paths.PendingDownloadsPath, JsonSerializer.Serialize(ids));
    }

    public static bool IsHuggingFace(Uri source) =>
        source.Scheme == Uri.UriSchemeHttps && source.Host.Equals(HuggingFaceHost, StringComparison.OrdinalIgnoreCase);

    private string StagingDirectory(ModelPackage package) => Path.Combine(_paths.DownloadsDirectory, package.Id);

    /// <summary>
    /// Lists the repository at the pinned revision, then downloads the wanted files (a few at a time) into
    /// <paramref name="target"/>. Files already complete there (an interrupted install) are kept.
    /// </summary>
    private async Task DownloadFilesAsync(HuggingFaceFiles files, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var wanted = (await _hub.ListFilesAsync(files.Repo, files.Revision, ct).ConfigureAwait(false))
            .Where(f => files.Paths.Any(p => p.EndsWith('/') ? f.Path.StartsWith(p, StringComparison.Ordinal) : f.Path == p))
            .ToList();
        var missing = files.Paths.Where(p => !p.EndsWith('/') && wanted.All(f => f.Path != p)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"Not in {files.Repo}: {string.Join(", ", missing)}.");

        Directory.CreateDirectory(target);
        var total = Math.Max(1, wanted.Sum(f => f.Size));
        var done = new double[wanted.Count];
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        await Parallel.ForEachAsync(Enumerable.Range(0, wanted.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles, CancellationToken = ct }, async (i, token) =>
            {
                var file = wanted[i];
                var destination = Path.GetFullPath(Path.Combine(target, file.Path));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unexpected file name in {files.Repo}: {file.Path}");
                if (!File.Exists(destination) || new FileInfo(destination).Length != file.Size)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var fileProgress = new InlineProgress(p =>
                    {
                        Volatile.Write(ref done[i], p * file.Size);
                        progress?.Report(done.Sum() / total);
                    });
                    await _downloader.DownloadAsync(HuggingFaceApi.FileUri(files.Repo, files.Revision, file.Path), destination, file.Size, fileProgress, token).ConfigureAwait(false);
                }
                Volatile.Write(ref done[i], file.Size);
                progress?.Report(done.Sum() / total);
            }).ConfigureAwait(false);
        progress?.Report(1);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A model added from Hugging Face: its files go into its own folder.</summary>
    private static ModelPackage AddedPackage(LlmCatalogEntry e, string modelsDirectory) => new()
    {
        Id = e.Id, Kind = ModelKind.Llm, DisplayName = e.DisplayName, Source = new Uri($"https://{HuggingFaceHost}/{e.Repo}"),
        InstallPath = Path.GetFullPath(Path.Combine(modelsDirectory, e.Dir)), SizeBytes = e.SizeBytes, MinVramMb = e.MinVramMb,
        Files = new HuggingFaceFiles(e.Repo, e.Revision, e.Files), IsAdded = true, Official = e.Official,
    };

    private static List<ModelPackage> BuildPackages(ModelCatalog catalog, string modelsDirectory)
    {
        string Full(string relative) => Path.GetFullPath(Path.Combine(modelsDirectory, relative));

        var packages = new List<ModelPackage>();
        foreach (var e in catalog.Llm.Where(e => Uri.TryCreate(e.Url, UriKind.Absolute, out var u) && IsHuggingFace(u)))
            packages.Add(new ModelPackage
            {
                Id = e.Id, Kind = ModelKind.Llm, DisplayName = e.DisplayName, Source = new Uri(e.Url),
                InstallPath = Full(e.File), SizeBytes = e.SizeBytes, MinVramMb = e.MinVramMb,
            });
        foreach (var e in catalog.Whisper) packages.Add(FilePackage(e, ModelKind.SpeechRecognition));
        foreach (var e in catalog.Vad) packages.Add(FilePackage(e, ModelKind.VoiceActivity));
        foreach (var e in catalog.Embedding) packages.Add(FilePackage(e, ModelKind.Embedding));
        if (catalog.Tts is { Id.Length: > 0, Repo.Length: > 0, Dir.Length: > 0 } tts)
            packages.Add(new ModelPackage
            {
                Id = tts.Id, Kind = ModelKind.Voice, DisplayName = string.IsNullOrEmpty(tts.DisplayName) ? tts.Id : tts.DisplayName,
                Source = new Uri($"https://{HuggingFaceHost}/{tts.Repo}"), InstallPath = Full(tts.Dir), SizeBytes = tts.SizeBytes,
                Files = new HuggingFaceFiles(tts.Repo, tts.Revision, tts.Files),
            });
        return packages;

        ModelPackage FilePackage(FileCatalogEntry e, ModelKind kind) => new()
        {
            Id = e.Id, Kind = kind, DisplayName = string.IsNullOrEmpty(e.DisplayName) ? e.Id : e.DisplayName,
            Source = new Uri(e.Url), InstallPath = Full(e.File), SizeBytes = e.SizeBytes,
        };
    }
}
