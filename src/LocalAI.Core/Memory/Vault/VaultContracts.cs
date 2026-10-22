namespace LocalAI.Core.Memory.Vault;

public enum NoteKind { Identity, Profile, Conversation }

/// <summary>What the index knows about one note file of an assistant.</summary>
public sealed record VaultNote
{
    public long Id { get; init; }
    public long AssistantId { get; init; }
    /// <summary>Path relative to the vault's <see cref="VaultLayout.RootFolder"/>, with '/'.</summary>
    public required string Path { get; init; }
    public NoteKind Kind { get; init; }
    public long? ConversationId { get; init; }
    /// <summary>The generated block as the app last wrote it; a different block in the file means the user edited it.</summary>
    public string? GeneratedText { get; init; }
    /// <summary>Hash of the whole file when it was last indexed.</summary>
    public string FileHash { get; init; } = "";
    /// <summary>The conversation's last change when its note was written (the note is stale after newer messages).</summary>
    public DateTimeOffset? SourceUpdatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One section of a note, as retrieved for prompts.</summary>
public sealed record NoteChunk
{
    public long Id { get; init; }
    public long NoteId { get; init; }
    public string NoteTitle { get; init; } = "";
    public string Heading { get; init; } = "";
    public required string Text { get; init; }
    public float[]? Embedding { get; init; }
    public string? EmbeddingModel { get; init; }
}

public enum MemoryJobKind { MigrateLegacyFacts, SummarizeConversation, ConsolidateProfile, IndexNote, WriteIdentity }

public sealed record MemoryJob(long Id, MemoryJobKind Kind, long AssistantId, long? ConversationId, string? Path, int Attempts);

/// <summary>The note files: the <c>local-ai</c> folder in data\memory or in the user's Obsidian vault.</summary>
public interface IVaultStorage
{
    /// <summary>Absolute path of the <c>local-ai</c> folder.</summary>
    string Root { get; }
    string? Read(string path);
    /// <summary>Writes atomically (a reader never sees half a file), creating folders as needed.</summary>
    void Write(string path, string text);
    void Delete(string path);
    bool Exists(string path);
    /// <summary>Every note (.md) under <paramref name="folder"/>, as relative paths.</summary>
    IReadOnlyList<string> ListNotes(string folder);
    void DeleteFolder(string folder);
    /// <summary>Raised (on any thread, debounced) with the relative path of a note changed outside the app.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>
/// Index of the notes in the database: which file belongs to which assistant and conversation, their sections with
/// embeddings and full-text search, and the queue of background work. Rebuildable from the files.
/// </summary>
public interface IVaultIndex
{
    Task<VaultNote?> FindNoteAsync(long assistantId, string path, CancellationToken ct = default);
    Task<VaultNote?> FindConversationNoteAsync(long conversationId, CancellationToken ct = default);
    Task<IReadOnlyList<VaultNote>> ListNotesAsync(long assistantId, CancellationToken ct = default);
    /// <summary>Inserts (Id 0) or updates the note; returns it with its id.</summary>
    Task<VaultNote> SaveNoteAsync(VaultNote note, CancellationToken ct = default);
    /// <summary>Removes the note and its sections from the index (not the file).</summary>
    Task DeleteNoteAsync(long noteId, CancellationToken ct = default);

    Task ReplaceChunksAsync(long noteId, IReadOnlyList<NoteChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<NoteChunk>> ListChunksAsync(long assistantId, CancellationToken ct = default);
    /// <summary>Full-text matches among the assistant's sections, best first (chunk ids).</summary>
    Task<IReadOnlyList<long>> SearchChunksAsync(long assistantId, string query, int limit, CancellationToken ct = default);

    /// <summary>
    /// Conversations created after <paramref name="since"/>, quiet since <paramref name="idleBefore"/>, with a reply
    /// and without an up-to-date note.
    /// </summary>
    Task<IReadOnlyList<(long ConversationId, long AssistantId)>> ConversationsNeedingNotesAsync(
        DateTimeOffset since, DateTimeOffset idleBefore, CancellationToken ct = default);

    /// <summary>The facts of the old memory (before notes) of the assistant, removed from the database.</summary>
    Task<IReadOnlyList<string>> TakeLegacyFactsAsync(long assistantId, CancellationToken ct = default);

    /// <summary>Queues a job unless the same one is already waiting (then its start time is kept).</summary>
    Task EnqueueAsync(MemoryJobKind kind, long assistantId, long? conversationId, string? path, DateTimeOffset notBefore,
        CancellationToken ct = default);
    /// <summary>The next job due at <paramref name="now"/>, of <paramref name="assistantId"/> only when given.</summary>
    Task<MemoryJob?> NextJobAsync(DateTimeOffset now, CancellationToken ct = default, long? assistantId = null);
    Task CompleteJobAsync(long jobId, CancellationToken ct = default);
    /// <summary>Puts the job back for later; <paramref name="failed"/> counts it as a failed attempt.</summary>
    Task PostponeJobAsync(long jobId, DateTimeOffset notBefore, bool failed, CancellationToken ct = default);

    Task<string?> GetStateAsync(string key, CancellationToken ct = default);
    Task SetStateAsync(string key, string value, CancellationToken ct = default);
}
