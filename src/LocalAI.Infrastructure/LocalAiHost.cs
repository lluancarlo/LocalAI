using System.Runtime.Versioning;
using LocalAI.Audio;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Diagnostics;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.LLM;
using LocalAI.Memory;
using LocalAI.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace LocalAI.Infrastructure;

/// <summary>Composition root: configuration layering, logging, native runtime setup and service registration.</summary>
public static class LocalAiHost
{
    /// <summary>appsettings.json (next to the executable) → usersettings.json (data dir) → LOCALAI_ environment variables.</summary>
    public static IConfigurationRoot BuildConfiguration(string? baseDirectory = null, string? dataDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var dataDir = dataDirectory ?? LocalAiPaths.DefaultDataDirectory;
        return new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(dataDir, "usersettings.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("LOCALAI_")
            .Build();
    }

    public static IServiceCollection AddLocalAi(this IServiceCollection services, IConfiguration configuration, LocalAiPaths? pathsOverride = null)
    {
        services.AddOptions<LocalAiOptions>().Bind(configuration.GetSection(LocalAiOptions.SectionName));

        services.AddSingleton(sp => pathsOverride ?? new LocalAiPaths(sp.GetRequiredService<IOptions<LocalAiOptions>>().Value.Paths));
        services.AddSingleton(sp => ModelCatalog.Load(sp.GetRequiredService<LocalAiPaths>().CatalogPath));
        services.AddSingleton(sp => new UserSettingsStore(sp.GetRequiredService<LocalAiPaths>().UserSettingsPath));

        // Diagnostics
        services.AddSingleton<IGpuInfoProvider, NvidiaSmiGpuInfoProvider>();

        // LLM + embeddings (llama.cpp child processes on loopback)
        services.AddSingleton<LlamaCppLanguageModel>();
        services.AddSingleton<ILanguageModel>(sp => sp.GetRequiredService<LlamaCppLanguageModel>());
        services.AddSingleton<LlamaCppEmbeddingService>();
        services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<LlamaCppEmbeddingService>());

        // Persistence + memory
        services.AddSingleton(sp => new SqliteDatabase(sp.GetRequiredService<LocalAiPaths>().DatabasePath,
            sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
        services.AddSingleton<IMemoryRetriever, MemoryRetriever>();
        services.AddSingleton<MemoryService>();

        // Speech
        services.AddSingleton<ISpeechToText, WhisperSpeechToText>();
        services.AddSingleton<ITextToSpeech, SherpaTextToSpeech>();
        services.AddSingleton<IVoiceActivityDetectorFactory, SileroVoiceActivityDetectorFactory>();
        services.AddSingleton<ILanguageDetector, HeuristicLanguageDetector>();

        // Audio (Windows/WASAPI implementation; the Core only sees the interfaces)
        if (OperatingSystem.IsWindows())
        {
            AddWindowsAudio(services);
        }
        else
        {
            services.AddSingleton<IAudioDeviceProvider, NullAudioDeviceProvider>();
            services.AddSingleton<IAudioCapture, NullAudioCapture>();
            services.AddSingleton<IAudioPlayer, NullAudioPlayer>();
        }

        // Orchestration
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<SpeechOutput>();
        services.AddSingleton<AssistantSession>();
        services.AddSingleton<VoiceConversationController>();
        services.AddSingleton<StartupService>();
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsAudio(IServiceCollection services)
    {
        services.AddSingleton<IAudioDeviceProvider, WasapiAudioDeviceProvider>();
        services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
        services.AddSingleton<IAudioPlayer>(sp => new WasapiAudioPlayer(
            sp.GetRequiredService<ILogger<WasapiAudioPlayer>>(),
            sp.GetRequiredService<IOptions<LocalAiOptions>>().Value.Audio.OutputDeviceId));
    }

    /// <summary>Structured local file logging. Logs never leave the machine and never contain conversation text or audio.</summary>
    public static void ConfigureLogging(ILoggingBuilder logging, IConfiguration configuration, string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        var level = Enum.TryParse<LogEventLevel>(configuration["Logging:Level"], true, out var l) ? l : LogEventLevel.Information;
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDirectory, "localai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        logging.ClearProviders();
        logging.AddSerilog(serilog, dispose: true);
        logging.SetMinimumLevel(LogLevel.Trace);
    }

    /// <summary>
    /// Makes the CUDA runtime shipped with llama.cpp (cublas64_13.dll, ...) resolvable for Whisper.net's CUDA backend,
    /// so no CUDA Toolkit installation is needed.
    /// </summary>
    public static void ConfigureNativeSearchPath(LocalAiPaths paths)
    {
        var dir = paths.LlamaCppDirectory;
        if (!Directory.Exists(dir)) return;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Contains(dir, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + path);
    }
}
