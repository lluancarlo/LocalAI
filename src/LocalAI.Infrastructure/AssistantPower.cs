using LocalAI.Core.Assistants;
using LocalAI.Core.Memory.Vault;
using Microsoft.Extensions.Logging;

namespace LocalAI.Infrastructure;

/// <summary>
/// Turns an assistant's AI off the way the memory needs: first the memory work left for that assistant runs (its
/// conversation notes, profile, identity note and index; the language model writes the notes), then the model is
/// unloaded. Turning an AI on meanwhile stops that work and keeps it queued: the user comes first. Before the app
/// exits, the same work runs for every assistant whose model is loaded.
/// </summary>
public sealed class AssistantPower
{
    private readonly AssistantManager _manager;
    private readonly MemoryWorker _memory;
    private readonly ILogger<AssistantPower> _logger;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _finishing;

    public AssistantPower(AssistantManager manager, MemoryWorker memory, ILogger<AssistantPower> logger)
    {
        _manager = manager;
        _memory = memory;
        _logger = logger;
        _manager.ModelRequested += (_, _) => CancelFinishing();
    }

    /// <summary>The assistant whose memory is being finished before its AI turns off, or null.</summary>
    public long? FinishingAssistantId { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="FinishingAssistantId"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Finishes the assistant's memory work, then unloads the model. Returns false when an AI was turned on meanwhile
    /// (the model then stays loaded).
    /// </summary>
    public async Task<bool> TurnOffAsync(AssistantProfile assistant)
    {
        var cts = new CancellationTokenSource();
        CancelFinishing();
        lock (_gate) _finishing = cts;
        SetFinishing(assistant.Id);
        try
        {
            var jobs = await _memory.FinishAsync(assistant.Id, cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Finished {Jobs} memory jobs of assistant {Id} before turning its AI off", jobs, assistant.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation("Turning the AI of assistant {Id} off was stopped: an AI was turned on", assistant.Id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The memory work of assistant {Id} failed; turning its AI off anyway", assistant.Id);
        }
        finally
        {
            lock (_gate)
            {
                if (_finishing == cts) _finishing = null;
            }
            cts.Dispose();
            SetFinishing(null);
        }
        await _manager.UnloadModelAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Before the app exits: finishes the memory work of every assistant whose model is loaded.</summary>
    public async Task FinishBeforeExitAsync(CancellationToken ct)
    {
        foreach (var assistant in await _manager.ListAsync(ct).ConfigureAwait(false))
        {
            if (!_manager.IsModelLoaded(assistant)) continue;
            SetFinishing(assistant.Id);
            try
            {
                var jobs = await _memory.FinishAsync(assistant.Id, ct).ConfigureAwait(false);
                _logger.LogInformation("Finished {Jobs} memory jobs of assistant {Id} before exiting", jobs, assistant.Id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "The memory work of assistant {Id} failed before exiting", assistant.Id); }
            finally { SetFinishing(null); }
        }
    }

    private void CancelFinishing()
    {
        lock (_gate)
        {
            try { _finishing?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void SetFinishing(long? id)
    {
        FinishingAssistantId = id;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
