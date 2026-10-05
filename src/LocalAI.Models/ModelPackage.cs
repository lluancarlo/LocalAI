namespace LocalAI.Models;

public enum ModelKind { Llm, SpeechRecognition, VoiceActivity, Embedding, Voice }

/// <summary>A downloadable model: one file, or a .tar.bz2 archive extracted to a folder.</summary>
public sealed record ModelPackage
{
    public required string Id { get; init; }
    public required ModelKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required Uri Source { get; init; }
    public required string InstallPath { get; init; }
    public long SizeBytes { get; init; }
    public bool IsArchive { get; init; }
    public string? Language { get; init; }
    public int MinVramMb { get; init; }
    public bool IsDefault { get; init; }
}
