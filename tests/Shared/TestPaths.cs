namespace LocalAI.Tests;

/// <summary>Test files live in the test output folder, never in the user's temp folder.</summary>
internal static class TestPaths
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-data");

    /// <summary>The repository root (the folder containing LocalAI.sln).</summary>
    public static string Repository { get; } = FindRepository();

    /// <summary>The published app (scripts\publish.ps1): runtime and downloaded models used by integration tests.</summary>
    public static string PublishedApp => Path.Combine(Repository, "publish", "LocalAI");

    public static string New(string prefix) => Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)]);

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LocalAI.sln"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("LocalAI.sln not found above the test output folder.");
    }
}
