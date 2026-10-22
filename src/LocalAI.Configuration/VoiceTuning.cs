namespace LocalAI.Configuration;

/// <summary>One voice setting the user can change: its range and the value the voice was made for.</summary>
public sealed record VoiceSettingRange(double Min, double Max, double Default)
{
    public double Clamp(double value) => Math.Clamp(value, Min, Max);
}

/// <summary>A catalog.json change to one setting of the engine's ranges: only the numbers given replace them.</summary>
public sealed record VoiceSettingOverride
{
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Default { get; init; }
}

/// <summary>A voice's "settings" in catalog.json. <c>Unsupported</c> hides settings the voice does not have.</summary>
public sealed record VoiceSettingsOverride
{
    public VoiceSettingOverride? Speed { get; init; }
    public VoiceSettingOverride? Pitch { get; init; }
    public VoiceSettingOverride? Expressiveness { get; init; }
    public VoiceSettingOverride? Rhythm { get; init; }
    public IReadOnlyList<string> Unsupported { get; init; } = [];
}

/// <summary>
/// The settings a voice supports, with their ranges and the voice's own values. A null setting is not supported by
/// the voice and is not shown. The engine gives the ranges; a voice's catalog entry can change them.
/// </summary>
public sealed record VoiceTuning
{
    public const string PiperEngine = "piper";

    /// <summary>Speaking rate multiplier.</summary>
    public VoiceSettingRange? Speed { get; init; }
    /// <summary>Semitones; applied after synthesis, so every engine has it.</summary>
    public VoiceSettingRange? Pitch { get; init; }
    /// <summary>Piper noise scale.</summary>
    public VoiceSettingRange? Expressiveness { get; init; }
    /// <summary>Piper noise width.</summary>
    public VoiceSettingRange? Rhythm { get; init; }

    /// <summary>Piper's defaults (noise scale 0.667, noise width 0.8) are what most voices' .onnx.json files use.</summary>
    public static readonly VoiceTuning Piper = new()
    {
        Speed = new(0.75, 1.5, 1.0),
        Pitch = new(-6, 6, 0),
        Expressiveness = new(0, 1, 0.667),
        Rhythm = new(0, 1.2, 0.8),
    };

    /// <summary>The engine's settings; an unknown engine has only pitch.</summary>
    public static VoiceTuning ForEngine(string? engine) => engine switch
    {
        null or "" or PiperEngine => Piper,
        _ => new VoiceTuning { Pitch = Piper.Pitch },
    };

    public VoiceTuning With(VoiceSettingsOverride? changes)
    {
        if (changes == null) return this;
        return new VoiceTuning
        {
            Speed = Apply(Speed, changes.Speed, "speed"),
            Pitch = Apply(Pitch, changes.Pitch, "pitch"),
            Expressiveness = Apply(Expressiveness, changes.Expressiveness, "expressiveness"),
            Rhythm = Apply(Rhythm, changes.Rhythm, "rhythm"),
        };

        VoiceSettingRange? Apply(VoiceSettingRange? range, VoiceSettingOverride? change, string name)
        {
            if (range == null || changes.Unsupported.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (change == null) return range;
            var min = change.Min ?? range.Min;
            var max = Math.Max(min, change.Max ?? range.Max);
            return new VoiceSettingRange(min, max, Math.Clamp(change.Default ?? range.Default, min, max));
        }
    }
}
