using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Diagnostics;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.LLM;
using LocalAI.Memory;
using LocalAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Infrastructure;

public sealed record SubsystemStatus(string Name, ComponentState State, string? Detail);

/// <summary>
/// Brings the app up in the background: the database and the active assistant. The models (language model, voice,
/// speech recognition, embeddings) load only when an AI turns on (<see cref="AssistantManager.LoadModelAsync"/>), here
/// only if Settings › General › Startup asks for it. A failure in one never prevents the others.
/// </summary>
public sealed class StartupService(
    ILanguageModel llm,
    ISpeechToText stt,
    AiCompanions companions,
    SqliteDatabase database,
    AssistantManager assistants,
    AssistantContext assistant,
    MemoryWorker memory,
    VoiceConversationController voice,
    IGpuInfoProvider gpu,
    ModelLibrary library,
    IOptions<LocalAiOptions> options,
    ILogger<StartupService> logger)
{
    public GpuInfo? Gpu { get; private set; }
    public event EventHandler<SubsystemStatus>? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        logger.LogInformation("LocalAI starting (PID {Pid}, .NET {Version})", Environment.ProcessId, Environment.Version);

        // The GPU is asked (nvidia-smi, a few hundred milliseconds) while the database opens and the assistant loads.
        var gpuInfo = Task.Run(() => gpu.GetGpuInfoAsync(ct), ct);

        try
        {
            var removed = library.RemoveObsoleteVoices();
            if (removed.Count > 0) logger.LogInformation("Removed voices the app no longer uses: {Folders}", string.Join(", ", removed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the voices the app no longer uses");
        }

        // Voice input follows speech recognition, which loads and unloads with the AI.
        companions.Changed += (_, _) =>
        {
            if (stt.State == ComponentState.Ready) voice.MarkReady();
            else voice.MarkOff();
        };
        // Notes indexed while the embedding model was off (or with another one) get their embeddings.
        companions.EmbeddingsStarted += (_, _) => _ = memory.RescanAsync();

        await RunAsync("Database", async () =>
        {
            await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
            return (ComponentState.Ready, database.DatabasePath);
        }).ConfigureAwait(false);

        await RunAsync("Assistant", async () =>
        {
            await assistants.LoadAsync(ct).ConfigureAwait(false);
            // Assistants created before pictures existed get the one that matches their voice.
            await assistants.AssignMissingAvatarsAsync(a => AssistantAvatars.ForGender(
                library.Voices.FirstOrDefault(v => v.Id == a.VoiceId)?.Gender), ct).ConfigureAwait(false);
            return assistant.Current is { } a ? (ComponentState.Ready, $"assistant {a.Id}") : (ComponentState.Unavailable, "none created yet");
        }).ConfigureAwait(false);

        await RunAsync("GPU", async () =>
        {
            Gpu = await gpuInfo.ConfigureAwait(false);
            if (Gpu != null)
                logger.LogInformation("GPU: {Name}, {Vram} MB VRAM ({Used} MB in use), driver {Driver}, CUDA {Cuda}, compute {Cc}",
                    Gpu.Name, Gpu.TotalMemoryMb, Gpu.UsedMemoryMb, Gpu.DriverVersion, Gpu.CudaVersion, Gpu.ComputeCapability);
            else
                logger.LogWarning("No NVIDIA GPU detected; inference will run on the CPU");
            return Gpu != null ? (ComponentState.Ready, Gpu.Name) : (ComponentState.Unavailable, "none detected");
        }).ConfigureAwait(false);

        // The AI stays off until the user turns it on, unless Settings › General › Startup says otherwise.
        if (options.Value.Assistant.LoadModelAtStartup && assistant.Current is { } active)
        {
            await RunAsync("AI", async () =>
            {
                await assistants.LoadModelAsync(active, ct).ConfigureAwait(false);
                return llm.State == LanguageModelState.Ready
                    ? (ComponentState.Ready, llm.Info?.DisplayName)
                    : (ComponentState.Unavailable, llm.LastError);
            }).ConfigureAwait(false);
        }

        // Background memory work (notes, profile, index); what needs the AI waits for it, and it always yields to the
        // assistant. A new assistant (or a new shortcut) gets its identity note written there.
        assistants.AssistantsChanged += (_, _) => _ = memory.QueueIdentityNotesAsync();
        memory.Start();
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
