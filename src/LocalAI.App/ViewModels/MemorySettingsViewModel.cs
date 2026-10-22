using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Memory.Vault;
using LocalAI.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

public sealed record NotesLanguageChoice(string Code, string Name);

/// <summary>
/// Settings › Memory: where the notes live (the app's data\memory folder, or an Obsidian vault the user picks, where
/// the notes are moved) and the language new notes are written in.
/// </summary>
public sealed partial class MemorySettingsViewModel : ObservableObject
{
    private readonly FileVaultStorage _storage;
    private readonly MemoryWorker _worker;
    private readonly UserSettingsStore _settings;
    private readonly LocalAiPaths _paths;
    private readonly MemoryOptions _options;
    private readonly ILogger<MemorySettingsViewModel> _logger;
    private bool _loading;

    public MemorySettingsViewModel(FileVaultStorage storage, MemoryWorker worker, UserSettingsStore settings, LocalAiPaths paths,
        LanguageData languages, IOptions<LocalAiOptions> options, ILogger<MemorySettingsViewModel> logger)
    {
        _storage = storage;
        _worker = worker;
        _settings = settings;
        _paths = paths;
        _options = options.Value.Memory;
        _logger = logger;
        Languages = languages.Languages.Select(l => new NotesLanguageChoice(l.Key, l.Value.Name)).OrderBy(l => l.Name).ToList();
        _loading = true;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == _options.NotesLanguage) ?? Languages.FirstOrDefault(l => l.Code == "en");
        _loading = false;
    }

    public IReadOnlyList<NotesLanguageChoice> Languages { get; }

    /// <summary>Where the notes are now.</summary>
    public string FolderText => _storage.Root;
    public bool UsesVault => !string.IsNullOrWhiteSpace(_options.VaultPath);

    [ObservableProperty] private NotesLanguageChoice? _selectedLanguage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UseAppFolderCommand))] private bool _isMoving;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;

    partial void OnSelectedLanguageChanged(NotesLanguageChoice? value)
    {
        if (_loading || value == null) return;
        _options.NotesLanguage = value.Code;
        _settings.Set("Memory", "NotesLanguage", value.Code);
    }

    /// <summary>Moves the notes into the local-ai folder of the Obsidian vault at <paramref name="vaultPath"/>.</summary>
    public Task UseVaultAsync(string vaultPath) => MoveAsync(vaultPath);

    private bool CanUseAppFolder() => UsesVault && !IsMoving;

    [RelayCommand(CanExecute = nameof(CanUseAppFolder))]
    private Task UseAppFolderAsync() => MoveAsync("");

    /// <summary>
    /// Pauses the background memory work, copies every note to the new place and checks the copies, then switches to
    /// it and removes the old folder. On any failure the notes stay where they were.
    /// </summary>
    private async Task MoveAsync(string vaultPath)
    {
        Error = null;
        Status = null;
        var old = _options.VaultPath;
        if (string.Equals(FileVaultStorage.RootFor(_paths, vaultPath), _storage.Root, StringComparison.OrdinalIgnoreCase)) return;
        IsMoving = true;
        try
        {
            await _worker.PauseAsync();
            await Task.Run(() => _storage.CopyTo(vaultPath));
            _options.VaultPath = vaultPath;
            _settings.Set("Memory", "VaultPath", vaultPath);
            await Task.Run(() =>
            {
                try { FileVaultStorage.DeleteRoot(_paths, old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("The notes were copied but the old folder could not be removed: {Error}", ex.Message);
                }
                _storage.EnsureWatching();
            });
            _logger.LogInformation("Memory notes moved ({Where})", vaultPath.Length == 0 ? "application folder" : "Obsidian vault");
            Status = "Notes moved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move the memory notes");
            Error = $"Could not move the notes: {ex.Message}";
        }
        finally
        {
            _worker.Resume();
            IsMoving = false;
            OnPropertyChanged(nameof(FolderText));
            OnPropertyChanged(nameof(UsesVault));
            UseAppFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
