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
    });

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
        await using var file = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

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
                var copied = await CopyCountingAsync(stream, file, ct).ConfigureAwait(false);

                if (copied <= 0) { consecutiveFailures++; continue; }

                written += copied;
                consecutiveFailures = 0;
                progress?.Report(written);
                log?.Invoke($"chunk at {from:N0}: 206, {copied:N0} bytes (total {written:N0})");
            }
            catch (HttpRequestException ex)
            {
                consecutiveFailures++;
                log?.Invoke($"chunk at {from:N0}: {ex.Message} (failure {consecutiveFailures})");
            }
            finally
            {
                response?.Dispose();
            }
        }

        await file.FlushAsync(ct).ConfigureAwait(false);
        return written;
    }

    /// <summary>Fetches to a throwaway file purely to learn where the ceiling is.</summary>
    public static async Task<long> MeasureCeilingAsync(
        DirectMedia media, Action<string>? log, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"whallee-budget-{Guid.NewGuid():N}.bin");
        try
        {
            // Deliberately allows far more than the measured 6 MiB: the point is to find the wall,
            // not to assume where it is.
            return await FetchPrefixAsync(media, scratch, 64 * 1024 * 1024, log, null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(scratch)) File.Delete(scratch); } catch (IOException) { }
        }
    }

    private static async Task<long> CopyCountingAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }
        return total;
    }
}
