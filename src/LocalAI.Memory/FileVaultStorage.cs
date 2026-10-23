using System.Collections.Concurrent;
using LocalAI.Configuration;
using LocalAI.Core.Memory.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Memory;

/// <summary>
/// The note files on disk: <c>local-ai</c> inside the chosen Obsidian vault, or inside data\memory by default. Writes
/// are atomic (temporary file, then rename) so Obsidian or a sync tool never sees half a note. A folder watcher reports
/// notes changed outside the app (the user editing in Obsidian), debounced so one save is reported once.
/// </summary>
public sealed class FileVaultStorage : IVaultStorage, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private readonly LocalAiPaths _paths;
    private readonly MemoryOptions _options;
    private readonly ILogger<FileVaultStorage> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _watchedRoot;

    public FileVaultStorage(LocalAiPaths paths, IOptions<LocalAiOptions> options, ILogger<FileVaultStorage> logger)
    {
        _paths = paths;
        _options = options.Value.Memory;
        _logger = logger;
    }

    /// <summary>The notes folder for a vault path (empty = the application's data\memory).</summary>
    public static string RootFor(LocalAiPaths paths, string vaultPath) =>
        Path.Combine(string.IsNullOrWhiteSpace(vaultPath) ? paths.MemoryDirectory : Path.GetFullPath(vaultPath), VaultLayout.RootFolder);

    public string Root => RootFor(_paths, _options.VaultPath);

    public event EventHandler<string>? Changed;

    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        // Never step outside the notes folder, whatever a path in the index or a note says.
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && full != Root)
            throw new ArgumentException("The path leaves the notes folder.", nameof(path));
        return full;
    }

    public string? Read(string path)
    {
        var full = Full(path);
        if (!File.Exists(full)) return null;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd();
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not read a note: {Error}", ex.Message);
            return null;
        }
    }

    public void Write(string path, string text)
    {
        EnsureWatching();
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, full, overwrite: true);
    }

    public void Delete(string path)
    {
        var full = Full(path);
        if (File.Exists(full)) File.Delete(full);
    }

    public bool Exists(string path) => File.Exists(Full(path));

    public IReadOnlyList<string> ListNotes(string folder)
    {
        EnsureWatching();
        var full = Full(folder);
        if (!Directory.Exists(full)) return [];
        return Directory.EnumerateFiles(full, "*" + VaultLayout.NoteExtension, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public void DeleteFolder(string folder)
    {
        var full = Full(folder);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    /// <summary>Copies every note to the notes folder of <paramref name="vaultPath"/> and checks the copies.</summary>
    /// <exception cref="IOException">The target already has a non-empty local-ai folder, or a copy differs.</exception>
    public void CopyTo(string vaultPath)
    {
        var target = RootFor(_paths, vaultPath);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException($"{target} already exists and is not empty.");
        if (!Directory.Exists(Root)) return;
        var created = !Directory.Exists(target);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(Root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
                if (File.ReadAllText(destination) != File.ReadAllText(file))
                    throw new IOException($"The copy of {Path.GetRelativePath(Root, file)} differs from the original.");
            }
        }
        catch when (created)
        {
            // Leave the target as it was: the notes stay where they are.
            try { Directory.Delete(target, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Deletes the current notes folder (after <see cref="CopyTo"/> succeeded and the option points elsewhere).</summary>
    public static void DeleteRoot(LocalAiPaths paths, string vaultPath)
    {
        var root = RootFor(paths, vaultPath);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // ---------------- Watching for the user's edits ----------------

    /// <summary>Starts (or moves) the folder watcher to the current notes folder.</summary>
    public void EnsureWatching()
    {
        var root = Root;
        lock (_gate)
        {
            if (_watchedRoot == root && _watcher != null) return;
            _watcher?.Dispose();
            _watcher = null;
            _watchedRoot = root;
            try
            {
                Directory.CreateDirectory(root);
                var watcher = new FileSystemWatcher(root, "*" + VaultLayout.NoteExtension)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Report(e.FullPath);
                watcher.Created += (_, e) => Report(e.FullPath);
                watcher.Deleted += (_, e) => Report(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Report(e.OldFullPath);
                    Report(e.FullPath);
                };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning("Could not watch the notes folder: {Error}", ex.Message);
            }
        }
    }

    private void Report(string fullPath)
    {
        if (!fullPath.EndsWith(VaultLayout.NoteExtension, StringComparison.OrdinalIgnoreCase)) return; // .tmp of atomic writes
        var path = Path.GetRelativePath(_watchedRoot ?? Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var cts = new CancellationTokenSource();
        if (_pending.TryRemove(path, out var previous))
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        _pending[path] = cts;
        _ = Task.Delay(Debounce, cts.Token).ContinueWith(t =>
        {
            try
            {
                if (t.IsCanceled) return;
                _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts));
                Changed?.Invoke(this, path);
            }
            finally
            {
                cts.Dispose();
            }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        foreach (var cts in _pending.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
