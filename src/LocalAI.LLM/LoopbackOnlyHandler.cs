using System.Net;

namespace LocalAI.LLM;

/// <summary>
/// HTTP handler that refuses any request not addressed to the local machine. The inference engines run as local
/// child processes on 127.0.0.1; this guarantees the client can never be pointed at a remote host.
/// </summary>
public sealed class LoopbackOnlyHandler : DelegatingHandler
{
    public LoopbackOnlyHandler() : base(new SocketsHttpHandler
    {
        UseProxy = false, // never route through a system proxy
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !IsLoopback(request.RequestUri))
            throw new InvalidOperationException($"Blocked non-loopback request to '{request.RequestUri?.Host}'. LocalAI never contacts remote hosts.");
        return base.SendAsync(request, cancellationToken);
    }
}
