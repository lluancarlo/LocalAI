using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;

namespace LocalAI.Core.Tests;

/// <summary>The learn folder: documents learned only with Learn, all at once, and recalled with their own share.</summary>
public sealed class LearnTests
{
    private readonly MemoryHarness _m = new();
    private AssistantProfile _nova = null!;

    private async Task<AssistantProfile> NovaAsync()
    {
        _nova = await _m.Assistants.CreateAsync(new NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        _m.Context.Set(_nova);
        return _nova;
    }

    private const string Manual = "# Coffee machine\n\n## Descaling\nThe user descales the coffee machine every month with citric acid.\n\n" +
                                  "## Cleaning\nRinse the milk frother after each use.";

    [Fact]
    public async Task Learn_indexes_the_markdown_and_text_files_and_replaces_what_was_learned_before()
    {
        var nova = await NovaAsync();
        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual);
        await _m.Vault.AddLearnFileAsync(nova, "rome.txt", "The user visits Rome in May.\n\nThe hotel is near the Pantheon.");

        Assert.Equal(2 + 1, await _m.Vault.LearnAsync(nova, null)); // two sections, one short text
        var documents = _m.Index.Notes.Where(n => n.Kind == NoteKind.Document).ToList();
        Assert.Equal(["nova/learn/coffee.md", "nova/learn/rome.txt"], documents.Select(n => n.Path).Order());
        Assert.All(_m.Index.Chunks, c => Assert.Equal("fake-embed", c.EmbeddingModel));

        // Learn again with one file gone: everything is replaced, nothing of the old file stays.
        _m.Storage.Delete("nova/learn/rome.txt");
        Assert.Equal(2, await _m.Vault.LearnAsync(nova, null));
        Assert.Equal(["nova/learn/coffee.md"], _m.Index.Notes.Where(n => n.Kind == NoteKind.Document).Select(n => n.Path));
        Assert.DoesNotContain(_m.Index.Chunks, c => c.Text.Contains("Pantheon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_learn_keeps_what_was_learned_before()
    {
        var nova = await NovaAsync();
        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual);
        await _m.Vault.LearnAsync(nova, null);
        var before = _m.Index.Chunks.Select(c => c.Text).ToList();

        await _m.Vault.AddLearnFileAsync(nova, "rome.txt", "The user visits Rome in May.");
        _m.Index.FailReplaceDocuments = true;
        await Assert.ThrowsAsync<IOException>(() => _m.Vault.LearnAsync(nova, null));

        Assert.Equal(before, _m.Index.Chunks.Select(c => c.Text).ToList());
        Assert.False(_m.Vault.IsLearning);
    }

    [Fact]
    public async Task Learn_needs_the_embedding_model()
    {
        var nova = await NovaAsync();
        _m.Embeddings.IsAvailable = false;
        Assert.False(_m.Vault.CanLearn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _m.Vault.LearnAsync(nova, null));
    }

    [Fact]
    public async Task The_learn_folder_is_never_indexed_on_its_own()
    {
        var nova = await NovaAsync();
        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual);

        await _m.Vault.ScanAsync(nova.Id, CancellationToken.None);
        await _m.Vault.IndexNoteAsync(nova.Id, "nova/learn/coffee.md", CancellationToken.None);
        Assert.Empty(_m.Index.Notes);

        // Learned, then the file deleted: it stays learned until the next Learn.
        await _m.Vault.LearnAsync(nova, null);
        _m.Storage.Delete("nova/learn/coffee.md");
        await _m.Vault.ScanAsync(nova.Id, CancellationToken.None);
        Assert.Single(_m.Index.Notes, n => n.Kind == NoteKind.Document);
    }

    [Fact]
    public async Task The_status_tells_what_was_learned_and_whether_the_files_changed_since()
    {
        var nova = await NovaAsync();
        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual);
        var status = await _m.Vault.LearnStatusAsync(nova);
        Assert.Null(status.LearnedAt);
        Assert.Single(status.Files);

        await _m.Vault.LearnAsync(nova, null);
        status = await _m.Vault.LearnStatusAsync(nova);
        Assert.Equal((1, 2, false), (status.LearnedFiles, status.Passages, status.Changed));

        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual + "\n\nDescale with vinegar too.");
        Assert.True((await _m.Vault.LearnStatusAsync(nova)).Changed);
    }

    [Fact]
    public async Task Only_markdown_and_text_files_can_be_added()
    {
        var nova = await NovaAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _m.Vault.AddLearnFileAsync(nova, "manual.pdf", "%PDF"));
    }

    [Fact]
    public void A_text_file_is_cut_at_paragraphs_and_a_long_paragraph_at_words()
    {
        var note = new VaultNote { Path = "nova/learn/notes.txt", Kind = NoteKind.Document };
        var chunks = MemoryVault.ChunkDocument(note, "First paragraph.\r\n\r\nSecond paragraph.");
        Assert.Equal(["First paragraph.\n\nSecond paragraph."], chunks.Select(c => c.Text)); // short: one passage

        var longText = string.Join(' ', Enumerable.Repeat("word", 700)); // 3500 characters without a blank line
        var pieces = MemoryVault.ChunkDocument(note, longText);
        Assert.True(pieces.Count >= 3);
        Assert.All(pieces, p => Assert.True(p.Text.Length <= 1200));
        Assert.All(pieces, p => Assert.Equal("notes", p.NoteTitle));
    }

    [Fact]
    public async Task Learned_passages_are_recalled_apart_from_the_notes_and_labelled_in_the_prompt()
    {
        var nova = await NovaAsync();
        await _m.Vault.AddLearnFileAsync(nova, "coffee.md", Manual);
        await _m.Vault.LearnAsync(nova, null);

        var memory = await _m.Vault.RecallAsync("how often does the user descale the coffee machine", CancellationToken.None);
        Assert.Contains(memory.Documents, c => c.Text.Contains("citric acid", StringComparison.Ordinal));
        Assert.Empty(memory.Passages);

        var prompt = new PromptBuilder(_m.Options, _m.Context).Build([], "How often?", memory, spoken: false, DateTimeOffset.Now);
        Assert.Contains("Passages of the documents you learned", prompt[^1].Content, StringComparison.Ordinal);
        Assert.Contains("[[coffee]] Descaling: The user descales", prompt[^1].Content, StringComparison.Ordinal);
    }
}
