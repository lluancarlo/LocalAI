namespace LocalAI.Core.Extensibility;

// Extension points reserved for post-MVP modules. Nothing in the MVP implements or registers these:
// the assistant cannot call tools and makes no network requests.

/// <summary>A capability the assistant could invoke in the future (filesystem, terminal, ...).</summary>
/// <remarks>
/// Model output is untrusted. A future tool host must validate arguments and require explicit user confirmation
/// before any side effect; it must never execute generated code automatically.
/// </remarks>
public interface IAiTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON schema of the arguments.</summary>
    string ParametersSchema { get; }
    Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default);
}

public sealed record WebSearchResult(string Title, Uri Url, string Snippet);

/// <summary>Optional web module. Must live in a separate assembly so disabling it guarantees offline operation.</summary>
public interface IWebSearchService
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct = default);
}
