using LocalAI.Configuration;
using LocalAI.Tests;

namespace LocalAI.Infrastructure.Tests;

/// <summary>
/// Rule: the application never writes outside its own folder, so deleting the folder removes everything it created.
/// </summary>
public sealed class SelfContainedFolderTests
{
    private static readonly string[] ForbiddenApis =
    [
        "Environment.SpecialFolder", "GetFolderPath", "GetTempPath", "GetTempFileName", "Registry.",
        "LOCALAPPDATA", "%APPDATA%", "USERPROFILE", "IsolatedStorage",
    ];

    [Fact]
    public void Application_code_does_not_use_user_or_system_folders()
    {
        var src = Path.Combine(TestPaths.Repository, "src");
        var offenders =
            from file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            where !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            from line in File.ReadLines(file).Select((text, index) => (text, number: index + 1))
            from api in ForbiddenApis
            where line.text.Contains(api, StringComparison.Ordinal)
            select $"{Path.GetRelativePath(src, file)}:{line.number} uses {api}";

        Assert.Empty(offenders);
    }

    [Fact]
    public void Temporary_files_of_libraries_and_child_processes_go_to_the_application_folder()
    {
        var paths = new LocalAiPaths(TestPaths.New("temp"));
        var temp = Environment.GetEnvironmentVariable("TEMP");
        var tmp = Environment.GetEnvironmentVariable("TMP");
        try
        {
            Directory.CreateDirectory(paths.TempDirectory);
            File.WriteAllText(Path.Combine(paths.TempDirectory, "stale.tmp"), "");

            LocalAiHost.ConfigureTempDirectory(paths);

            Assert.Equal(paths.TempDirectory, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            Assert.Empty(Directory.GetFileSystemEntries(paths.TempDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", tmp);
        }
    }
}
