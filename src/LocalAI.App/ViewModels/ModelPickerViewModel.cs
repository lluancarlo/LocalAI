using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Infrastructure;
using LocalAI.Models;
using Microsoft.Extensions.Logging;

namespace LocalAI.App.ViewModels;

/// <summary>
/// The language model chosen for an assistant: a model of the list (<paramref name="Package"/>), or one found on
/// Hugging Face that is added to the list when the assistant is created (<paramref name="Entry"/>).
/// </summary>
public sealed record ChosenModel(ModelPackage? Package, LlmCatalogEntry? Entry, string Text)
{
    public string Id => Package?.Id ?? Entry!.Id;
}

/// <summary>A row of the picker's list: a section title, a model of the list, or a repository found on Hugging Face.</summary>
public sealed record PickerItem(string Title, string Detail, ModelPackage? Package = null, FoundModel? Found = null)
{
    public bool IsHeader => Package == null && Found == null;
    /// <summary>Published on Hugging Face by the company that made the model.</summary>
    public bool IsOfficial { get; init; }
    public bool IsRecommended { get; init; }
}

/// <summary>A quantization to download, with where it runs on this computer.</summary>
public sealed record QuantizationChoice(Quantization Quantization, ModelFit Fit, string Detail)
{
    public string Text => $"{Quantization.Label} · {Detail}";
    public bool Runs => Fit != ModelFit.TooLarge;
}

/// <summary>
/// The dialog that picks an assistant's language model. It opens on the models for this computer (the catalog's, the
/// recommended one first, then the ones added from Hugging Face); a company or a name searches Hugging Face (GGUF for
/// llama.cpp only). The chosen model shows its details, a link to its repository and, for a repository, its quantizations with
/// where each runs here.
/// </summary>
public sealed partial class ModelPickerViewModel : ObservableObject
{
    private readonly ModelService _models;
    private readonly ModelBrowser _browser;
    private readonly ILogger _logger;
    private readonly int? _vram;
    private readonly long _ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _details;
    private string _revision = "";

    public ModelPickerViewModel(ModelService models, ModelBrowser browser, int? gpuVramMb, string? currentId, ILogger logger)
    {
        _models = models;
        _browser = browser;
        _vram = gpuVramMb;
        _logger = logger;
        Companies = browser.Companies;
        ShowModelsForThisComputer();
        SelectedItem = Items.FirstOrDefault(i => i.Package?.Id == currentId) ?? Items.FirstOrDefault(i => i.IsRecommended);
    }

    public IReadOnlyList<CompanyCatalogEntry> Companies { get; }
    public ObservableCollection<PickerItem> Items { get; } = [];
    public ObservableCollection<QuantizationChoice> Quantizations { get; } = [];

    [ObservableProperty] private CompanyCatalogEntry? _selectedCompany;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanSelect), nameof(IsRepository))] private PickerItem? _selectedItem;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSelect))] private QuantizationChoice? _selectedQuantization;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;

    // The details of the selected model.
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailAuthor = "";
    [ObservableProperty] private string _detailFacts = "";
    [ObservableProperty] private string _detailFit = "";
    [ObservableProperty] private string? _detailWarning;
    [ObservableProperty] private Uri? _repositoryUrl;

    /// <summary>The model the user chose (set by <see cref="SelectCommand"/>); the dialog then closes.</summary>
    public ChosenModel? Result { get; private set; }
    public event EventHandler? CloseRequested;

    public bool HasSelection => SelectedItem is { IsHeader: false };
    /// <summary>A repository found on Hugging Face: the user picks one of its sizes.</summary>
    public bool IsRepository => SelectedItem?.Found != null;
    public bool CanSelect => SelectedItem switch
    {
        { Package: not null } => true,
        { Found.Model.Gated: false } => SelectedQuantization is { Runs: true },
        _ => false,
    };

    /// <summary>The catalog's models (the recommended one first, then the ones that fit) and the ones added before.</summary>
    [RelayCommand]
    private void ShowModelsForThisComputer()
    {
        _search?.Cancel();
        SelectedCompany = null;
        SearchText = "";
        Error = null;
        Status = null;
        Items.Clear();
        var llms = _models.Packages.Where(p => p.Kind == ModelKind.Llm).ToList();
        var recommended = _models.RecommendedLlm(_vram);
        var catalog = llms.Where(p => !p.IsAdded).OrderByDescending(p => p == recommended).ToList();
        Items.Add(new PickerItem("For this computer", ""));
        foreach (var p in catalog) Items.Add(Describe(p) with { IsRecommended = p == recommended });
        var added = llms.Where(p => p.IsAdded).ToList();
        if (added.Count == 0) return;
        Items.Add(new PickerItem("Added from Hugging Face", ""));
        foreach (var p in added) Items.Add(Describe(p));
    }

    private PickerItem Describe(ModelPackage p)
    {
        var state = _models.IsInstalled(p) ? "installed" : $"{ModelsViewModel.FormatSize(p.SizeBytes)} download";
        var repo = p.Files?.Repo ?? RepoOf(_models.LanguageModel(p.Id)?.Url);
        return new PickerItem(p.DisplayName, $"{state} · {FitText(FitOf(p), p.MinVramMb)}", Package: p)
        {
            IsOfficial = p.Official || (repo != null && _browser.IsOfficial(repo.Split('/')[0])),
        };
    }

    /// <summary>A model of the list fits the GPU when its VRAM need does (the same rule as the recommendation).</summary>
    private ModelFit FitOf(ModelPackage p) =>
        _vram is > 0 && p.MinVramMb <= _vram ? ModelFit.Gpu
        : p.MinVramMb > (_vram ?? 0) + _ram * 3 / 4 ? ModelFit.TooLarge
        : _vram is > 0 ? ModelFit.GpuAndCpu : ModelFit.Cpu;

    private static string FitText(ModelFit fit, int neededMb) => fit switch
    {
        ModelFit.Gpu => "fits your GPU",
        ModelFit.GpuAndCpu => $"needs {neededMb / 1000.0:0.#} GB: split between GPU and CPU (slower)",
        ModelFit.Cpu => "runs on the CPU (slow)",
        _ => $"does not run on this PC (needs {neededMb / 1000.0:0.#} GB)",
    };

    partial void OnSelectedCompanyChanged(CompanyCatalogEntry? value)
    {
        if (value == null) return;
        SearchText = "";
        _ = RunSearchAsync(ct => _browser.SearchAsync(value, ct), value.Name);
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return Task.CompletedTask;
        SelectedCompany = null;
        return RunSearchAsync(ct => _browser.SearchAsync(text, ct), $"\"{text}\"");
    }

    private async Task RunSearchAsync(Func<CancellationToken, Task<IReadOnlyList<FoundModel>>> search, string what)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        Items.Clear();
        SelectedItem = null;
        Error = null;
        Status = $"Searching Hugging Face for {what}…";
        IsBusy = true;
        try
        {
            var found = await Task.Run(() => search(cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var f in found) Items.Add(new PickerItem(f.Model.Repo, Summary(f.Model), Found: f) { IsOfficial = f.Official });
            Status = found.Count == 0 ? $"No GGUF models for {what}." : $"{found.Count} models for {what}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hugging Face search failed");
            Status = null;
            Error = $"Could not search Hugging Face: {ex.Message}";
        }
        finally
        {
            if (_search == cts) IsBusy = false;
        }
    }

    private static string Summary(HuggingFaceModel m)
    {
        var parts = new List<string>();
        if (m.Parameters > 0) parts.Add(Parameters(m.Parameters));
        parts.Add($"{Count(m.Downloads)} downloads");
        if (m.Gated) parts.Add("needs a Hugging Face sign-in");
        return string.Join(" · ", parts);
    }

    private static string Parameters(long n) => n >= 1e9 ? $"{n / 1e9:0.#}B parameters" : $"{n / 1e6:0}M parameters";

    private static string Count(long n) =>
        n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 1_000 ? $"{n / 1e3:0.#}K" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    partial void OnSelectedItemChanged(PickerItem? value)
    {
        _details?.Cancel();
        Quantizations.Clear();
        SelectedQuantization = null;
        DetailWarning = null;
        if (value is { IsHeader: true })
        {
            SelectedItem = null; // section titles cannot be chosen
            return;
        }
        if (value?.Package is { } package) ShowPackage(package);
        else if (value?.Found is { } found) _ = ShowRepositoryAsync(found);
    }

    private void ShowPackage(ModelPackage p)
    {
        var entry = _models.LanguageModel(p.Id);
        var repo = p.Files?.Repo ?? RepoOf(entry?.Url);
        DetailTitle = p.DisplayName;
        DetailAuthor = repo ?? "";
        var facts = new List<string>();
        if (entry is { Quantization.Length: > 0 }) facts.Add($"Quantization {entry.Quantization}");
        if (entry is { ParametersB: > 0 }) facts.Add($"{entry.ParametersB:0.#}B parameters");
        facts.Add($"{ModelsViewModel.FormatSize(p.SizeBytes)}{(_models.IsInstalled(p) ? ", installed" : " download")}");
        if (entry != null) facts.Add($"starts with {entry.DefaultContext / 1024}K tokens of context");
        DetailFacts = string.Join(" · ", facts);
        DetailFit = FitText(FitOf(p), p.MinVramMb);
        RepositoryUrl = repo == null ? null : new Uri($"https://{HuggingFaceApi.Host}/{repo}");
    }

    /// <summary>"https://huggingface.co/org/name/resolve/main/file.gguf" is "org/name".</summary>
    internal static string? RepoOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && ModelLibrary.IsHuggingFace(u) && u.Segments.Length >= 3
            ? $"{u.Segments[1].Trim('/')}/{u.Segments[2].Trim('/')}"
            : null;

    private async Task ShowRepositoryAsync(FoundModel found)
    {
        var m = found.Model;
        DetailTitle = m.Name;
        DetailAuthor = m.Repo;
        var facts = new List<string>();
        if (m.Parameters > 0) facts.Add(Parameters(m.Parameters));
        if (m.Architecture.Length > 0) facts.Add($"architecture {m.Architecture}");
        if (m.ContextLength > 0) facts.Add($"trained for {m.ContextLength / 1024}K tokens of context");
        facts.Add($"{Count(m.Downloads)} downloads");
        if (m.License.Length > 0) facts.Add($"license {m.License}");
        DetailFacts = string.Join(" · ", facts);
        DetailFit = "";
        RepositoryUrl = new Uri($"https://{HuggingFaceApi.Host}/{m.Repo}");
        DetailWarning = m.Gated
            ? "This repository asks for a Hugging Face sign-in, which the app does not support: pick another version of the model."
            : found.Official ? null : "Made by the community, not by the company behind the model.";

        var cts = _details = new CancellationTokenSource();
        Status = $"Reading the files of {m.Repo}…";
        IsBusy = true;
        try
        {
            var (revision, quantizations) = await Task.Run(() => _browser.GetQuantizationsAsync(m.Repo, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _revision = revision;
            var context = ModelBrowser.ContextFor(m);
            foreach (var q in quantizations)
            {
                var fit = ModelBrowser.Fit(q.SizeBytes, context, _vram, _ram);
                Quantizations.Add(new QuantizationChoice(q, fit,
                    $"{ModelsViewModel.FormatSize(q.SizeBytes)} · {FitText(fit, VramEstimate.Mb(q.SizeBytes, context))}"));
            }
            var suggested = ModelBrowser.Suggest(quantizations, context, _vram, _ram);
            SelectedQuantization = Quantizations.FirstOrDefault(c => c.Quantization == suggested) ?? Quantizations.FirstOrDefault();
            Status = quantizations.Count == 0 ? "This repository has no GGUF model llama.cpp can run on its own." : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list a Hugging Face repository");
            Status = null;
            Error = $"Could not read the model's files: {ex.Message}";
        }
        finally
        {
            if (_details == cts) IsBusy = false;
        }
    }

    [RelayCommand]
    private void Select()
    {
        if (!CanSelect) return;
        if (SelectedItem?.Package is { } package)
        {
            Result = Chosen(package);
        }
        else if (SelectedItem?.Found is { } found && SelectedQuantization is { } choice)
        {
            // A model found on Hugging Face joins the list (and downloads) only when the assistant is created.
            var entry = _browser.CreateEntry(found, _revision, choice.Quantization);
            Result = (_models.CatalogModelFor(entry) ?? _models.Find(entry.Id)) is { } existing
                ? Chosen(existing)
                : new ChosenModel(null, entry, $"{entry.DisplayName} · {choice.Detail}");
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A model of the list as the assistant form shows it.</summary>
    public ChosenModel Chosen(ModelPackage package)
    {
        var item = Describe(package);
        return new ChosenModel(package, null, $"{item.Title} · {item.Detail}");
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
