using System.Collections.Concurrent;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.LLM;
using LocalAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Infrastructure;

/// <summary>
/// Installs and removes models at runtime. Engines are started right after a download and released before their
/// files are deleted, so changes take effect without restarting the app. A model or voice used by an assistant
/// cannot be removed.
/// </summary>
public sealed class ModelService(
    ModelLibrary library,
    IAssistantStore assistants,
    AssistantContext assistant,
    ILanguageModel llm,
    LlamaCppEmbeddingService embeddings,
    ISpeechToText stt,
    ITextToSpeech tts,
    VoiceConversationController voice,
    MemoryService memory,
    IOptions<LocalAiOptions> options,
    ILogger<ModelService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly LocalAiOptions _options = options.Value;

    public event EventHandler? Changed;

    public IReadOnlyList<ModelPackage> Packages => library.Packages;

    public bool IsInstalled(ModelPackage package) => library.IsInstalled(package);

    public bool IsDownloading(ModelPackage package) => _downloads.ContainsKey(package.Id);

    public bool HasLanguageModel => Packages.Any(p => p.Kind == ModelKind.Llm && IsInstalled(p));

    public ModelPackage? Find(string id) => Packages.FirstOrDefault(p => p.Id == id);

    public ModelPackage? RecommendedLlm(int? vramMb)
    {
        var llms = Packages.Where(p => p.Kind == ModelKind.Llm).ToList();
        return llms.FirstOrDefault(p => (vramMb ?? 0) >= p.MinVramMb) ?? llms.LastOrDefault();
    }

    /// <summary>Speech recognition, voice detection and the memory model: needed by every assistant.</summary>
    public IEnumerable<ModelPackage> SharedPackages =>
        Packages.Where(p => p.Kind is ModelKind.SpeechRecognition or ModelKind.VoiceActivity or ModelKind.Embedding);

    public IEnumerable<ModelPackage> RecommendedSet(int? vramMb) =>
        SharedPackages.Prepend(RecommendedLlm(vramMb)).OfType<ModelPackage>();

    /// <summary>Names of the assistants that use each model or voice, by package id.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetUsageAsync()
    {
        var all = await assistants.ListAsync().ConfigureAwait(false);
        return all
            .SelectMany(a => new[] { (Id: a.ModelId, a.Name), (Id: a.VoiceId, a.Name) })
            .Where(u => u.Id.Length > 0)
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(u => u.Name).Distinct().ToList());
    }

    public async Task InstallAsync(ModelPackage package, IProgress<double>? progress = null)
    {
        using var cts = new CancellationTokenSource();
        if (!_downloads.TryAdd(package.Id, cts)) return;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            logger.LogInformation("Downloading {Model} from {Source}", package.Id, package.Source);
            await library.InstallAsync(package, progress, cts.Token).ConfigureAwait(false);
            logger.LogInformation("Installed {Model}", package.Id);
        }
        finally
        {
            _downloads.TryRemove(package.Id, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        await ActivateAsync(package).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel(ModelPackage package)
    {
        if (_downloads.TryGetValue(package.Id, out var cts)) cts.Cancel();
    }

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

    private async Task ActivateAsync(ModelPackage package)
    {
        switch (package.Kind)
        {
            case ModelKind.Llm when llm.State != LanguageModelState.Ready:
                await llm.LoadAsync().ConfigureAwait(false);
                break;
            case ModelKind.Embedding when package.Id == _options.Embedding.Model:
                await embeddings.StartAsync(CancellationToken.None).ConfigureAwait(false);
                if (embeddings.IsAvailable) await memory.BackfillEmbeddingsAsync(CancellationToken.None).ConfigureAwait(false);
                break;
            case ModelKind.SpeechRecognition when package.Id == _options.SpeechToText.Model:
                await stt.InitializeAsync().ConfigureAwait(false);
                if (stt.State == ComponentState.Ready) voice.MarkReady();
                break;
            case ModelKind.Voice when package.Id == assistant.Current?.VoiceId:
                await tts.SetVoiceAsync(package.Id).ConfigureAwait(false);
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
                tts.RemoveVoice(package.Id);
                break;
        }
    }

    public void Dispose()
    {
        foreach (var cts in _downloads.Values) cts.Cancel();
    }
}
