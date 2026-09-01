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
        var meta = await YtDlpProbe.ProbeAsync(ytDlpPath, request.Url, ct).ConfigureAwait(false);
        if (meta is null)
            return new ImportOutcome(false, null, "Could not read that video's details.", "none",
                                     TimeSpan.Zero);

        var staging = DefaultStagingFolder;
        Directory.CreateDirectory(staging);
        var stem = Path.Combine(staging, Guid.NewGuid().ToString("N"));

        try
        {
            var outcome = await TryStrategyAAsync(
                request, ytDlpPath, stem, progress, ct).ConfigureAwait(false);

            outcome ??= await TryStrategyCAsync(
                request, ytDlpPath, ffmpegPath, stem, progress, ct).ConfigureAwait(false);

            if (outcome is null || !outcome.Success || outcome.LibraryPath is null)
                return outcome ?? new ImportOutcome(false, null,
                    "YouTube refused to send any of this video. This is a restriction on this " +
                    "network, not a problem with the link — a different connection will likely " +
                    "lift it.", "none", TimeSpan.Zero);

            var finalPath = MoveIntoLibrary(outcome.LibraryPath, libraryFolder, meta.Title);
            return outcome with { LibraryPath = finalPath };
        }
        finally
        {
            CleanStaging(stem);
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
                     "--download-sections", section,
                     "-f", "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b",
                     "-o", target,
                     request.Url
                 })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return null;

        try
        {
            _ = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
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
            // not an error worth showing the user.
            progress?.Report("Direct section download unavailable; falling back.");
            return null;
        }

        return new ImportOutcome(true, target, "Downloaded the requested section.", "A",
                                 request.Length);
    }

    /// <summary>Bounded prefix fetch, then a local trim. Only ever yields a prefix.</summary>
    private static async Task<ImportOutcome?> TryStrategyCAsync(
        ImportRequest request, string ytDlpPath, string ffmpegPath, string stem,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Fetching what this network allows…");

        var media = await ResolveForPredictionAsync(ytDlpPath, request.Url, ct).ConfigureAwait(false);
        if (media is null) return null;

        var prefix = stem + "-prefix.mp4";
        var fetched = await RangePrefixFetcher.FetchPrefixAsync(
            media, prefix, ImportPlanner.ByteCeiling * 4, null,
            new Progress<long>(b => progress?.Report($"{b / 1024 / 1024} MiB fetched…")),
            ct).ConfigureAwait(false);

        if (fetched <= 0) return null;

        var available = ImportPlanner.TrimTargetFor(fetched, media.ContentLength, media.Duration);
        var length = available < request.Length ? available : request.Length;
        if (length <= TimeSpan.Zero) return null;

        var trimmed = stem + "-c.mp4";

        // START TIME IS NOT AVAILABLE HERE and the dialog must already have said so: a prefix
        // fetch only ever holds the beginning of the file, so seeking into it is meaningless.
        await ClipTrimmer.TrimAsync(ffmpegPath, prefix, trimmed, length, TimeSpan.Zero, ct)
            .ConfigureAwait(false);

        var message = length < request.Length
            ? $"YouTube sent {fetched / 1024 / 1024} MiB before refusing more, which is " +
              $"{length.TotalSeconds:F0} seconds rather than the {request.Length.TotalSeconds:F0} " +
              "requested. This is a restriction on this network, not a problem with the link, " +
              "and the clip is still usable."
            : "Downloaded the requested section.";

        return new ImportOutcome(true, trimmed, message, "C", length);
    }

    /// <summary>
    /// Resolves a YouTube URL to its direct media URL (clen/dur included) via <c>yt-dlp -g</c>.
    /// </summary>
    /// <remarks>
    /// Public: also used to predict what strategy C could obtain before committing to an import,
    /// not only internally by <see cref="TryStrategyCAsync"/>.
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

    private static string MoveIntoLibrary(string finished, string libraryFolder, string title)
    {
        Directory.CreateDirectory(libraryFolder);

        var target = Path.Combine(libraryFolder, ImportPlanner.SlugFor(title));

        // Unlike Add from file, an import DISAMBIGUATES rather than overwrites. Two imports of the
        // same video are usually two different sections, and silently replacing the first would
        // destroy a clip the user chose to keep. The overwrite ruling was about a file the user
        // picked deliberately by name; this filename is generated, so the user never chose it.
        var counter = 1;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        while (File.Exists(target))
            target = Path.Combine(libraryFolder, $"{stem} ({counter++}){extension}");

        File.Move(finished, target);
        return target;
    }

    private static void CleanStaging(string stem)
    {
        foreach (var suffix in new[] { "-a.mp4", "-prefix.mp4", "-c.mp4" })
        {
            try
            {
                var path = stem + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }   // a leftover in staging is harmless; a crash here is not
        }
    }
}
