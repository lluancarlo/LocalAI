using LocalAI.Core.Assistants;
using LocalAI.Core.Speech;
using LocalAI.LLM;
using Microsoft.Extensions.Logging;

namespace LocalAI.Infrastructure;

/// <summary>
/// Speech recognition (Whisper) and the embedding model: they load when an AI turns on and unload when it turns off
/// (<see cref="AssistantManager"/>). Each one starts on its own, so a failure in one (e.g. a missing model) never stops
/// the other or the AI.
/// </summary>
public sealed class AiCompanions(
    ISpeechToText stt,
    LlamaCppEmbeddingService embeddings,
    ILogger<AiCompanions> logger) : IAiCompanions
{
    /// <summary>Raised (on any thread) after they loaded or unloaded: voice input follows speech recognition.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised (on any thread) when the embedding model started: notes indexed without it get embedded.</summary>
    public event EventHandler? EmbeddingsStarted;

    public async Task StartAsync(bool hearing, CancellationToken ct = default)
    {
        try
        {
            if (!hearing) UnloadSpeech();
            await Task.WhenAll(
                hearing ? Task.Run(() => StartSpeechAsync(ct), ct) : Task.CompletedTask,
                Task.Run(() => StartEmbeddingsAsync(ct), ct)).ConfigureAwait(false);
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetHearingAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            if (on) await Task.Run(() => StartSpeechAsync(ct), ct).ConfigureAwait(false);
            else UnloadSpeech();
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UnloadSpeech()
    {
        if (stt.State == ComponentState.NotInitialized) return;
        stt.Unload();
        logger.LogInformation("Speech recognition unloaded (hearing off)");
    }

    private async Task StartSpeechAsync(CancellationToken ct)
    {
        try
        {
            await stt.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Speech recognition failed to start");
        }
    }

    private async Task StartEmbeddingsAsync(CancellationToken ct)
    {
        try
        {
            if (embeddings.IsAvailable) return;
            await embeddings.StartAsync(ct).ConfigureAwait(false);
            if (embeddings.IsAvailable) EmbeddingsStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The embedding model failed to start");
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            stt.Unload();
            await embeddings.StopAsync().ConfigureAwait(false);
            logger.LogInformation("Speech recognition and the embedding model unloaded");
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
