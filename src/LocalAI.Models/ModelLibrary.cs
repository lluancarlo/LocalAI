using System.Formats.Tar;
using ICSharpCode.SharpZipLib.BZip2;
using LocalAI.Configuration;

namespace LocalAI.Models;

/// <summary>
/// The models the app can use (from the catalog) and their state on disk. Downloads are staged in
/// <see cref="LocalAiPaths.DownloadsDirectory"/> and moved into place only when complete, so an installed model is
/// always whole. Language models are accepted only from Hugging Face.
/// </summary>
public sealed class ModelLibrary
{
    public const string HuggingFaceHost = "huggingface.co";

    private readonly LocalAiPaths _paths;
    private readonly ModelDownloader _downloader;

    public ModelLibrary(ModelCatalog catalog, LocalAiPaths paths, ModelDownloader downloader)
    {
        _paths = paths;
        _downloader = downloader;
        Packages = BuildPackages(catalog, paths.ModelsDirectory);
    }

    public IReadOnlyList<ModelPackage> Packages { get; }

    public bool IsInstalled(ModelPackage package) =>
        package.IsArchive ? Directory.Exists(package.InstallPath) : File.Exists(package.InstallPath);

    public async Task InstallAsync(ModelPackage package, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled(package)) return;
        var staging = StagingDirectory(package);
        Directory.CreateDirectory(staging);

        var download = Path.Combine(staging, Path.GetFileName(package.Source.AbsolutePath));
        await _downloader.DownloadAsync(package.Source, download, package.SizeBytes, progress, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(package.InstallPath)!);
        if (package.IsArchive)
            await Task.Run(() => ExtractTarBz2(download, Path.Combine(staging, "content"), package.InstallPath), ct).ConfigureAwait(false);
        else
            File.Move(download, package.InstallPath);

        Directory.Delete(staging, recursive: true);
    }

    public void Uninstall(ModelPackage package)
    {
        if (package.IsArchive && Directory.Exists(package.InstallPath)) Directory.Delete(package.InstallPath, recursive: true);
        else if (File.Exists(package.InstallPath)) File.Delete(package.InstallPath);

        var staging = StagingDirectory(package);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    }

    public static bool IsHuggingFace(Uri source) =>
        source.Scheme == Uri.UriSchemeHttps && source.Host.Equals(HuggingFaceHost, StringComparison.OrdinalIgnoreCase);

    private string StagingDirectory(ModelPackage package) => Path.Combine(_paths.DownloadsDirectory, package.Id);

    private static void ExtractTarBz2(string archive, string extractTo, string installPath)
    {
        if (Directory.Exists(extractTo)) Directory.Delete(extractTo, recursive: true);
        Directory.CreateDirectory(extractTo);
        using (var file = File.OpenRead(archive))
        using (var bzip = new BZip2InputStream(file))
            TarFile.ExtractToDirectory(bzip, extractTo, overwriteFiles: true);

        var entries = Directory.GetFileSystemEntries(extractTo);
        var root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : extractTo;
        Directory.Move(root, installPath);
    }

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
        foreach (var e in catalog.Tts)
            packages.Add(new ModelPackage
            {
                Id = e.Id, Kind = ModelKind.Voice, DisplayName = string.IsNullOrEmpty(e.DisplayName) ? e.Id : e.DisplayName,
                Source = new Uri(e.Url), InstallPath = Full(e.Dir), SizeBytes = e.SizeBytes, IsArchive = true,
                Language = e.Language, IsDefault = e.Default,
            });
        return packages;

        ModelPackage FilePackage(FileCatalogEntry e, ModelKind kind) => new()
        {
            Id = e.Id, Kind = kind, DisplayName = string.IsNullOrEmpty(e.DisplayName) ? e.Id : e.DisplayName,
            Source = new Uri(e.Url), InstallPath = Full(e.File), SizeBytes = e.SizeBytes,
        };
    }
}
