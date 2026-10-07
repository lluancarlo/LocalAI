namespace LocalAI.Core.Voice;

/// <summary>The continuous (hands-free) voice conversation, as seen by what turns it on and off.</summary>
public interface ILiveVoice
{
    bool IsContinuous { get; }

    /// <summary>Why the last attempt to listen failed, if it did.</summary>
    string? LastError { get; }

    event EventHandler<VoiceState>? StateChanged;

    Task StartContinuousAsync();

    Task StopContinuousAsync();
}
