using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Whallee.Core.Media;

/// <summary>
/// Fetches the leading bytes of a media URL with explicit bounded Range requests.
/// </summary>
/// <remarks>
/// THE WHOLE TRICK IS THAT THE RANGE IS BOUNDED. On this network an open-ended request - which is
/// what ffmpeg and yt-dlp's own downloader make - is answered with 403, while an explicit
/// "bytes=N-M" is answered with 206. That difference is the only reason this feature works here.
/// NEVER let ffmpeg touch the network; it only ever sees a local file.
///
/// Measured 2026-08-19 against one video: three 2 MiB chunks succeeded, the fourth returned 403
/// persistently, and neither re-resolving the URL nor waiting two minutes reset the allowance.
/// Whether that ceiling is per-video or per-IP is what the harness `budget` verb measures.
/// </remarks>
public static class RangePrefixFetcher
{
    public const long ChunkSize = 2 * 1024 * 1024;

    private const int MaxConsecutiveFailures = 3;

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        // 30 s per request, not the 100 s default. The default COMPOUNDS: 3 chunks x 3 retries is
        // ~10 minutes for a normal fetch and ~58 for MeasureCeilingAsync, which is far past what
        // a user driving an import will wait. 30 s is generous for a 2 MiB chunk.
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Writes as much of the file's prefix as the server will hand over. Returns bytes written.
    /// </summary>
    public static async Task<long> FetchPrefixAsync(
        DirectMedia media,
        string destinationPath,
        long maxBytes,
        Action<string>? log,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var ceiling = Math.Min(maxBytes, media.ContentLength);
        long written = 0;
        var consecutiveFailures = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            await using var file = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];

            while (written < ceiling && consecutiveFailures < MaxConsecutiveFailures)
            {
                ct.ThrowIfCancellationRequested();

                var from = written;
                var to = Math.Min(from + ChunkSize, ceiling) - 1;

                using var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
                request.Headers.Range = new RangeHeaderValue(from, to);

                HttpResponseMessage? response = null;
                try
                {
                    response = await Client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    // 206 is the success case. A 200 here would mean the server ignored the Range and
                    // is about to send the whole multi-GB file - refuse it rather than swallow it.
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        consecutiveFailures++;
                        log?.Invoke($"chunk at {from:N0}: {(int)response.StatusCode} " +
                                    $"{response.StatusCode} (failure {consecutiveFailures})");
                        continue;
                    }

                    await using var stream = await response.Content
                        .ReadAsStreamAsync(ct).ConfigureAwait(false);

                    // `written` ADVANCES AS EACH BLOCK LANDS, never in one step at the end. That keeps
                    // written == file.Position at every instant, INCLUDING when the read throws
                    // mid-body. A retry then resumes at exactly the first byte that did not arrive.
                    // Advancing only on success would leave partial bytes on disk while `written`
                    // still pointed before them, so the retry would re-request that range and append
                    // it after the partial - duplicating bytes and corrupting the file invisibly,
                    // since a prefix is partial by design and a corrupt one looks identical.
                    var chunkBytes = 0L;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        chunkBytes += read;
                    }

                    if (chunkBytes <= 0) { consecutiveFailures++; continue; }

                    consecutiveFailures = 0;
                    progress?.Report(written);
                    log?.Invoke($"chunk at {from:N0}: 206, {chunkBytes:N0} bytes (total {written:N0})");
                }
                catch (HttpRequestException ex)
                {
                    consecutiveFailures++;
                    log?.Invoke($"chunk at {from:N0}: {ex.Message} (failure {consecutiveFailures})");
                }
                catch (IOException ex)
                {
                    // A mid-body connection reset arrives here, NOT as HttpRequestException: stream
                    // reads are not covered by SendAsync's exception translation. Without this, one
                    // dropped connection aborts the entire download instead of retrying a chunk.
                    consecutiveFailures++;
                    log?.Invoke($"chunk at {from:N0}: {ex.Message} (failure {consecutiveFailures})");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // HttpClient's own timeout arrives as TaskCanceledException, which IS an
                    // OperationCanceledException - indistinguishable from a user cancel by type alone.
                    // The filter is what separates them: if OUR token did not fire, this is a timeout,
                    // so count it as a failure and retry. Genuine cancellation still propagates.
                    consecutiveFailures++;
                    log?.Invoke($"chunk at {from:N0}: timed out (failure {consecutiveFailures})");
                }
                finally
                {
                    response?.Dispose();
                }
            }

            // CancellationToken.None DELIBERATELY: if ct fires after the last chunk landed, a
            // cancellable flush would throw away a download that actually succeeded.
            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return written;
        }
        catch
        {
            // No truncated file may survive a failed or cancelled fetch - see above: it cannot be
            // told from a successful prefix by looking at it.
            try { File.Delete(destinationPath); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Fetches to a throwaway file purely to learn where the ceiling is.</summary>
    public static async Task<long> MeasureCeilingAsync(
        DirectMedia media, Action<string>? log, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"whallee-budget-{Guid.NewGuid():N}.bin");
        try
        {
            // Deliberately allows far more than any measured ceiling: the point is to find the
            // wall, not to assume where it is. RAISED FROM 64 MiB TO 256 MiB on 2026-09-01, because
            // 64 MiB was itself the wall in one measurement - Big Buck Bunny reached the cap with
            // no 403 at all, which censored the data point and left the limit unknown. A cap that
            // stops the measurement is indistinguishable from a server that refuses, so the cap
            // must sit well clear of anything the server might do.
            return await FetchPrefixAsync(media, scratch, 256 * 1024 * 1024, log, null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(scratch)) File.Delete(scratch); } catch (IOException) { }
        }
    }

}
