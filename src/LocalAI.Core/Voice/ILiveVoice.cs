namespace LocalAI.Core.Voice;

/// <summary>The continuous (hands-free) voice conversation, as seen by what turns it on and off.</summary>
public interface ILiveVoice
{
    bool IsContinuous { get; }

    /// <summary>Why the last attempt to listen failed, if it did.</summary>
    string? LastError { get; }

    event EventHandler<VoiceState>? StateChanged;

    Task StartContinuousAsync();

    /// <summary>
    /// Leaves the live conversation. What the user is still saying is dropped; with <paramref name="keepReply"/> a reply
    /// already being made goes on (and speaks) until it ends or is stopped, else it stops too.
    /// </summary>
    Task StopContinuousAsync(bool keepReply = false);
}
