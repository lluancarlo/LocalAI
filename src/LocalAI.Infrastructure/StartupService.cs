using System.Diagnostics;
using LocalAI.Core.Assistants;
using LocalAI.Core.Diagnostics;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.LLM;
using LocalAI.Memory;
using Microsoft.Extensions.Logging;

namespace LocalAI.Infrastructure;

public sealed record SubsystemStatus(string Name, ComponentState State, string? Detail);

/// <summary>
/// Brings subsystems up in the background, independently: a failure in one (e.g. no microphone, missing voice)
/// never prevents the others — text chat works as long as the LLM loads.
/// </summary>
public sealed class StartupService(
    ILanguageModel llm,
    LlamaCppEmbeddingService embeddings,
    ISpeechToText stt,
    ITextToSpeech tts,
    SqliteDatabase database,
    AssistantManager assistants,
    AssistantContext assistant,
    MemoryService memory,
    VoiceConversationController voice,
    IGpuInfoProvider gpu,
    ILogger<StartupService> logger)
{
    public GpuInfo? Gpu { get; private set; }
    public event EventHandler<SubsystemStatus>? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        logger.LogInformation("LocalAI starting (PID {Pid}, .NET {Version})", Environment.ProcessId, Environment.Version);

        Gpu = await gpu.GetGpuInfoAsync(ct).ConfigureAwait(false);
        if (Gpu != null)
            logger.LogInformation("GPU: {Name}, {Vram} MB VRAM ({Used} MB in use), driver {Driver}, CUDA {Cuda}, compute {Cc}",
                Gpu.Name, Gpu.TotalMemoryMb, Gpu.UsedMemoryMb, Gpu.DriverVersion, Gpu.CudaVersion, Gpu.ComputeCapability);
        else
            logger.LogWarning("No NVIDIA GPU detected; inference will run on the CPU");

        // Database first (cheap), then the heavy models in parallel. The LLM is started before Whisper so its VRAM
        // reservation is deterministic.
        await RunAsync("Database", async () =>
        {
            await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
            return (ComponentState.Ready, database.DatabasePath);
        }).ConfigureAwait(false);

        // The active assistant decides which voices the TTS engine loads.
        await RunAsync("Assistant", async () =>
        {
            await assistants.LoadAsync(ct).ConfigureAwait(false);
            return assistant.Current is { } a ? (ComponentState.Ready, $"assistant {a.Id}") : (ComponentState.Unavailable, "none created yet");
        }).ConfigureAwait(false);

        var llmTask = RunAsync("LLM", async () =>
        {
            await llm.LoadAsync(ct).ConfigureAwait(false);
            return llm.State == LanguageModelState.Ready
                ? (ComponentState.Ready, llm.Info?.DisplayName)
                : (ComponentState.Unavailable, llm.LastError);
        });
        var ttsTask = RunAsync("TTS", async () =>
        {
            await tts.InitializeAsync(ct).ConfigureAwait(false);
            return (tts.State, tts.State == ComponentState.Ready ? tts.Voice?.Id : tts.LastError);
        });
        await llmTask.ConfigureAwait(false);

        var sttTask = RunAsync("STT", async () =>
        {
            await stt.InitializeAsync(ct).ConfigureAwait(false);
            if (stt.State == ComponentState.Ready) voice.MarkReady();
            return (stt.State, stt.State == ComponentState.Ready ? stt.Description : stt.LastError);
        });
        var embTask = RunAsync("Embeddings", async () =>
        {
            await embeddings.StartAsync(ct).ConfigureAwait(false);
            if (embeddings.IsAvailable) await memory.BackfillEmbeddingsAsync(ct).ConfigureAwait(false);
            return embeddings.IsAvailable ? (ComponentState.Ready, embeddings.ModelId) : (ComponentState.Unavailable, embeddings.LastError);
        });
        await Task.WhenAll(ttsTask, sttTask, embTask).ConfigureAwait(false);
        logger.LogInformation("Startup complete in {Ms:F0} ms", total.Elapsed.TotalMilliseconds);
    }

    private async Task RunAsync(string name, Func<Task<(ComponentState State, string? Detail)>> init)
    {
        StatusChanged?.Invoke(this, new SubsystemStatus(name, ComponentState.Initializing, null));
        var sw = Stopwatch.StartNew();
        try
        {
            var (state, detail) = await init().ConfigureAwait(false);
            logger.LogInformation("{Subsystem}: {State} in {Ms:F0} ms ({Detail})", name, state, sw.Elapsed.TotalMilliseconds, detail);
            StatusChanged?.Invoke(this, new SubsystemStatus(name, state, detail));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Subsystem} failed to start", name);
            StatusChanged?.Invoke(this, new SubsystemStatus(name, ComponentState.Unavailable, ex.Message));
        }
    }
}
