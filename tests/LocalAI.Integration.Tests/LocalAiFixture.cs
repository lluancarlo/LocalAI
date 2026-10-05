using LocalAI.Tests;
using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAI.Integration.Tests;

/// <summary>
/// One real stack (LLM on the GPU, Whisper, Piper, embeddings) shared by all integration tests. Uses the runtime and
/// downloaded models of the published app (publish\LocalAI) and a throw-away data directory (database, logs).
/// </summary>
public sealed class LocalAiFixture : IAsyncLifetime
{
    public string DataDirectory { get; } = TestPaths.New("it");
    public ServiceProvider Services { get; private set; } = null!;
    public SimulatedRoom Room { get; } = new();
    public LocalAiPaths Paths { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Paths = CreatePaths(DataDirectory);
        Services = BuildServices(DataDirectory, Room);
        LocalAiHost.ConfigureNativeSearchPath(Paths);
        await Services.GetRequiredService<StartupService>().StartAsync(CancellationToken.None);
        var model = ModelCatalog.LoadDefault().Llm.First(e => File.Exists(Path.Combine(Paths.ModelsDirectory, e.File))).Id;
        await Services.GetRequiredService<LocalAI.Core.Assistants.AssistantManager>().CreateAsync(
            new LocalAI.Core.Assistants.NewAssistant("Diana", "", model, VoiceFor("en"), "en"));
    }

    public static ServiceProvider BuildServices(string dataDirectory, SimulatedRoom? room)
    {
        var paths = CreatePaths(dataDirectory);
        var configuration = LocalAiHost.BuildConfiguration(paths);
        var services = new ServiceCollection();
        services.AddLogging(b => LocalAiHost.ConfigureLogging(b, configuration, paths.LogsDirectory));
        services.AddLocalAi(configuration, paths);
        if (room != null)
        {
            services.AddSingleton<IAudioCapture>(room);
            services.AddSingleton<IAudioPlayer>(room);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>The catalog's default voice for a language (used to synthesize test speech in that language).</summary>
    public static string VoiceFor(string language) =>
        ModelCatalog.LoadDefault().Tts.First(v => v.Default && v.Language == language).Id;

    // Reuses the published app's runtime and downloaded models; the database and logs go to a throw-away folder.
    private static LocalAiPaths CreatePaths(string dataDirectory) =>
        new(TestPaths.PublishedApp, dataDirectory) { ModelsDirectory = Path.Combine(TestPaths.PublishedApp, "data", "models") };

    public string ReadLog() =>
        string.Join("\n", Directory.GetFiles(Paths.LogsDirectory).Select(f =>
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(s).ReadToEnd();
        }));

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        Room.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<LocalAiFixture>
{
    public const string Name = "LocalAI integration";
}
