using System.Runtime.Versioning;
using LocalAI.Core.Audio;
using NAudio.CoreAudioApi;

namespace LocalAI.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioDeviceProvider : IAudioDeviceProvider
{
    public IReadOnlyList<AudioDevice> GetInputDevices() => Enumerate(DataFlow.Capture);
    public IReadOnlyList<AudioDevice> GetOutputDevices() => Enumerate(DataFlow.Render);

    private static List<AudioDevice> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Communications))
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
                defaultId = def.ID;
            }
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d) result.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // No audio subsystem: report no devices; voice features show as unavailable.
        }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static MMDevice OpenDevice(MMDeviceEnumerator enumerator, string? deviceId, DataFlow flow)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var d = enumerator.GetDevice(deviceId);
                if (d.State == DeviceState.Active) return d;
                d.Dispose();
            }
            catch (System.Runtime.InteropServices.COMException) { /* unplugged: fall back to default */ }
        }
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Communications))
            throw new AudioDeviceException(flow == DataFlow.Capture ? "No microphone found." : "No audio output device found.");
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
    }
}
