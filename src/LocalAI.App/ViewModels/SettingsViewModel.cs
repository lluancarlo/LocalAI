using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

/// <summary>
/// Settings menu: audio devices, echo cancellation, microphone test, assistants, models and where the memory notes
/// live. Changes apply immediately and are saved to usersettings.json. An assistant's voice style is chosen when it is
/// created (the new assistant dialog) and does not change afterwards.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly VoiceConversationController _voice;
    private readonly IAudioDeviceProvider _devices;
    private readonly IAudioPlayer _player;
    private readonly UserSettingsStore _settings;
    private readonly LocalAiOptions _options;
    private bool _loading;

    public const int ModelsTab = 2;

    public SettingsViewModel(VoiceConversationController voice, IAudioDeviceProvider devices, IAudioPlayer player,
        UserSettingsStore settings, ModelsViewModel models, AssistantsViewModel assistantsViewModel, MemorySettingsViewModel memory,
        IOptions<LocalAiOptions> options)
    {
        Models = models;
        Assistants = assistantsViewModel;
        Memory = memory;
        _voice = voice;
        _devices = devices;
        _player = player;
        _settings = settings;
        _options = options.Value;
        _loading = true;
        _echoCancellation = _options.Audio.EchoCancellation;
        _loading = false;
        RefreshDevices();
    }

    /// <summary>Raised after any setting changed (main window refreshes its status line).</summary>
    public event EventHandler? Changed;

    public ModelsViewModel Models { get; }
    public AssistantsViewModel Assistants { get; }
    public MemorySettingsViewModel Memory { get; }
    public ObservableCollection<AudioDevice> Microphones { get; } = [];
    public ObservableCollection<AudioDevice> Speakers { get; } = [];

    [ObservableProperty] private AudioDevice? _selectedMicrophone;
    [ObservableProperty] private AudioDevice? _selectedSpeaker;
    [ObservableProperty] private bool _echoCancellation;
    [ObservableProperty] private bool _isTestingMicrophone;
    [ObservableProperty] private int _selectedTab;

    // ---------------- Devices ----------------

    /// <summary>
    /// Lists the devices. The first item follows the Windows default device (saved as an empty id, so the app always
    /// opens whatever the default is at that moment); picking a device saves it for good. A saved device that is not
    /// connected stays chosen and the default is used until it comes back.
    /// </summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        _loading = true;
        Fill(Microphones, _devices.GetInputDevices(), _options.Audio.InputDeviceId);
        Fill(Speakers, _devices.GetOutputDevices(), _options.Audio.OutputDeviceId);
        SelectedMicrophone = Microphones.FirstOrDefault(d => d.Id == _options.Audio.InputDeviceId) ?? Microphones.FirstOrDefault();
        SelectedSpeaker = Speakers.FirstOrDefault(d => d.Id == _options.Audio.OutputDeviceId) ?? Speakers.FirstOrDefault();
        _loading = false;
        // The saved choice, not the device shown: an empty id keeps following the system default.
        _ = _voice.SetInputDeviceAsync(NullIfEmpty(_options.Audio.InputDeviceId));
        _player.SetDevice(NullIfEmpty(_options.Audio.OutputDeviceId));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Fill(ObservableCollection<AudioDevice> list, IReadOnlyList<AudioDevice> devices, string savedId)
    {
        list.Clear();
        if (devices.Count == 0) return; // the combo box shows "No ... found"
        var current = devices.FirstOrDefault(d => d.IsDefault);
        list.Add(new AudioDevice(SystemDefaultId, current != null ? $"System default ({current.Name})" : "System default", true));
        foreach (var d in devices) list.Add(d);
        if (savedId.Length > 0 && devices.All(d => d.Id != savedId))
            list.Add(new AudioDevice(savedId, "Chosen device (not connected: using the system default)", false));
    }

    /// <summary>Id of the "System default" item: an empty saved id means "always the Windows default".</summary>
    private const string SystemDefaultId = "";

    private static string? NullIfEmpty(string id) => id.Length == 0 ? null : id;

    partial void OnSelectedMicrophoneChanged(AudioDevice? value)
    {
        if (_loading || value == null) return;
        _options.Audio.InputDeviceId = value.Id;
        _settings.Set("Audio", "InputDeviceId", value.Id);
        _ = _voice.SetInputDeviceAsync(NullIfEmpty(value.Id)); // reopens the mic if it is in use
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedSpeakerChanged(AudioDevice? value)
    {
        if (_loading || value == null) return;
        _options.Audio.OutputDeviceId = value.Id;
        _settings.Set("Audio", "OutputDeviceId", value.Id);
        _player.SetDevice(NullIfEmpty(value.Id));
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

    /// <summary>Stops the microphone test and any shortcut recording when the panel closes.</summary>
    public void OnClosed()
    {
        IsTestingMicrophone = false;
        Assistants.CancelHotkeyRecording();
    }
}
