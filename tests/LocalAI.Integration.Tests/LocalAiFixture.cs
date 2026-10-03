using LocalAI.Configuration;
using LocalAI.Core.Audio;
using LocalAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAI.Integration.Tests;

/// <summary>
/// One real stack (LLM on the GPU, Whisper, Piper, embeddings) shared by all integration tests. Uses the installed
/// models from the repository's models/ folder and a throw-away data directory (database, logs).
/// </summary>
public sealed class LocalAiFixture : IAsyncLifetime
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "localai-it-" + Guid.NewGuid().ToString("N")[..8]);
    public ServiceProvider Services { get; private set; } = null!;
    public SimulatedRoom Room { get; } = new();
    public LocalAiPaths Paths { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Paths = new LocalAiPaths(new PathOptions { DataDirectory = DataDirectory });
        Services = BuildServices(DataDirectory, Room);
        LocalAiHost.ConfigureNativeSearchPath(Paths);
        await Services.GetRequiredService<StartupService>().StartAsync(CancellationToken.None);
    }

    public static ServiceProvider BuildServices(string dataDirectory, SimulatedRoom? room)
    {
        var configuration = LocalAiHost.BuildConfiguration(AppContext.BaseDirectory, dataDirectory);
        var paths = new LocalAiPaths(new PathOptions { DataDirectory = dataDirectory });
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
        try { Directory.Delete(DataDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<LocalAiFixture>
{
    public const string Name = "LocalAI integration";
}
