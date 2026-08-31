using System.Collections.ObjectModel;
using System.IO;
using Whallee.App.Services;
using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Displays;
using Whallee.Core.Media;

namespace Whallee.App.ViewModels;

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
    /// An existing name is disambiguated, never overwritten: PinnedPath refers to a clip BY NAME,
    /// so overwriting would silently change what a monitor is playing.
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
            // invites, and the disambiguation loop below would turn it into a silent duplicate -
            // two tiles, two names, one clip. Not the plan's behaviour; added because a duplicate
            // is indistinguishable from a bug once it is on screen.
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
            var counter = 1;
            while (File.Exists(target))
            {
                var name = Path.GetFileNameWithoutExtension(sourcePath)
                           + $" ({counter++})" + extension;
                target = Path.Combine(libraryFolder, name);
            }

            File.Copy(sourcePath, target);
            _reporter.Report($"added {Path.GetFileName(target)} to the library");
            return target;
        }, ct).ConfigureAwait(true);   // back to the UI thread; the caller refreshes the grid
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
            Tiles.Add(new ClipTile { Path = file });

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
}
