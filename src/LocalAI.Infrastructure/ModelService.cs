using System.Collections.Concurrent;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.LLM;
using LocalAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Infrastructure;

/// <summary>
/// Installs and removes models at runtime. A downloaded engine starts right away when the AI needs it (it is on, or
/// was asked to turn on and had no model yet), and engines are released before their files are deleted, so changes
/// take effect without restarting the app. A model used by an assistant (the voices: by any assistant) cannot be removed.
/// </summary>
public sealed class ModelService(
    ModelLibrary library,
    ModelCatalog catalog,
    AddedLanguageModels added,
    IAssistantStore assistants,
    AssistantContext assistant,
    AssistantManager manager,
    AiCompanions companions,
    ILanguageModel llm,
    LlamaCppEmbeddingService embeddings,
    ISpeechToText stt,
    ITextToSpeech tts,
    IOptions<LocalAiOptions> options,
    ILogger<ModelService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly LocalAiOptions _options = options.Value;

    public event EventHandler? Changed;

    public IReadOnlyList<ModelPackage> Packages => library.Packages;

    public bool IsInstalled(ModelPackage package) => library.IsInstalled(package);

    public bool IsDownloading(ModelPackage package) => _downloads.ContainsKey(package.Id);

    /// <summary>Some model is downloading: the controls wait for it.</summary>
    public bool IsAnyDownloading => !_downloads.IsEmpty;

    private readonly ConcurrentDictionary<string, double> _progress = new();

    /// <summary>How far the download of <paramref name="id"/> is (0 to 1), or null when it is not downloading.</summary>
    public double? DownloadProgress(string id) => _downloads.ContainsKey(id) ? _progress.GetValueOrDefault(id) : null;

    private const int ProgressNotifyMs = 250;

    /// <summary>Raised (on any thread, at most four times a second) while a download advances.</summary>
    public event EventHandler? DownloadProgressChanged;

    public bool HasLanguageModel => Packages.Any(p => p.Kind == ModelKind.Llm && IsInstalled(p));

    public ModelPackage? Find(string id) => Packages.FirstOrDefault(p => p.Id == id);

    /// <summary>What the catalog (or the list of added models) says about a language model.</summary>
    public LlmCatalogEntry? LanguageModel(string id) => added.Merge(catalog).Llm.FirstOrDefault(e => e.Id == id);

    private readonly ConcurrentDictionary<string, GgufMetadata?> _metadata = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The context sizes <paramref name="entry"/> can be started with on this computer, each with what it costs and where
    /// it runs: from the model file's own attention once it is downloaded, else from a coarse estimate.
    /// </summary>
    public IReadOnlyList<ContextOption> ContextOptions(LlmCatalogEntry entry, bool compact, int? gpuVramMb)
    {
        var path = library.LanguageModelPath(entry);
        var metadata = File.Exists(path) ? _metadata.GetOrAdd(path, GgufMetadata.TryRead) : null;
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        return ContextPlanner.Options(entry.SizeBytes, entry.TrainedContext, metadata, compact, gpuVramMb, memory);
    }

    /// <summary>The strongest catalog model that fits the GPU (models added from Hugging Face are never recommended).</summary>
    public ModelPackage? RecommendedLlm(int? vramMb)
    {
        var llms = Packages.Where(p => p.Kind == ModelKind.Llm && !p.IsAdded).ToList();
        return llms.FirstOrDefault(p => (vramMb ?? 0) >= p.MinVramMb) ?? llms.LastOrDefault();
    }

    /// <summary>Speech recognition, voice detection, the voices and the memory model: needed by every assistant.</summary>
    public IEnumerable<ModelPackage> SharedPackages =>
        Packages.Where(p => p.Kind is ModelKind.SpeechRecognition or ModelKind.VoiceActivity or ModelKind.Voice or ModelKind.Embedding);

    /// <summary>The speech model, whose speakers are the voices.</summary>
    public ModelPackage? VoicePackage => Packages.FirstOrDefault(p => p.Kind == ModelKind.Voice);

    public IReadOnlyList<VoiceCatalogEntry> Voices => library.Voices;

    public VoiceCatalogEntry? FindVoice(string id) => library.Voices.FirstOrDefault(v => v.Id == id);

    public IEnumerable<ModelPackage> RecommendedSet(int? vramMb) =>
        SharedPackages.Prepend(RecommendedLlm(vramMb)).OfType<ModelPackage>();

    /// <summary>Names of the assistants that use each model or the voices, by package id.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetUsageAsync()
    {
        var all = await assistants.ListAsync().ConfigureAwait(false);
        var voices = VoicePackage?.Id ?? "";
        return all
            .SelectMany(a => new[] { (Id: a.ModelId, a.Name), (Id: FindVoice(a.VoiceId) != null ? voices : "", a.Name) })
            .Where(u => u.Id.Length > 0)
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(u => u.Name).Distinct().ToList());
    }

    /// <summary>Adds a language model found on Hugging Face to the list; <see cref="InstallAsync"/> downloads it.</summary>
    public ModelPackage AddLanguageModel(LlmCatalogEntry entry)
    {
        var package = library.AddLanguageModel(entry);
        logger.LogInformation("Added language model {Model} from {Repo}", entry.Id, entry.Repo);
        Changed?.Invoke(this, EventArgs.Empty);
        return package;
    }

    /// <summary>Takes back a model just added (the assistant it was for could not be created). Its files are not downloaded yet.</summary>
    public void RemoveAddedModel(ModelPackage package)
    {
        if (!package.IsAdded || IsDownloading(package)) return;
        library.Uninstall(package);
        logger.LogInformation("Removed the added language model {Model}", package.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The catalog's own model when <paramref name="entry"/> (found on Hugging Face) is the same repository and file:
    /// then it is the catalog's model (recommended for the hardware, never listed twice).
    /// </summary>
    public ModelPackage? CatalogModelFor(LlmCatalogEntry entry) =>
        catalog.Llm.FirstOrDefault(c => c.Url.Contains($"/{entry.Repo}/resolve/", StringComparison.OrdinalIgnoreCase)
                                        && entry.Files.Count == 1
                                        && Path.GetFileName(c.File).Equals(Path.GetFileName(entry.Files[0]), StringComparison.OrdinalIgnoreCase))
            is { } same ? Find(same.Id) : null;

    public async Task InstallAsync(ModelPackage package, IProgress<double>? progress = null)
    {
        using var cts = new CancellationTokenSource();
        if (!_downloads.TryAdd(package.Id, cts)) return;
        library.AddPending([package]);
        _progress[package.Id] = 0;
        Changed?.Invoke(this, EventArgs.Empty);
        var lastNotified = 0L;
        var tracked = new InlineProgress(p =>
        {
            _progress[package.Id] = p;
            progress?.Report(p);
            // Several files download at once and each reports ten times a second: the screen follows four times a second.
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref lastNotified);
            if (p < 1 && now - last < ProgressNotifyMs) return;
            if (Interlocked.CompareExchange(ref lastNotified, now, last) == last || p >= 1)
                DownloadProgressChanged?.Invoke(this, EventArgs.Empty);
        });
        try
        {
            logger.LogInformation("Downloading {Model} from {Source}", package.Id, package.Source);
            await library.InstallAsync(package, tracked, cts.Token).ConfigureAwait(false);
            library.RemovePending(package);
            logger.LogInformation("Installed {Model}", package.Id);
        }
        finally
        {
            _downloads.TryRemove(package.Id, out _);
            _progress.TryRemove(package.Id, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        await ActivateAsync(package).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops a download; the downloads queued after it are not resumed at the next start either.</summary>
    public void Cancel(ModelPackage package)
    {
        library.ClearPending();
        if (_downloads.TryGetValue(package.Id, out var cts)) cts.Cancel();
    }

    /// <summary>
    /// Downloads the user asked for that did not finish (the app was closed or the network failed). Kept until they
    /// are installed or the user cancels one.
    /// </summary>
    public IReadOnlyList<ModelPackage> PendingDownloads() => library.PendingDownloads();

    public void AddPending(IEnumerable<ModelPackage> packages) => library.AddPending(packages);

    public async Task UninstallAsync(ModelPackage package)
    {
        var usage = await GetUsageAsync().ConfigureAwait(false);
        if (usage.TryGetValue(package.Id, out var users))
            throw new InvalidOperationException($"Used by {string.Join(", ", users)}.");

        await DeactivateAsync(package).ConfigureAwait(false);
        library.Uninstall(package);
        logger.LogInformation("Removed {Model}", package.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Nothing starts while the AI is off: turning it on loads what it needs.</summary>
    private async Task ActivateAsync(ModelPackage package)
    {
        switch (package.Kind)
        {
            // The AI was asked to turn on but had no model yet (e.g. a new assistant whose model was downloading).
            case ModelKind.Llm when llm.State == LanguageModelState.Failed && assistant.Current is { } current:
                await manager.LoadModelAsync(current).ConfigureAwait(false);
                break;
            case ModelKind.Embedding or ModelKind.SpeechRecognition when llm.State == LanguageModelState.Ready:
                await companions.StartAsync(assistant.Current?.Hearing ?? true).ConfigureAwait(false);
                break;
            case ModelKind.Voice when assistant.Current is { } current && FindVoice(current.VoiceId) != null && manager.IsActiveModelLoaded:
                await tts.SetVoiceAsync(current.VoiceId).ConfigureAwait(false);
                break;
        }
    }

    private async Task DeactivateAsync(ModelPackage package)
    {
        switch (package.Kind)
        {
            case ModelKind.Llm when llm.Info?.Id == package.Id:
                await llm.UnloadAsync().ConfigureAwait(false);
                break;
            case ModelKind.Embedding when package.Id == _options.Embedding.Model:
                await embeddings.StopAsync().ConfigureAwait(false);
                break;
            case ModelKind.SpeechRecognition when package.Id == _options.SpeechToText.Model:
                stt.Unload();
                break;
            case ModelKind.Voice:
                tts.Release();
                break;
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose()
    {
        foreach (var cts in _downloads.Values) cts.Cancel();
    }
}
