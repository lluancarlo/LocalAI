using System.Net;
using System.Net.Http.Headers;

namespace LocalAI.Models;

/// <summary>Resumable HTTP download into a <c>.partial</c> file next to the destination.</summary>
public sealed class ModelDownloader : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly HttpClient _http;

    public ModelDownloader(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAI/1.0");
    }

    public async Task DownloadAsync(Uri source, string destination, long expectedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        if (expectedBytes == 0 || existing != expectedBytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
                await DownloadAsync(source, destination, expectedBytes, progress, ct).ConfigureAwait(false);
                return;
            }
            response.EnsureSuccessStatusCode();

            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            var received = resumed ? existing : 0;
            var total = expectedBytes > 0 ? expectedBytes : received + (response.Content.Headers.ContentLength ?? 0);

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0 && DateTime.UtcNow - lastReport >= ProgressInterval)
                {
                    progress?.Report((double)received / total);
                    lastReport = DateTime.UtcNow;
                }
            }
        }

        var length = new FileInfo(partial).Length;
        if (expectedBytes > 0 && length != expectedBytes)
        {
            File.Delete(partial);
            throw new InvalidDataException($"Download incomplete: expected {expectedBytes:N0} bytes, received {length:N0}.");
        }
        progress?.Report(1);
        File.Move(partial, destination, overwrite: true);
    }

    public void Dispose() => _http.Dispose();
}
