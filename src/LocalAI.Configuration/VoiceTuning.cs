namespace LocalAI.Configuration;

/// <summary>One voice setting the user can change: its range and the value the voice was made for.</summary>
public sealed record VoiceSettingRange(double Min, double Max, double Default)
{
    public double Clamp(double value) => Math.Clamp(value, Min, Max);
}

/// <summary>A catalog.json change to one setting of the default ranges: only the numbers given replace them.</summary>
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
    public IReadOnlyList<string> Unsupported { get; init; } = [];
}

/// <summary>
/// The settings a voice supports, with their ranges and the voice's own values. A null setting is not supported by
/// the voice and is not shown. A voice's catalog entry can change the ranges.
/// </summary>
public sealed record VoiceTuning
{
    /// <summary>Speaking rate multiplier.</summary>
    public VoiceSettingRange? Speed { get; init; }
    /// <summary>Semitones; applied after synthesis. A small range: more than a semitone sounds artificial.</summary>
    public VoiceSettingRange? Pitch { get; init; }

    /// <summary>Kokoro's settings: its voices are made for speed 1 and no pitch change.</summary>
    public static readonly VoiceTuning Default = new()
    {
        Speed = new(0.75, 1.5, 1.0),
        Pitch = new(-1, 1, 0),
    };

    public VoiceTuning With(VoiceSettingsOverride? changes)
    {
        if (changes == null) return this;
        return new VoiceTuning
        {
            Speed = Apply(Speed, changes.Speed, "speed"),
            Pitch = Apply(Pitch, changes.Pitch, "pitch"),
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
