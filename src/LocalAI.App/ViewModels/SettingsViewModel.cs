using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

/// <summary>
/// Settings menu: audio devices, echo cancellation, microphone test and voices. Changes apply immediately and are
/// persisted to usersettings.json.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly Dictionary<string, (string Title, string Sample)> LanguageInfo = new()
    {
        ["pt"] = ("Português (Brasil)", "Olá! Esta é a minha voz. Como posso ajudar você hoje?"),
        ["it"] = ("Italiano", "Ciao! Questa è la mia voce. Come posso aiutarti oggi?"),
        ["en"] = ("English", "Hello! This is my voice. How can I help you today?"),
    };

    private readonly VoiceConversationController _voice;
    private readonly IAudioDeviceProvider _devices;
    private readonly IAudioPlayer _player;
    private readonly ITextToSpeech _tts;
    private readonly UserSettingsStore _settings;
    private readonly LocalAiOptions _options;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(VoiceConversationController voice, IAudioDeviceProvider devices, IAudioPlayer player,
        ITextToSpeech tts, UserSettingsStore settings, IOptions<LocalAiOptions> options, ILogger<SettingsViewModel> logger)
    {
        _voice = voice;
        _devices = devices;
        _player = player;
        _tts = tts;
        _settings = settings;
        _options = options.Value;
        _logger = logger;
        _loading = true;
        _echoCancellation = _options.Audio.EchoCancellation;
        _speed = _options.TextToSpeech.Speed;
        _loading = false;
        RefreshDevices();
    }

    /// <summary>Raised after any setting changed (main window refreshes its status line).</summary>
    public event EventHandler? Changed;

    public ObservableCollection<AudioDevice> Microphones { get; } = [];
    public ObservableCollection<AudioDevice> Speakers { get; } = [];
    public ObservableCollection<VoiceLanguageViewModel> Languages { get; } = [];

    [ObservableProperty] private AudioDevice? _selectedMicrophone;
    [ObservableProperty] private AudioDevice? _selectedSpeaker;
    [ObservableProperty] private bool _echoCancellation;
    [ObservableProperty] private bool _isTestingMicrophone;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeedText))] private double _speed;
    [ObservableProperty] private string? _voiceError;

    public string SpeedText => $"{Speed:0.00}×";

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

    // ---------------- Voices ----------------

    /// <summary>Reloads installed voices (called when the panel opens).</summary>
    public void RefreshVoices()
    {
        var available = _tts.AvailableVoices;
        Languages.Clear();
        foreach (var (language, info) in LanguageInfo)
        {
            var options = available.Where(v => v.Language == language).ToList();
            if (options.Count == 0) continue;
            var current = _tts.GetVoice(language);
            Languages.Add(new VoiceLanguageViewModel(this, language, info.Title, info.Sample, options,
                options.FirstOrDefault(o => o.Id == current?.Id) ?? options[0]));
        }
    }

    internal async Task SelectVoiceAsync(VoiceLanguageViewModel language, VoiceInfo voice)
    {
        try
        {
            VoiceError = null;
            await _tts.SetVoiceAsync(language.Language, voice.Id);
            _settings.Set("TextToSpeech", "Voices", _options.TextToSpeech.Voices);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch voice");
            VoiceError = ex.Message;
        }
    }

    internal async Task PreviewAsync(VoiceLanguageViewModel language)
    {
        try
        {
            VoiceError = null;
            _player.Stop();
            var clip = await Task.Run(() => _tts.SynthesizeAsync(language.Sample, language.Language));
            _player.Enqueue(clip);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice preview failed");
            VoiceError = ex.Message;
        }
    }

    partial void OnSpeedChanged(double value)
    {
        if (_loading) return;
        _tts.Speed = (float)value;
        _settings.Set("TextToSpeech", "Speed", Math.Round(value, 2));
    }

    /// <summary>Stops the microphone test when the panel closes.</summary>
    public void OnClosed() => IsTestingMicrophone = false;
}

public sealed partial class VoiceLanguageViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public VoiceLanguageViewModel(SettingsViewModel owner, string language, string title, string sample,
        IReadOnlyList<VoiceInfo> voices, VoiceInfo selected)
    {
        _owner = owner;
        Language = language;
        Title = title;
        Sample = sample;
        Voices = voices;
        _selected = selected;
    }

    public string Language { get; }
    public string Title { get; }
    public string Sample { get; }
    public IReadOnlyList<VoiceInfo> Voices { get; }

    [ObservableProperty] private VoiceInfo _selected;

    partial void OnSelectedChanged(VoiceInfo value) => _ = _owner.SelectVoiceAsync(this, value);

    [RelayCommand]
    private Task PreviewAsync() => _owner.PreviewAsync(this);
}
