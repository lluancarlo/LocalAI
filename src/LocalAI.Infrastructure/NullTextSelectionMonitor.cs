using LocalAI.Core.Desktop;

namespace LocalAI.Infrastructure;

/// <summary>Used on platforms where selections cannot be read yet (the Win32 implementation is Windows-only).</summary>
internal sealed class NullTextSelectionMonitor : ITextSelectionMonitor
{
    public bool IsSupported => false;

    public IDisposable Start(Action<string> selected) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}
