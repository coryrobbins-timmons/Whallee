using System.Diagnostics;

namespace Whallee.Core.Media;

public sealed record ImportRequest(string Url, TimeSpan Start, TimeSpan Length);

public sealed record ImportOutcome(
    bool Success, string? LibraryPath, string Message, string Strategy, TimeSpan ActualLength);

/// <summary>
/// Downloads a YouTube video into the library. Strategy A first, then C.
/// </summary>
/// <remarks>
/// STAGES IN %LOCALAPPDATA%\Whallee\imports\, NEVER THE LIBRARY FOLDER. The library is
/// OneDrive-backed, so a multi-GB partial there would sync, count against corporate storage, and
/// leave a broken tile if the import failed. Only a finished, trimmed file is moved across.
/// </remarks>
public static class YouTubeImporter
{
    public static string DefaultStagingFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "imports");

    public static async Task<ImportOutcome> ImportAsync(
        ImportRequest request,
        string ytDlpPath,
        string ffmpegPath,
        string libraryFolder,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // AGE-GATED, SO IT CANNOT TOUCH A LIVE IMPORT. Run before the probe: a crash or held
        // handle from an earlier run is the only thing this ever removes. OFFLOADED to a pool
        // thread: this enumerates a OneDrive-backed folder and can recursively delete a
        // multi-GB directory, and ImportAsync is public - nothing guarantees every caller has
        // already moved off a thread that owns desktop window handles.
        await Task.Run(() => SweepStaleResidue(libraryFolder), ct).ConfigureAwait(false);

        var meta = await YtDlpProbe.ProbeAsync(ytDlpPath, request.Url, ct).ConfigureAwait(false);
        if (meta is null)
            return new ImportOutcome(false, null, "Could not read that video's details.", "none",
                                     TimeSpan.Zero);

        // meta.Duration was in hand and never used. --download-sections with -c copy snaps to
        // keyframes and truncates silently past the end of the video, so strategy A would report
        // the length REQUESTED rather than the length obtained.
        var available = meta.Duration - request.Start;
        if (available <= TimeSpan.Zero)
            return new ImportOutcome(false, null,
                "That start time is past the end of the video.", "none", TimeSpan.Zero);

        var clamped = request with
        {
            Length = request.Length < available ? request.Length : available
        };

        // STRATEGY C'S CEILING IS meta.Duration, NOT meta.Duration - Start. Unlike A, C ignores
        // Start entirely - it fetches a prefix and trims from zero - so clamping it against the
        // section-relative `available` above would cap it far too aggressively for a late start
        // time even when the fetched prefix could support more.
        var cCeiling = request.Length < meta.Duration ? request.Length : meta.Duration;

        var staging = Path.Combine(DefaultStagingFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stem = Path.Combine(staging, "import");

        try
        {
            var outcome = await TryStrategyAAsync(
                clamped, ytDlpPath, stem, progress, ct).ConfigureAwait(false);

            // The ORIGINAL, unclamped request is passed here (not `clamped`) so a shortfall
            // message can honestly quote the length the user actually typed.
            outcome ??= await TryStrategyCAsync(
                request, cCeiling, ytDlpPath, ffmpegPath, stem, progress, ct).ConfigureAwait(false);

            if (outcome is null || !outcome.Success || outcome.LibraryPath is null)
                return outcome ?? new ImportOutcome(false, null,
                    "YouTube refused to send any of this video. This is a restriction on this " +
                    "network, not a problem with the link — a different connection will likely " +
                    "lift it.", "none", TimeSpan.Zero);

            var finalPath = await MoveIntoLibraryAsync(
                outcome.LibraryPath, libraryFolder, meta.Title, ct).ConfigureAwait(false);
            return outcome with { LibraryPath = finalPath };
        }
        finally
        {
            CleanStaging(staging);
        }
    }

    /// <summary>Cheapest path: let yt-dlp fetch only the section. Null when it fails.</summary>
    private static async Task<ImportOutcome?> TryStrategyAAsync(
        ImportRequest request, string ytDlpPath, string stem, IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Trying a direct section download…");

        var target = stem + "-a.mp4";
        var section = $"*{request.Start.TotalSeconds:F0}-{(request.Start + request.Length).TotalSeconds:F0}";

        var info = new ProcessStartInfo(ytDlpPath)
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "--no-warnings", "--no-playlist",
                     // WITHOUT THIS, yt-dlp stamps the output file's mtime from the response
                     // Last-Modified header, which googlevideo reports as the video's transcode
                     // date - weeks or years in the past, not when this import ran. --no-mtime
                     // exists so an imported clip is dated when it was actually imported, not
                     // when the source video happened to be uploaded.
                     //
                     // This is UNRELATED to SweepStaleResidue's creation-time gate below, and does
                     // not "defend" it - a stale mtime here could never have reached that gate
                     // either way, because File.Copy propagates the source's LAST-WRITE time, not
                     // its creation time. The gate stands on File.Copy's own behaviour regardless
                     // of what mtime yt-dlp writes. Two independent reasons; a future edit must
                     // not drop one on the assumption the other already covers it.
                     "--no-mtime",
                     "--download-sections", section,
                     "-f", "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b",
                     "-o", target,
                     request.Url
                 })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return null;

        string stderr;
        try
        {
            stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // A cancelled or otherwise-aborted section download must not keep writing to `target`
            // in the background — see ClipTrimmer.TrimAsync, which established this pattern.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }

        if (process.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
        {
            // EXPECTED ON THIS NETWORK. Strategy A delegates fetching to ffmpeg, which makes
            // open-ended requests and gets 403 here. Falling through to C is the normal path,
            // not an error worth showing the user - but if it failed for some OTHER reason
            // (age-gate, sign-in required, geo-block, a stale yt-dlp build), that must be visible
            // somewhere, or a strategy-C failure right after it gets blamed entirely on "this
            // network" when the real cause follows the user to a different one. Same
            // last-stderr-line precedent as ClipTrimmer.TrimAsync.
            var reason = LastLine(stderr);
            progress?.Report(reason.Length == 0
                ? "Direct section download unavailable; falling back."
                : $"Direct section download unavailable; falling back. ({reason})");
            return null;
        }

        return new ImportOutcome(true, target, "Downloaded the requested section.", "A",
                                 request.Length);
    }

    /// <summary>Bounded prefix fetch, then a local trim. Only ever yields a prefix.</summary>
    /// <param name="request">
    /// The ORIGINAL, unclamped request — its <c>Length</c> is what the shortfall message quotes,
    /// so the user is never told they asked for a number they never typed.
    /// </param>
    /// <param name="cCeiling">
    /// min(request.Length, video duration) - strategy C's true ceiling, computed by the caller
    /// since C ignores Start and so must not be clamped by the section-relative arithmetic that
    /// strategy A uses.
    /// </param>
    private static async Task<ImportOutcome?> TryStrategyCAsync(
        ImportRequest request, TimeSpan cCeiling, string ytDlpPath, string ffmpegPath,
        string stem, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Fetching what this network allows…");

        var media = await ResolveForPredictionAsync(ytDlpPath, request.Url, ct).ConfigureAwait(false);
        if (media is null) return null;

        var prefix = stem + "-prefix.mp4";

        // The server's own refusal is the real limit (ContentCeiling seconds of content, not a
        // byte count) - this only sizes the request so we don't ask for a whole multi-gigabyte
        // file when we only want the opening minute or less of it.
        var wanted = cCeiling < ImportPlanner.ContentCeiling ? cCeiling : ImportPlanner.ContentCeiling;
        var maxBytes = ImportPlanner.BytesForSeconds(wanted, media.ContentLength, media.Duration);

        // BytesForSeconds returns 0 when ContentLength or Duration is non-positive, which would
        // otherwise fetch nothing and fall through to the "YouTube refused to send any of this
        // video" message below - blaming the network for a request that was never made. The old
        // fixed-constant maxBytes could never be empty; this computed one can, so guard it
        // explicitly. Unreachable today because DirectMediaUrl.Parse rejects a non-positive dur
        // or clen before a DirectMedia is ever constructed, but DirectMedia is a public record and
        // this call site should not depend on that.
        if (maxBytes <= 0)
            return new ImportOutcome(false, null,
                "Could not work out how much of this video to fetch - its length or size was " +
                "not reported. This is not a network problem.", "C", TimeSpan.Zero);

        var fetched = await RangePrefixFetcher.FetchPrefixAsync(
            media, prefix, maxBytes, null,
            new Progress<long>(b => progress?.Report($"{b / 1024 / 1024} MiB fetched…")),
            ct).ConfigureAwait(false);

        if (fetched <= 0) return null;

        var available = ImportPlanner.TrimTargetFor(fetched, media.ContentLength, media.Duration);
        var length = available < cCeiling ? available : cCeiling;
        if (length <= TimeSpan.Zero) return null;

        var trimmed = stem + "-c.mp4";

        // START TIME IS NOT AVAILABLE HERE and the dialog must already have said so: a prefix
        // fetch only ever holds the beginning of the file, so seeking into it is meaningless.
        await ClipTrimmer.TrimAsync(ffmpegPath, prefix, trimmed, length, TimeSpan.Zero, ct)
            .ConfigureAwait(false);

        // THREE DISTINCT OUTCOMES, not two - length alone cannot tell "got the section" from "got
        // the wrong section at full length". Ask for start 1:30, length 0:30 on a 5-minute video:
        // C always fetches from 0:00, so a length match here means the OPENING 30 seconds, not the
        // requested ones. Silently calling that "the requested section" would be confidently wrong.
        string message;
        if (length < request.Length)
        {
            // Existing shortfall message - left exactly as it was. It already covers the length
            // mismatch; whether Start was also non-zero doesn't change what happened here (a
            // prefix that came up short), so it isn't mentioned.
            message = $"YouTube sent {fetched / 1024 / 1024} MiB before refusing more, which is " +
                      $"{length.TotalSeconds:F0} seconds rather than the {request.Length.TotalSeconds:F0} " +
                      "requested. This is a restriction on this network, not a problem with the link, " +
                      "and the clip is still usable.";
        }
        else if (request.Start > TimeSpan.Zero)
        {
            message = "This clip starts from the beginning of the video, not the requested " +
                      $"{request.Start.TotalSeconds:F0}-second start time. Strategy C can only " +
                      "fetch a prefix of the file, so it can never seek into the middle - only " +
                      "how much of the opening it captured, never where it starts.";
        }
        else
        {
            message = "Downloaded the requested section.";
        }

        return new ImportOutcome(true, trimmed, message, "C", length);
    }

    /// <summary>
    /// Resolves a YouTube URL to its direct media URL (clen/dur included) via <c>yt-dlp -g</c>.
    /// </summary>
    /// <remarks>
    /// Public because Whallee.Harness's <c>budget</c> verb calls it directly to measure what a URL
    /// would yield before an import is ever attempted, not only internally by
    /// <see cref="TryStrategyCAsync"/>. (An earlier caller that used this to preview strategy C's
    /// outcome from the UI was removed when the duration-ceiling model landed; this is the one that
    /// remains.)
    /// </remarks>
    public static async Task<DirectMedia?> ResolveForPredictionAsync(
        string ytDlpPath, string url, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ytDlpPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "--no-warnings", "--no-playlist", "-g", url })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return null;

        string stdout;
        try
        {
            // BOTH STREAMS ARE DRAINED CONCURRENTLY, and the reads start before either is awaited.
            // Redirecting stderr and never reading it deadlocks: the stdout read only returns when
            // the child closes its handle at exit, but a child blocked writing to a full stderr
            // pipe never exits. Found by review in Task 2, which had this same defect.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            stdout = await stdoutTask.ConfigureAwait(false);
        }
        finally
        {
            // Same rationale as ClipTrimmer.TrimAsync and TryStrategyAAsync: cancellation must not
            // leave a yt-dlp process running in the background.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }

        return process.ExitCode != 0
            ? null
            : DirectMediaUrl.ParseFirst(
                stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                   StringSplitOptions.TrimEntries));
    }

    private static async Task<string> MoveIntoLibraryAsync(
        string finished, string libraryFolder, string title, CancellationToken ct)
    {
        Directory.CreateDirectory(libraryFolder);

        var slug = ImportPlanner.SlugFor(title);
        var stem = Path.GetFileNameWithoutExtension(slug);
        var extension = Path.GetExtension(slug);

        // UNIQUE, NOT DERIVED FROM THE TITLE. A name derived purely from the title collides
        // between two concurrent imports of the SAME video: the second import's File.Copy would
        // throw on the first import's exclusively-open temp mid-copy (not a move collision, so
        // the retry filter below would not catch it, and the clip would be deleted outright) - or,
        // with overwrite, silently truncate and refill the first import's already-finished temp,
        // so the first import's later File.Move would publish the SECOND video's bytes under the
        // FIRST video's name. A GUID suffix makes that collision effectively impossible, so
        // overwrite is dropped too - a real collision here should fail loudly, never clobber.
        var temp = Path.Combine(libraryFolder,
            $"{stem}{extension}.{Guid.NewGuid():N}.whallee-tmp");

        // COPY UNDER A NAME THE LIBRARY SCAN IGNORES, THEN RENAME. ClipResolver only accepts
        // .mp4/.mkv/.webm/.mov, so this suffix is invisible to it. Copying straight to the final
        // name lets the engine pick up a half-written file as a real clip mid-copy - and File.Move
        // is only atomic within a volume, which a user-configured library folder cannot guarantee.
        try
        {
            File.Copy(finished, temp);

            for (var counter = 0; counter < 100; counter++)
            {
                var target = counter == 0
                    ? Path.Combine(libraryFolder, slug)
                    : Path.Combine(libraryFolder, $"{stem} ({counter}){extension}");

                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        // No overwrite, deliberately. Two concurrent imports of one title race for
                        // the name; losing that race must cost a retry, never the clip.
                        File.Move(temp, target);
                        return target;
                    }
                    catch (IOException) when (File.Exists(target))
                    {
                        break;   // name claimed - take the next candidate
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        // TRANSIENT LOCK ON temp, not a name collision: OneDrive sync or an AV
                        // scanner opening the file it just saw appear. This must cost a short
                        // wait, never the finished clip.
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                }
            }

            throw new IOException($"Could not find a free name for {slug} in the library.");
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Same precedent as ClipTrimmer.LastLine: the last non-empty line of a process's stderr.</summary>
    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }

    /// <summary>
    /// Removes the whole per-import directory. NOT a list of known suffixes: yt-dlp writes
    /// separate stream files (.f137.mp4, .f140.m4a) plus .part and .ytdl scratch, and merges them
    /// only on success - so the failing path, which is the NORMAL path here, is exactly the one
    /// that leaves the big files behind.
    /// </summary>
    private static void CleanStaging(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes residue a crash or a held handle left behind. AGE-GATED so it can never touch a
    /// live import: anything younger than the cutoff may still be in flight.
    /// </summary>
    /// <remarks>
    /// Two residue classes accumulate without a sweep, both unbounded and both large: an orphaned
    /// ".whallee-tmp" in the OneDrive-backed library (a crash or power loss between the copy and
    /// the rename in MoveIntoLibraryAsync), and a whole staging GUID directory under
    /// DefaultStagingFolder (CleanStaging's own delete can fail - e.g. Process.Kill only
    /// requests termination, so a handle still open when the recursive delete runs leaks every
    /// yt-dlp fragment underneath it).
    /// </remarks>
    private static void SweepStaleResidue(string libraryFolder)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);

        try
        {
            foreach (var file in Directory.EnumerateFiles(libraryFolder, "*.whallee-tmp"))
            {
                try
                {
                    // CREATION TIME, NOT LAST-WRITE TIME. File.Copy preserves the SOURCE's
                    // last-write time rather than stamping now, and yt-dlp's strategy-A output
                    // (before --no-mtime was added) could itself carry a stale mtime from
                    // googlevideo's Last-Modified header - either way, a fresh GUID-named temp
                    // could already read as ancient by LastWriteTimeUtc. CreationTimeUtc is set
                    // to now for a new file regardless, and the GUID name guarantees this file is
                    // new. Do not "fix" this back to LastWriteTimeUtc.
                    if (File.GetCreationTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(DefaultStagingFolder))
            {
                try
                {
                    // CREATION TIME, NOT LAST-WRITE TIME. A directory's LastWriteTimeUtc advances
                    // only when entries are added or removed, not while data is written into a
                    // file already inside it - so a long-running strategy-A download whose
                    // fragments were created at t=0 and are still filling hours later would read
                    // as stale by write time while genuinely live. CreationTimeUtc is set once,
                    // when this per-import directory was made, and never changes underneath it.
                    // Do not "fix" this back to LastWriteTimeUtc.
                    if (Directory.GetCreationTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, recursive: true);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
