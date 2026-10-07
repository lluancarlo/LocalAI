using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

/// <summary>
/// Settings menu: audio devices, echo cancellation, microphone test, the active assistant's voice style, assistants and
/// models. Changes apply immediately; device settings go to usersettings.json, the voice style to the assistant.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly VoiceConversationController _voice;
    private readonly IAudioDeviceProvider _devices;
    private readonly IAudioPlayer _player;
    private readonly ITextToSpeech _tts;
    private readonly UserSettingsStore _settings;
    private readonly AssistantManager _assistants;
    private readonly AssistantContext _assistant;
    private readonly LocalAiOptions _options;
    private readonly LanguageData _languageData;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public const int ModelsTab = 2;

    public SettingsViewModel(VoiceConversationController voice, IAudioDeviceProvider devices, IAudioPlayer player,
        ITextToSpeech tts, UserSettingsStore settings, AssistantManager assistants, AssistantContext assistant, ModelsViewModel models,
        AssistantsViewModel assistantsViewModel, LanguageData languageData, ModelCatalog catalog, IOptions<LocalAiOptions> options,
        ILogger<SettingsViewModel> logger)
    {
        _catalog = catalog;
        _languageData = languageData;
        Models = models;
        Assistants = assistantsViewModel;
        _assistants = assistants;
        _assistant = assistant;
        _voice = voice;
        _devices = devices;
        _player = player;
        _tts = tts;
        _settings = settings;
        _options = options.Value;
        _logger = logger;
        _loading = true;
        _echoCancellation = _options.Audio.EchoCancellation;
        _loading = false;
        RefreshDevices();
    }

    /// <summary>Raised after any setting changed (main window refreshes its status line).</summary>
    public event EventHandler? Changed;

    public ModelsViewModel Models { get; }
    public AssistantsViewModel Assistants { get; }
    public ObservableCollection<AudioDevice> Microphones { get; } = [];
    public ObservableCollection<AudioDevice> Speakers { get; } = [];

    [ObservableProperty] private AudioDevice? _selectedMicrophone;
    [ObservableProperty] private AudioDevice? _selectedSpeaker;
    [ObservableProperty] private bool _echoCancellation;
    [ObservableProperty] private bool _isTestingMicrophone;
    [ObservableProperty] private string _voiceName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeedText))] private double _speed = VoiceStyle.Default.Speed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PitchText))] private double _pitch;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ExpressivenessText))] private double _expressiveness = VoiceStyle.Default.Expressiveness;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RhythmText))] private double _rhythm = VoiceStyle.Default.Rhythm;
    [ObservableProperty] private string? _voiceError;
    [ObservableProperty] private int _selectedTab;

    public string SpeedText => $"{Speed:0.00}×";
    public string PitchText => Pitch == 0 ? "0" : $"{Pitch:+0;-0}";
    public string ExpressivenessText => $"{Expressiveness:0.00}";
    public string RhythmText => $"{Rhythm:0.00}";

    // ---------------- Devices ----------------

    [RelayCommand]
    private void RefreshDevices()
    {
        _loading = true;
        Microphones.Clear();
        foreach (var d in _devices.GetInputDevices()) Microphones.Add(d);
        Speakers.Clear();
        foreach (var d in _devices.GetOutputDevices()) Speakers.Add(d);
        SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == _options.Audio.InputDeviceId)
                             ?? Microphones.FirstOrDefault(d => d.IsDefault) ?? Microphones.FirstOrDefault();
        SelectedSpeaker = Speakers.FirstOrDefault(d => d.Id == _options.Audio.OutputDeviceId)
                          ?? Speakers.FirstOrDefault(d => d.IsDefault) ?? Speakers.FirstOrDefault();
        _loading = false;
        _ = _voice.SetInputDeviceAsync(SelectedMicrophone?.Id);
        _player.SetDevice(SelectedSpeaker?.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedMicrophoneChanged(AudioDevice? value)
    {
        if (_loading || value == null) return;
        _options.Audio.InputDeviceId = value.Id;
        _settings.Set("Audio", "InputDeviceId", value.Id);
        _ = _voice.SetInputDeviceAsync(value.Id); // reopens the mic if it is in use
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedSpeakerChanged(AudioDevice? value)
    {
        if (_loading || value == null) return;
        _options.Audio.OutputDeviceId = value.Id;
        _settings.Set("Audio", "OutputDeviceId", value.Id);
        _player.SetDevice(value.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnEchoCancellationChanged(bool value)
    {
        if (_loading) return;
        _options.Audio.EchoCancellation = value;
        _settings.Set("Audio", "EchoCancellation", value);
        _ = _voice.ReopenCaptureAsync(); // takes effect immediately if the microphone is open
    }

    partial void OnIsTestingMicrophoneChanged(bool value) =>
        _ = value ? _voice.StartMonitorAsync() : _voice.StopMonitorAsync();

    // ---------------- Voice of the active assistant ----------------

    private CancellationTokenSource? _saveStyle;

    /// <summary>Shows the active assistant's voice and style (called when the page opens or the assistant changes).</summary>
    public void RefreshVoice()
    {
        var profile = _assistant.Current;
        var entry = _catalog.Tts.FirstOrDefault(v => v.Id == profile?.VoiceId);
        var installed = _tts.AvailableVoices.Any(v => v.Id == profile?.VoiceId);
        var name = string.IsNullOrEmpty(entry?.DisplayName) ? profile?.VoiceId : entry.DisplayName;
        VoiceName = profile == null ? "" : installed ? name! : $"{name} · not downloaded yet";
        var style = profile?.VoiceStyle ?? VoiceStyle.Default;
        _loading = true;
        Speed = style.Speed;
        Pitch = style.Pitch;
        Expressiveness = style.Expressiveness;
        Rhythm = style.Rhythm;
        _loading = false;
    }

    private VoiceStyle CurrentStyle => new()
    {
        Speed = Math.Round(Speed, 2),
        Pitch = Math.Round(Pitch),
        Expressiveness = Math.Round(Expressiveness, 2),
        Rhythm = Math.Round(Rhythm, 2),
    };

    partial void OnSpeedChanged(double value) => ScheduleStyleSave();
    partial void OnPitchChanged(double value) => ScheduleStyleSave();
    partial void OnExpressivenessChanged(double value) => ScheduleStyleSave();
    partial void OnRhythmChanged(double value) => ScheduleStyleSave();

    // Sliders fire many changes per second: save (and rebuild the voice engine) once the user pauses.
    private void ScheduleStyleSave()
    {
        if (_loading) return;
        _saveStyle?.Cancel();
        _saveStyle = new CancellationTokenSource();
        _ = SaveStyleAsync(CurrentStyle, _saveStyle.Token);
    }

    private async Task SaveStyleAsync(VoiceStyle style, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
            await _assistants.SaveVoiceStyleAsync(style, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the voice style");
            VoiceError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (_assistant.Current is not { } profile) return;
        try
        {
            VoiceError = null;
            var sample = _languageData.Languages.TryGetValue(profile.Language, out var info) ? info.VoiceSample : profile.Name;
            _player.Stop();
            var clip = await Task.Run(() => _tts.SynthesizeAsync(sample, profile.VoiceId, CurrentStyle));
            _player.Enqueue(clip);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice preview failed");
            VoiceError = ex.Message;
        }
    }

    [RelayCommand]
    private void ResetVoiceStyle()
    {
        _loading = true;
        Speed = VoiceStyle.Default.Speed;
        Pitch = VoiceStyle.Default.Pitch;
        Expressiveness = VoiceStyle.Default.Expressiveness;
        Rhythm = VoiceStyle.Default.Rhythm;
        _loading = false;
        ScheduleStyleSave();
    }

    /// <summary>Stops the microphone test and any shortcut recording when the panel closes.</summary>
    public void OnClosed()
    {
        IsTestingMicrophone = false;
        Assistants.CancelHotkeyRecording();
    }
}
