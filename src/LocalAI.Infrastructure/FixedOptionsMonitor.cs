using Microsoft.Extensions.Options;

namespace LocalAI.Infrastructure;

/// <summary>Options monitor over a single shared instance (see <see cref="LocalAiHost.AddLocalAi"/>).</summary>
internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
