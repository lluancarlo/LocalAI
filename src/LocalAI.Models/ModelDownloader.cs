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

    /// <summary>Times a download is tried when the network fails; each try goes on from what was received.</summary>
    public int MaxAttempts { get; init; } = 5;
    /// <summary>Wait before the second try; doubled for each one after.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>A connection that sends nothing for this long is given up (and tried again).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task DownloadAsync(Uri source, string destination, long expectedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        var partial = destination + ".partial";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TransferAsync(source, partial, expectedBytes, progress, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                // A lost or stalled connection: what was received stays in the partial file, the next try resumes it.
                await Task.Delay(RetryDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
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

    /// <summary>Failures of the network or the server that another try may get past (not a missing file, not a full disk).</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } => (int)status!.Value >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        _ => false,
    };

    /// <summary>Appends what is missing of <paramref name="source"/> to <paramref name="partial"/>.</summary>
    private async Task TransferAsync(Uri source, string partial, long expectedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expectedBytes != 0 && existing == expectedBytes) return;

        // Cancelled when nothing arrives for StallTimeout (pushed back at every read).
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
                await TransferAsync(source, partial, expectedBytes, progress, ct).ConfigureAwait(false);
                return;
            }
            response.EnsureSuccessStatusCode();

            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            var received = resumed ? existing : 0;
            var total = expectedBytes > 0 ? expectedBytes : received + (response.Content.Headers.ContentLength ?? 0);

            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            var lastReport = DateTime.UtcNow;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Reading the network failed (writing the file is not in here: a full disk is not tried again).
                    throw new HttpRequestException("The connection was lost during the download.", ex);
                }
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0 && DateTime.UtcNow - lastReport >= ProgressInterval)
                {
                    progress?.Report((double)received / total);
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The download stalled: nothing was received for {StallTimeout.TotalSeconds:F0} seconds.");
        }
    }

    /// <summary>A small text response (an API page) and the address of the next page, from its <c>Link</c> header.</summary>
    public async Task<(string Body, Uri? Next)> GetPageAsync(Uri source, CancellationToken ct)
    {
        using var response = await _http.GetAsync(source, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (body, NextPage(response));
    }

    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        foreach (var link in links.SelectMany(l => l.Split(',')))
        {
            var parts = link.Split(';');
            if (parts.Length < 2 || !parts[1..].Any(p => p.Trim() == "rel=\"next\"")) continue;
            var url = parts[0].Trim().TrimStart('<').TrimEnd('>');
            if (Uri.TryCreate(url, UriKind.Absolute, out var next)) return next;
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
