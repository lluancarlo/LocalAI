using LocalAI.Configuration;
using LocalAI.Core.Memory.Vault;
using LocalAI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAI.Memory.Tests;

public sealed class FileVaultStorageTests : IDisposable
{
    private readonly LocalAiPaths _paths = new(TestPaths.New("vault"));
    private readonly LocalAiOptions _options = new();
    private readonly FileVaultStorage _storage;

    public FileVaultStorageTests() =>
        _storage = new FileVaultStorage(_paths, Options.Create(_options), NullLogger<FileVaultStorage>.Instance);

    public void Dispose()
    {
        _storage.Dispose();
        try { Directory.Delete(_paths.Home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void By_default_the_notes_live_in_the_applications_data_folder()
    {
        Assert.Equal(Path.Combine(_paths.DataDirectory, "memory", "local-ai"), _storage.Root);

        _storage.Write("nova/conversations/2026-10-22-chat.md", "# Chat\n");
        Assert.True(File.Exists(Path.Combine(_storage.Root, "nova", "conversations", "2026-10-22-chat.md")));
        Assert.Equal("# Chat\n", _storage.Read("nova/conversations/2026-10-22-chat.md"));
        Assert.Equal(["nova/conversations/2026-10-22-chat.md"], _storage.ListNotes("nova"));
        Assert.Empty(Directory.GetFiles(_storage.Root, "*.tmp", SearchOption.AllDirectories)); // atomic write leaves nothing behind

        _storage.Delete("nova/conversations/2026-10-22-chat.md");
        Assert.Null(_storage.Read("nova/conversations/2026-10-22-chat.md"));
        _storage.DeleteFolder("nova");
        Assert.False(Directory.Exists(Path.Combine(_storage.Root, "nova")));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("nova/../../outside.md")]
    [InlineData("C:/Windows/notes.md")]
    public void Paths_never_leave_the_notes_folder(string path) =>
        Assert.Throws<ArgumentException>(() => _storage.Write(path, "x"));

    [Fact]
    public void Notes_are_copied_into_the_local_ai_folder_of_a_vault()
    {
        _storage.Write("nova/nova-profile.md", "# Profile\n");
        var vault = Path.Combine(_paths.Home, "MyVault");
        Directory.CreateDirectory(Path.Combine(vault, "My own notes"));

        _storage.CopyTo(vault);

        Assert.Equal("# Profile\n", File.ReadAllText(Path.Combine(vault, "local-ai", "nova", "nova-profile.md")));
        Assert.True(Directory.Exists(Path.Combine(vault, "My own notes"))); // the user's vault is left alone

        // A vault that already has a local-ai folder with notes is not mixed into.
        Assert.Throws<IOException>(() => _storage.CopyTo(vault));

        _options.Memory.VaultPath = vault;
        Assert.Equal(Path.Combine(vault, "local-ai"), _storage.Root);
        FileVaultStorage.DeleteRoot(_paths, "");
        Assert.False(Directory.Exists(Path.Combine(_paths.DataDirectory, "memory", "local-ai")));
    }

    [Fact]
    public async Task Notes_changed_outside_the_app_are_reported_once()
    {
        _storage.EnsureWatching();
        var reported = new List<string>();
        var changed = new TaskCompletionSource();
        _storage.Changed += (_, path) =>
        {
            lock (reported) reported.Add(path);
            changed.TrySetResult();
        };

        var file = Path.Combine(_storage.Root, "nova", "ideas.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "# Ideas\n");
        await File.AppendAllTextAsync(file, "more\n");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500);
        lock (reported) Assert.Equal(["nova/ideas.md"], reported);
    }
}
