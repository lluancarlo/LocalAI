using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Infrastructure;
using LocalAI.Models;
using Microsoft.Extensions.Logging;

namespace LocalAI.App.ViewModels;

/// <summary>Settings › Models: download and remove the models stored in the application folder.</summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private static readonly (ModelKind Kind, string Title)[] GroupTitles =
    [
        (ModelKind.Llm, "Language model (Hugging Face)"),
        (ModelKind.SpeechRecognition, "Speech recognition"),
        (ModelKind.VoiceActivity, "Voice detection"),
        (ModelKind.Embedding, "Long-term memory"),
        (ModelKind.Voice, "Voices"),
    ];

    private readonly ModelService _models;
    private readonly StartupService _startup;
    private readonly LocalAiPaths _paths;
    private readonly Dictionary<string, ModelItemViewModel> _items = [];

    public ModelsViewModel(ModelService models, StartupService startup, LocalAiPaths paths, ILogger<ModelsViewModel> logger)
    {
        _models = models;
        _startup = startup;
        _paths = paths;
        Logger = logger;
        foreach (var (kind, title) in GroupTitles)
        {
            var items = models.Packages.Where(p => p.Kind == kind).Select(p => _items[p.Id] = new ModelItemViewModel(this, p)).ToList();
            if (items.Count > 0) Groups.Add(new ModelGroupViewModel(title, items));
        }
        _models.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    internal ModelService Service => _models;
    internal ILogger Logger { get; }

    public ObservableCollection<ModelGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private string _storageText = "";
    [ObservableProperty] private string _recommendedText = "";
    [ObservableProperty] private bool _canInstallRecommended;

    public void Refresh()
    {
        var vram = _startup.Gpu?.TotalMemoryMb;
        var recommendedLlm = _models.RecommendedLlm(vram);
        foreach (var item in _items.Values) item.Refresh(item.Package == recommendedLlm);

        var missing = _models.RecommendedSet(vram).Where(p => !_models.IsInstalled(p)).ToList();
        CanInstallRecommended = missing.Count > 0;
        RecommendedText = $"{missing.Count} items, {FormatSize(missing.Sum(p => p.SizeBytes))}";

        var free = new DriveInfo(Path.GetPathRoot(_paths.Home)!).AvailableFreeSpace;
        StorageText = $"Stored in {_paths.ModelsDirectory} ({FormatSize(free)} free)";
        _ = RefreshUsageAsync();
    }

    private async Task RefreshUsageAsync()
    {
        try
        {
            var usage = await _models.GetUsageAsync();
            foreach (var item in _items.Values)
                item.UsedBy = usage.TryGetValue(item.Package.Id, out var names) ? string.Join(", ", names) : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read which assistants use each model");
        }
    }

    /// <summary>Downloads the package unless it is installed (joins a download already running). Returns whether it is installed.</summary>
    public async Task<bool> EnsureInstalledAsync(ModelPackage package)
    {
        var item = _items[package.Id];
        if (_models.IsInstalled(package)) return true;
        if (item.DownloadCommand.ExecutionTask is { IsCompleted: false } running) await running;
        else await item.DownloadCommand.ExecuteAsync(null);
        return _models.IsInstalled(package);
    }

    [RelayCommand]
    private async Task InstallRecommendedAsync()
    {
        foreach (var package in _models.RecommendedSet(_startup.Gpu?.TotalMemoryMb).Where(p => !_models.IsInstalled(p)).ToList())
        {
            if (!await EnsureInstalledAsync(package)) return;
        }
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{Math.Max(1, bytes / 1e6):0} MB";
}

public sealed record ModelGroupViewModel(string Title, IReadOnlyList<ModelItemViewModel> Items);

public sealed partial class ModelItemViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private CancellationTokenSource? _confirmTimeout;

    public ModelItemViewModel(ModelsViewModel owner, ModelPackage package)
    {
        _owner = owner;
        Package = package;
        var size = ModelsViewModel.FormatSize(package.SizeBytes);
        Detail = package.Kind != ModelKind.Llm ? size
            : package.MinVramMb == 0 ? $"{size} · fits any GPU"
            : $"{size} · needs {package.MinVramMb / 1000.0:0} GB of VRAM";
    }

    public ModelPackage Package { get; }
    public string Title => Package.DisplayName;
    public string Detail { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanRemove))] private bool _isInstalled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownload))] private bool _isDownloading;
    /// <summary>Names of the assistants using this model or voice; such packages cannot be removed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRemove), nameof(IsUsed), nameof(UsedByText))] private string? _usedBy;
    [ObservableProperty] private bool _isRecommended;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RemoveText))] private bool _confirmRemove;

    public bool CanDownload => !IsInstalled && !IsDownloading;
    public bool IsUsed => UsedBy != null;
    public bool CanRemove => IsInstalled && !IsUsed;
    public string UsedByText => $"used by {UsedBy}";
    public string RemoveText => ConfirmRemove ? "Confirm" : "Remove";

    internal void Refresh(bool recommended)
    {
        IsInstalled = _owner.Service.IsInstalled(Package);
        IsDownloading = _owner.Service.IsDownloading(Package);
        IsRecommended = recommended && !IsInstalled;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        Error = null;
        Progress = 0;
        ProgressText = "Starting…";
        var progress = new Progress<double>(p =>
        {
            Progress = p;
            ProgressText = $"{p:P0} of {ModelsViewModel.FormatSize(Package.SizeBytes)}";
        });
        try
        {
            await Task.Run(() => _owner.Service.InstallAsync(Package, progress));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _owner.Logger.LogError(ex, "Download of {Model} failed", Package.Id);
            Error = $"Download failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => _owner.Service.Cancel(Package);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveAsync()
    {
        if (!ConfirmRemove)
        {
            ConfirmRemove = true;
            _confirmTimeout = new CancellationTokenSource();
            try { await Task.Delay(TimeSpan.FromSeconds(4), _confirmTimeout.Token); }
            catch (OperationCanceledException) { return; }
            ConfirmRemove = false;
            return;
        }
        _confirmTimeout?.Cancel();
        ConfirmRemove = false;
        Error = null;
        try
        {
            await Task.Run(() => _owner.Service.UninstallAsync(Package));
        }
        catch (Exception ex)
        {
            _owner.Logger.LogError(ex, "Removing {Model} failed", Package.Id);
            Error = $"Could not remove: {ex.Message}";
        }
    }
}
