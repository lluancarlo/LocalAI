namespace LocalAI.Configuration;

/// <summary>
/// The application lives in one folder: the executable's. Everything it creates or keeps for the user (database,
/// settings, logs, temporary files, downloaded models) goes into its <c>data</c> subfolder, so copying the folder
/// installs the app and deleting it removes every trace.
/// </summary>
public sealed class LocalAiPaths
{
    public LocalAiPaths(string home, string? dataDirectory = null)
    {
        Home = Path.GetFullPath(home);
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Home, "data"));
        ModelsDirectory = Path.Combine(DataDirectory, "models");
    }

    /// <summary>The executable's folder.</summary>
    public string Home { get; }
    public string DataDirectory { get; }
    /// <summary>Downloaded models. Can be pointed elsewhere (tests reuse already downloaded models).</summary>
    public string ModelsDirectory { get; init; }

    /// <summary>Native runtime shipped with the app.</summary>
    public string RuntimeDirectory => Path.Combine(Home, "runtime");
    public string LlamaCppDirectory => Path.Combine(RuntimeDirectory, "llama.cpp");
    public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");
    public string DatabasePath => Path.Combine(DataDirectory, "localai.db");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string UserSettingsPath => Path.Combine(DataDirectory, "usersettings.json");

    public static LocalAiPaths ForApplication() => new(AppContext.BaseDirectory);
}
