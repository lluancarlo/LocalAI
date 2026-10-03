namespace LocalAI.Configuration;

/// <summary>
/// Resolves on-disk locations. Models and runtime live under a "LocalAI home": the LOCALAI_HOME environment variable,
/// else the nearest ancestor of the executable containing a <c>localai.home</c> marker (the repository root),
/// else %LOCALAPPDATA%\LocalAI. User data defaults to %LOCALAPPDATA%\LocalAI.
/// </summary>
public sealed class LocalAiPaths
{
    public const string MarkerFile = "localai.home";

    public string Home { get; }
    public string ModelsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "localai.db");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string UserSettingsPath => Path.Combine(DataDirectory, "usersettings.json");
    public string CatalogPath => Path.Combine(ModelsDirectory, "catalog.json");
    public string LlamaCppDirectory => Path.Combine(RuntimeDirectory, "llama.cpp");

    public LocalAiPaths(PathOptions options, string? baseDirectory = null)
    {
        Home = ResolveHome(baseDirectory ?? AppContext.BaseDirectory) ?? DefaultDataDirectory;
        ModelsDirectory = Resolve(options.ModelsDirectory, Path.Combine(Home, "models"));
        RuntimeDirectory = Resolve(options.RuntimeDirectory, Path.Combine(Home, "runtime"));
        DataDirectory = Resolve(options.DataDirectory, DefaultDataDirectory);
    }

    /// <summary>Needed before configuration is built (to locate usersettings.json).</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAI");

    private string Resolve(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(fallback)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), Home);

    private static string? ResolveHome(string baseDirectory)
    {
        var env = Environment.GetEnvironmentVariable("LOCALAI_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return Path.GetFullPath(env);

        for (var dir = new DirectoryInfo(baseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, MarkerFile))) return dir.FullName;
        }
        return null;
    }
}
