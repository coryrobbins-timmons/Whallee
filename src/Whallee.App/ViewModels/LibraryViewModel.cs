using System.Collections.ObjectModel;
using System.IO;
using Whallee.App.Services;
using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Displays;
using Whallee.Core.Media;
// ALIASED, not a plain "using Microsoft.VisualBasic.FileIO". That namespace also contains a
// SearchOption type, which would become CS0104-ambiguous against System.IO.SearchOption for anyone
// who later reaches for it in this file. Three aliases plant no such landmine.
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VbRecycle = Microsoft.VisualBasic.FileIO.RecycleOption;
using VbUiOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace Whallee.App.ViewModels;

/// <summary>
/// Manages the clip library — importing, deleting, renaming, and pinning clips to displays.
/// </summary>
/// <remarks>
/// RESTART POLICY across public methods that touch config:
///
/// • Constructor persists discovered ffmpeg path with SaveConfig. Recording where an
///   executable lives is no reason to restart the engine.
///
/// • <c>ApplyClip</c> always uses ApplyConfigChange and restarts unconditionally. It pins a
///   clip to a display and the engine must re-attach.
///
/// • <c>ImportFileAsync</c> touches no config — it only copies the file into the library.
///
/// • <c>ImportFromUrlAsync</c> persists discovered yt-dlp path with SaveConfig only.
///   Recording where an executable lives is no reason to restart the engine.
///
/// • <c>DeleteClipAsync</c> uses ApplyConfigChange only when something actually referenced
///   the deleted clip (checked by ClipRemoval.ClearAssignments). Deleting an unpinned clip
///   costs no restart.
///
/// • <c>RenameClipAsync</c> always uses SaveConfig and never restarts. mpv does not lock a
///   clip it is playing (measured 2026-09-03), so a display showing the renamed clip keeps
///   playing uninterrupted while config is repointed underneath it.
/// </remarks>
public sealed class LibraryViewModel
{
    private readonly EngineHost _engine;
    private readonly UiReporter _reporter;
    private readonly ThumbnailService _thumbnails;
    private readonly bool _canMakeThumbnails;
    private readonly List<string?> _targetDeviceNames = new();

    public ObservableCollection<ClipTile> Tiles { get; } = new();

    /// <summary>"All displays", then one entry per attached display.</summary>
    public ObservableCollection<string> Targets { get; } = new();

    /// <summary>Device names in the same order as <see cref="Targets"/>, null for "All displays".</summary>
    public IReadOnlyList<string?> TargetDeviceNames => _targetDeviceNames;

    /// <summary>
    /// The persisted yt-dlp path, so a caller that needs to locate yt-dlp before
    /// <see cref="ImportFromUrlAsync"/> runs (e.g. to build the URL-import dialog) can pass the
    /// same hint <see cref="ImportFromUrlAsync"/> itself uses, rather than a bare <c>null</c> that
    /// would skip the configured path and force a PATH/winget scan even when the configured path
    /// still works.
    /// </summary>
    public string? ConfiguredYtDlpPath => _engine.Config.YtDlpPath;

    /// <summary>
    /// The ffmpeg path in use, already discovered and persisted by the constructor. Mirrors
    /// <see cref="ConfiguredYtDlpPath"/>. Empty when ffmpeg was not found, in which case callers
    /// skip probing rather than launching nothing.
    /// </summary>
    public string ConfiguredFfmpegPath => _engine.Config.FfmpegPath;

    /// <summary>The live config, for callers that need to ask what a clip is pinned to.</summary>
    public WhalleeConfig Config => _engine.Config;

    /// <summary>
    /// Where each imported clip came from. A miss is normal, never an error.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM config.json ON PURPOSE. This is a convenience record - it feeds one row in
    /// Details and prefills "Edit…" - and must never be able to take the engine's configuration
    /// down with it. ClipSourceStore.Load cannot throw and Save swallows, so the worst case is a
    /// missing row.
    /// </remarks>
    private readonly ClipSourceIndex _sources = ClipSourceStore.Load(ClipSourceStore.DefaultPath);

    /// <summary>Where <paramref name="fileName"/> came from, or null if it was never recorded.</summary>
    public ClipSource? SourceOf(string? fileName) => _sources.Get(fileName);

    /// <summary>Records where a freshly imported clip came from.</summary>
    public void RecordSource(string fileName, ClipSource source)
    {
        _sources.Record(fileName, source);
        ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
    }

    /// <summary>The genres, in the owner's order.</summary>
    public IReadOnlyList<string> Genres => _sources.Genres;

    /// <summary>The genre a clip is filed under, or null for ungrouped.</summary>
    public string? GenreOf(string? fileName) => _sources.GenreOf(fileName);

    /// <summary>Files a clip under a genre, or under none when <paramref name="genre"/> is null.</summary>
    public void AssignGenre(string fileName, string? genre)
    {
        _sources.Assign(fileName, genre);
        ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
    }

    /// <summary>Renames a genre and retags its clips. True if there was one to rename.</summary>
    public bool RenameGenre(string oldName, string newName)
    {
        if (!_sources.RenameGenre(oldName, newName)) return false;

        ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
        return true;
    }

    /// <summary>
    /// Removes a genre and unassigns its clips. NEVER deletes a video. True if there was one.
    /// </summary>
    public bool DeleteGenre(string name)
    {
        if (!_sources.DeleteGenre(name)) return false;

        ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
        return true;
    }

    /// <summary>Moves a genre in the bar. Returns its new index, or -1 if it is not there.</summary>
    public int MoveGenre(string name, int delta)
    {
        var to = _sources.MoveGenre(name, delta);
        if (to >= 0) ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
        return to;
    }

    public LibraryViewModel(EngineHost engine, UiReporter reporter)
    {
        _engine = engine;
        _reporter = reporter;

        var ffmpeg = FfmpegLocator.Locate(engine.Config.FfmpegPath) ?? "";
        _canMakeThumbnails = !string.IsNullOrEmpty(ffmpeg);

        if (_canMakeThumbnails && ffmpeg != engine.Config.FfmpegPath)
        {
            // Persist what discovery found, so the settings page shows a real path and the next
            // run skips the search. SaveConfig, not ApplyConfigChange: recording where an
            // executable lives is no reason to restart three mpv processes.
            engine.Config.FfmpegPath = ffmpeg;
            engine.SaveConfig();
        }
        else if (!_canMakeThumbnails)
        {
            _reporter.Report("ffmpeg not found — tiles will show without thumbnails");
        }

        _thumbnails = new ThumbnailService(ffmpeg, ThumbnailService.DefaultCacheFolder);
        RefreshTargets();
    }

    /// <summary>
    /// Rebuilds the target list from the displays attached right now.
    /// </summary>
    /// <remarks>
    /// EXISTS BECAUSE THE PAGE IS CACHED. The plan rebuilt the whole page on every switch, so its
    /// target list was incidentally always fresh; reusing one instance means the list would
    /// otherwise be frozen at whatever was attached when the window first opened. Called whenever
    /// the page is shown.
    ///
    /// Returns early when nothing changed, which is the normal case - rebuilding the collection
    /// clears the ComboBox selection, and silently resetting the user's chosen target every time
    /// they switch pages would be its own bug.
    /// </remarks>
    public void RefreshTargets()
    {
        var displays = DisplayEnumerator.Enumerate();
        var deviceNames = new string?[] { null }
            .Concat(displays.Select(d => (string?)d.DeviceName)).ToList();

        if (deviceNames.SequenceEqual(_targetDeviceNames)) return;

        _targetDeviceNames.Clear();
        _targetDeviceNames.AddRange(deviceNames);

        Targets.Clear();
        Targets.Add("All displays");
        foreach (var display in displays) Targets.Add(display.DeviceName);
    }

    /// <summary>
    /// Pins a clip to one display, or to all of them. Writes config and re-attaches.
    /// </summary>
    public void ApplyClip(string clipPath, string? deviceName)
    {
        // PinnedPath is stored RELATIVE to the library folder. ClipResolver rejects an absolute
        // path that points outside the library, and Path.Combine would silently ignore the library
        // folder entirely for any rooted value.
        var relative = System.IO.Path.GetFileName(clipPath);

        var applied = 0;
        foreach (var assignment in _engine.Config.Displays)
        {
            if (deviceName is not null &&
                !string.Equals(assignment.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            assignment.Mode = DisplayMode.Pinned;
            assignment.PinnedPath = relative;
            applied++;
        }

        if (applied == 0)
        {
            // Only displays already in config can be updated: a display attached but never seen by
            // Start() has no assignment row. Start() creates rows for everything attached, so this
            // should be unreachable - but the plan flags "All displays appears to skip a monitor"
            // as a real symptom, and a silent no-op is exactly what makes that hard to diagnose.
            _reporter.Report(
                $"nothing applied — no saved display matches {deviceName ?? "any attached display"}");
            return;
        }

        _reporter.Report($"applying {relative} to {deviceName ?? "all displays"}");
        _engine.ApplyConfigChange();
    }

    /// <summary>
    /// Copies a file into the library folder. Returns the path the clip now lives at, or null if
    /// its extension is not one the engine can play.
    /// </summary>
    /// <remarks>
    /// EVERY FILESYSTEM CALL IS INSIDE THE Task.Run, not just the copy. The plan left
    /// CreateDirectory and the File.Exists probes on the UI thread; this thread owns host windows
    /// parented into Explorer's desktop, and the library folder is OneDrive-backed here, where
    /// those are precisely the calls that stall. Same reasoning that moved the directory listing
    /// off the UI thread in LoadAsync.
    ///
    /// AN EXISTING CLIP OF THE SAME NAME IS OVERWRITTEN. Owner's call, made after the alternative
    /// was built and the trade explained: PinnedPath refers to a clip BY NAME, so replacing a
    /// pinned clip changes what that monitor plays without touching config. Disambiguating instead
    /// (the plan's behaviour, "name (1).mp4") avoids that but litters the library with near
    /// duplicates. Settled - do not relitigate.
    ///
    /// MPV DOES NOT BLOCK THE OVERWRITE. Measured 2026-09-01: replacing forest.mp4 while it was
    /// pinned to DISPLAY6 and playing succeeded, with three mpv processes live. Predicting a
    /// sharing violation there was wrong, so the IOException below is DEFENSIVE AND UNEXERCISED -
    /// some other holder (a scanner, another player, an open editor) could still lock the target.
    /// Do not cite it as the expected path.
    ///
    /// A successful replace does NOT restart the engine, so a display already playing that clip
    /// keeps showing the old content until the next restart. UNVERIFIED: the only replace-while-
    /// playing test used byte-identical content, which cannot show a stale frame either way.
    /// </remarks>
    public async Task<string?> ImportFileAsync(string sourcePath, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!ClipResolver.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return null;

        var libraryFolder = _engine.Config.LibraryFolder;

        return await Task.Run(() =>
        {
            Directory.CreateDirectory(libraryFolder);

            // Picking a file that already lives in the library is something the file dialog openly
            // invites, and it must be caught BEFORE the copy: File.Copy throws when source and
            // destination are the same file, so without this the user gets an IOException for
            // doing something entirely reasonable. Not the plan's behaviour; the owner has since
            // chosen overwrite over disambiguation, which makes this guard load-bearing rather
            // than merely tidy.
            var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (sourceFolder is not null && string.Equals(
                    sourceFolder.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(libraryFolder).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                // NAMES THE FOLDER IT WAS PICKED FROM. The first version of this message said only
                // "X is already in the library", which cannot be told apart from a misfiring
                // comparison - it cost a round trip on exactly that ambiguity.
                _reporter.Report($"not copied: {Path.GetFileName(sourcePath)} was picked from the "
                                 + $"library folder itself ({sourceFolder})");
                return sourcePath;
            }

            var target = Path.Combine(libraryFolder, Path.GetFileName(sourcePath));
            var replacing = File.Exists(target);

            try
            {
                // DELIBERATELY OVERWRITES A FILE OF THE SAME NAME. See RenameClipAsync's clash check:
                // an import overwrite replaces the file the user just chose, which is predictable; a
                // rename overwrite would destroy a different clip the user never mentioned. Both are
                // intentional and differ on purpose.
                File.Copy(sourcePath, target, overwrite: true);
            }
            catch (IOException ex) when (replacing)
            {
                // NOT the mpv-is-playing case: that was tested and mpv does not block the write.
                // This is whatever else may hold the target open - a scanner mid-pass, another
                // player, an open editor. Raw, it surfaces as "the process cannot access the
                // file", which says nothing about what to do, so name the shape of the problem
                // without claiming a cause that was already disproved.
                throw new IOException(
                    $"could not replace {Path.GetFileName(target)} — another program is holding "
                    + "that file open. Close whatever is using it, then try again.", ex);
            }

            // The thumbnail takes care of itself: the cache key hashes path AND last-write time,
            // and File.Copy carries the source's timestamp over, so replaced content re-renders.
            _reporter.Report(replacing
                ? $"replaced {Path.GetFileName(target)} in the library"
                : $"added {Path.GetFileName(target)} to the library");
            return target;
        }, ct).ConfigureAwait(true);   // back to the UI thread; the caller refreshes the grid
    }

    /// <summary>
    /// Imports a YouTube URL into the library. All process and network work is off the UI thread.
    /// </summary>
    /// <remarks>
    /// BOTH Locate CALLS ARE INSIDE THE Task.Run TOO, not just ImportAsync. This thread owns host
    /// windows re-parented into Explorer's desktop, and on a first-ever import both configured
    /// paths are null - so Locate does a full PATH walk plus a winget-tree enumeration
    /// (Directory.EnumerateDirectories), with FfmpegLocator's search heavier still. A stall there
    /// freezes the user's whole desktop exactly as a stall inside ImportAsync would; there is
    /// nothing that makes the locate step safer to leave on the caller's thread.
    ///
    /// PERSISTS A DISCOVERED yt-dlp PATH THE SAME WAY THE CONSTRUCTOR PERSISTS ffmpeg's: SaveConfig,
    /// not ApplyConfigChange. Recording where an executable lives is no reason to restart three mpv
    /// processes.
    /// </remarks>
    public async Task<ImportOutcome> ImportFromUrlAsync(
        ImportRequest request, IProgress<string>? progress, CancellationToken ct = default)
    {
        var outcome = await Task.Run(async () =>
        {
            var ytDlp = YtDlpLocator.Locate(_engine.Config.YtDlpPath);
            if (ytDlp is null)
                return new ImportOutcome(false, null, "yt-dlp was not found on this machine.",
                                         "none", TimeSpan.Zero);

            if (ytDlp != _engine.Config.YtDlpPath)
            {
                _engine.Config.YtDlpPath = ytDlp;
                _engine.SaveConfig();
            }

            var ffmpeg = FfmpegLocator.Locate(_engine.Config.FfmpegPath);
            if (ffmpeg is null)
                return new ImportOutcome(false, null, "ffmpeg was not found, so a clip cannot be cut.",
                                         "none", TimeSpan.Zero);

            return await YouTubeImporter.ImportAsync(
                request, ytDlp, ffmpeg, _engine.Config.LibraryFolder, progress, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(true);

        _reporter.Report(outcome.Success
            ? $"imported {Path.GetFileName(outcome.LibraryPath)} ({outcome.Strategy}, " +
              $"{outcome.ActualLength.TotalSeconds:F0}s)"
            : $"import failed: {outcome.Message}");

        return outcome;
    }

    /// <summary>
    /// Fills the grid, then generates any missing thumbnails.
    /// </summary>
    /// <remarks>
    /// EVERY EXPENSIVE STEP IS PUSHED OFF THE UI THREAD, including the directory listing. This
    /// thread owns host windows parented into Explorer's desktop, so blocking it freezes the
    /// user's whole desktop - and the library folder is OneDrive-backed here, where a plain
    /// directory enumeration can stall on a cloud placeholder.
    ///
    /// Thumbnail generation is SEQUENTIAL, not parallel. Measured at ~1.2 s per clip cold, so four
    /// clips is ~5 s of ffmpeg. Running them concurrently on a machine already driving three mpv
    /// instances is not a trade worth making, and tiles filling in one by one reads as progress
    /// rather than as a stall.
    /// </remarks>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Tiles.Clear();

        var folder = _engine.Config.LibraryFolder;
        IReadOnlyList<string> files;
        try
        {
            files = await Task.Run(() => ClipResolver.ListLibraryFiles(folder), ct)
                              .ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _reporter.Report($"could not read the library folder: {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var tile = new ClipTile { Path = file };
            tile.Genre = _sources.GenreOf(tile.FileName);
            Tiles.Add(tile);
        }

        if (files.Count == 0)
            _reporter.Report($"no clips found in {folder}");

        if (!_canMakeThumbnails) return;

        foreach (var tile in Tiles.ToList())
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // Task.Run wraps an already-async method deliberately: EnsureThumbnailAsync has a
                // SYNCHRONOUS prologue (File.Exists, GetLastWriteTimeUtc, CreateDirectory) that
                // would otherwise run on the UI thread, and on OneDrive-backed files those calls
                // are exactly the ones that can stall.
                var path = await Task.Run(() => _thumbnails.EnsureThumbnailAsync(tile.Path, ct), ct)
                                     .ConfigureAwait(true);   // back to the UI thread to assign

                tile.ThumbnailPath = path;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // One bad clip must not abandon the rest of the grid, and must not reach the
                // Dispatcher unhandled - this runs from an async void Loaded handler, where an
                // escaping exception would take the process down.
                _reporter.Report($"thumbnail failed for {tile.FileName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes a clip from the library, sending it to the Recycle Bin. Returns true when the file
    /// is gone - including when it turned out to be gone already.
    /// </summary>
    /// <remarks>
    /// OPTIMISTIC, ESCALATING ONLY ON FAILURE, AND THE ORDER IS LOAD-BEARING. The obvious design -
    /// unpin first so mpv lets go, then delete - was designed, approved, and then DISCARDED after
    /// reading ClipResolver.Resolve:
    ///
    ///   1. A SHUFFLE DISPLAY HOLDS A CLIP TOO. Resolve picks at random from the library and mpv
    ///      loops it, so unpinning cannot guarantee the handle is released - and a restart
    ///      RE-RANDOMISES, so it can land back on the very clip being deleted.
    ///   2. A DANGLING PinnedPath IS ALREADY HARMLESS. Resolve falls through to a random pick when
    ///      the pinned clip is gone, with a comment in that file saying exactly that.
    ///
    /// So unpinning first bought a ~3 s restart and a "lost the pin for nothing" failure mode, in
    /// exchange for a handle release it could not deliver, to prevent something already benign.
    ///
    /// DELETE-WHILE-PLAYING IS UNMEASURED on this machine. CLAUDE.md records only that mpv does not
    /// block an OVERWRITE, and explicitly flags predicting a sharing violation as having been wrong
    /// once already. The retry below is therefore the ESCALATION path, not the expected one - and
    /// it can still fail, which is why its message says so plainly instead of reassuring.
    /// </remarks>
    public async Task<bool> DeleteClipAsync(string clipPath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(clipPath);

        // FORGOTTEN ON THE PATHS WHERE THE CLIP IS ACTUALLY GONE, never up front. It used to run
        // here, before the recycle, which meant a REFUSED delete (the file is held - SentinelOne
        // denies a replace on this machine) still stripped the clip's genre and source link from
        // disk while the video stayed put. Worse, returning false skips the caller's reload, so the
        // tile on screen kept showing a genre the index no longer had, and the clip silently dropped
        // to Ungrouped at the next start. The entry is the clip's filing; it dies with the clip.
        //
        // A stale entry would resurface as a wrong source if something were later imported under the
        // same name, which is exactly why every gone path below calls this.
        void ForgetSource()
        {
            if (_sources.Remove(fileName))
                ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);
        }

        // CAPTURED BEFORE THE FILE GOES AWAY. The thumbnail cache key hashes path AND last-write
        // time, so once the clip is recycled there is no way to work out which cached jpg was its.
        DateTime? stamp = null;
        try { stamp = File.GetLastWriteTimeUtc(clipPath); }
        catch (Exception) { /* no stamp just means the cached thumbnail is left alone */ }

        try
        {
            await RecycleAsync(clipPath, ct).ConfigureAwait(true);
        }
        // BEFORE the IOException catch, and that ordering is required: FileNotFoundException
        // DERIVES from IOException, so the general catch would swallow it first.
        catch (FileNotFoundException)
        {
            // "Already gone" COUNTS AS GONE: there is no clip left for the entry to describe.
            _reporter.Report($"{fileName} was already gone");
            ForgetSource();
            ClipRemoval.ClearAssignments(_engine.Config, fileName);
            _engine.SaveConfig();
            return true;
        }
        catch (IOException first)
        {
            // Something is holding the file. Move the pinned displays off it and try once more.
            var cleared = ClipRemoval.ClearAssignments(_engine.Config, fileName);
            if (cleared == 0)
            {
                // Nothing referenced it, so there is nothing to escalate to - some other program
                // holds it. Retrying would just fail again.
                _reporter.Report($"could not delete {fileName}: {first.Message}");
                return false;
            }

            _reporter.Report($"{fileName} is in use — unpinning {cleared} display(s) and retrying");
            _engine.ApplyConfigChange();

            try
            {
                await RecycleAsync(clipPath, ct).ConfigureAwait(true);
            }
            catch (Exception retry)
            {
                // HONEST, not reassuring. A shuffling display may have re-picked the clip after the
                // restart, which is exactly the case this retry cannot fix.
                _reporter.Report(
                    $"could not delete {fileName}: {retry.Message} — it is still in the library. " +
                    "Something is still playing or holding it.");
                return false;
            }

            ForgetSource();
            DeleteCachedThumbnail(clipPath, stamp);
            _reporter.Report($"deleted {fileName} — moved to the Recycle Bin");
            return true;
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not delete {fileName}: {ex.Message}");
            return false;
        }

        // The file is gone on the first attempt. Clear config, and re-attach ONLY if anything
        // actually referenced it - deleting a clip nothing was using costs no restart at all.
        ForgetSource();

        var references = ClipRemoval.ClearAssignments(_engine.Config, fileName);
        if (references > 0) _engine.ApplyConfigChange();
        else _engine.SaveConfig();

        DeleteCachedThumbnail(clipPath, stamp);

        _reporter.Report($"deleted {fileName} — moved to the Recycle Bin");
        return true;
    }

    /// <summary>
    /// Renames a clip in the library, keeping its extension, and repoints any pin that named it.
    /// Returns true when the file was renamed.
    /// </summary>
    /// <remarks>
    /// NO ENGINE RESTART - SaveConfig, not ApplyConfigChange. mpv holds an open handle and DOES NOT
    /// LOCK A CLIP IT IS PLAYING, measured 2026-09-03 when deleting a playing clip succeeded and the
    /// display fell back to a random pick. So a display showing the renamed clip keeps playing it
    /// uninterrupted, and config is already repointed for the next restart. Restarting would cost a
    /// ~3 s flicker on three displays to change nothing visible.
    ///
    /// CONFIG IS TOUCHED ONLY AFTER THE FILE HAS ACTUALLY MOVED, so a failed rename cannot leave a
    /// pin naming a file that does not exist.
    ///
    /// NO RETRY AROUND File.Move. CLAUDE.md records that SentinelOne can refuse a File.Move ONTO an
    /// existing file with ERROR_ACCESS_DENIED, and asks new replace-in-place code to retry - but
    /// this is not replace-in-place: a clash is refused below, so the target never exists. Revisit
    /// only if a rename is actually seen to fail that way.
    /// </remarks>
    public async Task<bool> RenameClipAsync(string clipPath, string newBaseName,
                                            CancellationToken ct = default)
    {
        // RE-VALIDATED even though the dialog gates its button on the same call. This is a public
        // method and must not depend on its only current caller having checked; the alternative is
        // handing File.Move a name Windows will mangle or reject.
        var invalid = ClipRename.Validate(newBaseName);
        if (invalid is not null)
        {
            _reporter.Report($"could not rename: {invalid}");
            return false;
        }

        // NORMALISED HERE TOO, not only in the dialog. Validate judges the NORMALISED form, so
        // accepting the raw string would let a trailing space reach File.Move - where Windows strips
        // it anyway, leaving the file, config and the grid disagreeing about the clip's name.
        newBaseName = ClipRename.Normalise(newBaseName);

        var folder = Path.GetDirectoryName(clipPath) ?? _engine.Config.LibraryFolder;
        var oldName = Path.GetFileName(clipPath);
        var newName = newBaseName + Path.GetExtension(clipPath);
        var newPath = Path.Combine(folder, newName);

        // THE CLASH CHECK IGNORES THE SOURCE ITSELF. A case-only rename (Forest.mp4 -> forest.mp4)
        // targets the same file, so a bare File.Exists would refuse a perfectly legal rename.
        var sameFile = string.Equals(Path.GetFullPath(clipPath), Path.GetFullPath(newPath),
                                     StringComparison.OrdinalIgnoreCase);

        // DELIBERATELY REFUSES A NAME CLASH. See ImportFileAsync: an import overwrite replaces the
        // file the user just chose, which is predictable; a rename overwrite would destroy a different
        // clip the user never mentioned. Both are intentional and differ on purpose.
        if (!sameFile && File.Exists(newPath))
        {
            _reporter.Report($"could not rename: {newName} is already in the library");
            return false;
        }

        // CAPTURED BEFORE THE MOVE: the thumbnail cache key hashes path AND last-write time, so
        // once the file has moved there is no way to derive the old key.
        DateTime? stamp = null;
        try { stamp = File.GetLastWriteTimeUtc(clipPath); }
        catch (Exception) { /* no stamp just means the thumbnail regenerates instead */ }

        try
        {
            await Task.Run(() => File.Move(clipPath, newPath), ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not rename {oldName}: {ex.Message}");
            return false;
        }

        MoveCachedThumbnail(clipPath, newPath, stamp);

        // The source link follows the file, for the same reason the pins do: a rename that left it
        // behind would lose where the clip came from silently and permanently, and Details would
        // start claiming an imported clip had no source.
        if (_sources.Rename(oldName, newName))
            ClipSourceStore.Save(_sources, ClipSourceStore.DefaultPath);

        var repointed = ClipRename.Repoint(_engine.Config, oldName, newName);
        if (repointed > 0) _engine.SaveConfig();

        _reporter.Report(repointed > 0
            ? $"renamed {oldName} to {newName} — {repointed} pin(s) updated"
            : $"renamed {oldName} to {newName}");

        return true;
    }

    /// <summary>Carries the cached thumbnail across to the renamed clip's cache key.</summary>
    /// <remarks>
    /// A rename PRESERVES the last-write time, so only the path half of the key changes and the
    /// cached image is still correct for the new name. Without this the tile blanks and LoadAsync
    /// re-runs ffmpeg (~1.2 s) to produce a byte-identical picture.
    ///
    /// BEST EFFORT, AND IT MUST NEVER FAIL THE RENAME. The file has already moved by the time this
    /// runs, so reporting a rename as failed because a cache file could not be moved would be a
    /// lie. The swallow also covers the one place the scanner hazard DOES apply here: this move
    /// overwrites, so it is replace-in-place, and a refusal simply costs a regenerated thumbnail.
    /// </remarks>
    private void MoveCachedThumbnail(string oldClipPath, string newClipPath, DateTime? stamp)
    {
        if (stamp is null) return;

        try
        {
            var from = _thumbnails.CachePathFor(oldClipPath, stamp.Value);
            var to = _thumbnails.CachePathFor(newClipPath, stamp.Value);

            // A CASE-ONLY RENAME (Forest.mp4 -> forest.mp4) produces identical cache keys, because
            // CacheFileName hashes clipPath.ToLowerInvariant(). File.Move is then called with from==to,
            // which is harmless and already swallowed by the catch. This case exists and is covered.
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Task.Run because a shell operation on a OneDrive-backed path is exactly the call that
    /// stalls, and this thread owns windows parented into Explorer's desktop.
    /// </summary>
    private static Task RecycleAsync(string clipPath, CancellationToken ct) =>
        Task.Run(() => VbFileSystem.DeleteFile(
            clipPath, VbUiOption.OnlyErrorDialogs, VbRecycle.SendToRecycleBin), ct);

    /// <summary>Regenerable cache, not user data - so permanent, and a failure is not worth a word.</summary>
    private void DeleteCachedThumbnail(string clipPath, DateTime? stamp)
    {
        if (stamp is null) return;

        try { File.Delete(_thumbnails.CachePathFor(clipPath, stamp.Value)); }
        catch (Exception) { }
    }
}
