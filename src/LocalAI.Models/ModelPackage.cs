namespace LocalAI.Models;

public enum ModelKind { Llm, SpeechRecognition, VoiceActivity, Embedding, Voice }

/// <summary>
/// Files of a Hugging Face repository at one revision. A path ending in "/" is a folder: everything in it is downloaded.
/// </summary>
public sealed record HuggingFaceFiles(string Repo, string Revision, IReadOnlyList<string> Paths);

/// <summary>A downloadable model: one file, or files of a Hugging Face repository (<see cref="Files"/>) in a folder.</summary>
public sealed record ModelPackage
{
    public required string Id { get; init; }
    public required ModelKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required Uri Source { get; init; }
    public required string InstallPath { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>When set, <see cref="Source"/> is the repository's page and these files go into the folder <see cref="InstallPath"/>.</summary>
    public HuggingFaceFiles? Files { get; init; }
    public int MinVramMb { get; init; }
    /// <summary>A language model the user added from Hugging Face (not in the catalog).</summary>
    public bool IsAdded { get; init; }
    /// <summary>Added models: published by the company that made the model.</summary>
    public bool Official { get; init; }
}
