using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Whallee.App.Dialogs;
using Whallee.App.Services;
using Whallee.App.ViewModels;
using Whallee.Core.Configuration;
using Whallee.Core.Media;

namespace Whallee.App.Pages;

public partial class LibraryPage : UserControl
{
    private readonly LibraryViewModel _model;
    private readonly UiReporter _reporter;
    private bool _loaded;
    private ClipMenuActions _menuActions = null!;   // assigned in the constructor

    /// <summary>
    /// The genre being shown: null for All, GenreBar.Ungrouped for the untagged ones.
    /// </summary>
    private string? _selectedGenre;

    public LibraryPage(EngineHost engine, UiReporter reporter)
    {
        InitializeComponent();

        _reporter = reporter;
        _model = new LibraryViewModel(engine, reporter);

        TargetSelector.ItemsSource = _model.Targets;
        TargetSelector.SelectedIndex = 0;
        TileHost.ItemsSource = _model.Tiles;

        // SUBSCRIBED HERE, after InitializeComponent, alongside the other TileHost wiring.
        // ContextMenuOpening BUBBLES, so one subscription on the host serves every tile - the tiles
        // themselves do not exist yet and could not be subscribed individually.
        TileHost.ContextMenuOpening += TileHost_ContextMenuOpening;
        _menuActions = new ClipMenuActions(
            Apply: ApplyFromMenu,
            PlayInDefaultPlayer: PlayInDefaultPlayer,
            Rename: RenameClip,
            Edit: EditClip,
            SetGenre: SetGenre,
            NewGenre: NewGenre,
            OpenFileLocation: OpenFileLocation,
            ShowDetails: ShowDetails,
            Delete: DeleteClip);

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Re-reads the attached displays. Called each time the page is shown, because this instance is
    /// reused rather than rebuilt and would otherwise list whatever was attached at startup.
    /// </summary>
    public void RefreshTargets()
    {
        _model.RefreshTargets();

        // Rebuilding the list drops the selection; only actually happens when the displays changed.
        if (TargetSelector.SelectedIndex < 0 && _model.Targets.Count > 0)
            TargetSelector.SelectedIndex = 0;
    }

    /// <summary>
    /// async void, because that is what a Loaded handler must be - which is exactly why the body is
    /// wrapped. An exception escaping an async void handler goes to the Dispatcher unhandled and
    /// terminates the process; thumbnail work launches ffmpeg and touches OneDrive-backed files, so
    /// "it might throw" is not hypothetical. LoadAsync guards each clip individually as well; this
    /// is the outer net.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again every time the page is re-attached to the visual tree. The grid is
        // already populated by then, and reloading would re-run the whole thumbnail pass.
        if (_loaded) return;
        _loaded = true;

        try
        {
            await _model.LoadAsync();
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"library failed to load: {ex.Message}");
        }
    }

    /// <summary>
    /// async void, because a Click handler that awaits has no other shape - which is exactly why
    /// the body is wrapped. The plan's version had a try/finally and NO catch: File.Copy throws for
    /// a locked source, a full disk, or a OneDrive file that will not hydrate, and an exception
    /// escaping an async void handler reaches the Dispatcher unhandled and terminates the process.
    /// Same defect class as the tray callbacks and Tile_Click.
    /// </summary>
    private async void AddFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a video to the library",
            Filter = "Video files|*.mp4;*.mkv;*.webm;*.mov|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        // The copy and the thumbnail pass that follows it are both slow enough to click through.
        AddFromFile.IsEnabled = false;
        try
        {
            var added = await _model.ImportFileAsync(dialog.FileName);
            if (added is null)
            {
                Dialogs.MessageDialog.Show(Window.GetWindow(this),
                    "That file type is not supported.",
                    "Use .mp4, .mkv, .webm or .mov.");
                return;
            }

            await _model.LoadAsync();
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not add that file: {ex.Message}");
        }
        finally
        {
            AddFromFile.IsEnabled = true;
        }
    }

    /// <summary>
    /// async void, because a Click handler that awaits has no other shape - which is exactly why
    /// the ENTIRE body, including locating yt-dlp and running the dialog, is wrapped. Locate scans
    /// the winget package tree with Directory.EnumerateDirectories when the configured path doesn't
    /// resolve, which is real synchronous I/O that can throw (UnauthorizedAccessException,
    /// IOException) on a locked-down or cloud-backed path, and this thread owns windows re-parented
    /// into Explorer's desktop - so that call is Task.Run'd too. ImportAsync itself launches two
    /// child processes and does network and file I/O, so an exception escaping any of this is not
    /// hypothetical, and it can fail two ways: by returning an ImportOutcome with Success == false
    /// and a user-facing Message, or by throwing (ClipTrimmer throws InvalidOperationException, the
    /// library move can throw IOException). Same defect class as AddFromFile_Click and Tile_Click.
    /// </summary>
    private async void AddFromUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // ConfiguredYtDlpPath, not null: passing null skips the configured-path fast path
            // inside Locate and forces a PATH/winget scan even when the configured path still
            // works, which could falsely report yt-dlp missing. Task.Run because that scan is
            // real disk I/O.
            var ytDlp = await Task.Run(() => YtDlpLocator.Locate(_model.ConfiguredYtDlpPath));
            if (ytDlp is null)
            {
                Dialogs.MessageDialog.Show(Window.GetWindow(this),
                    "yt-dlp was not found on this machine.",
                    "It is needed to import from a link. Set its location in Settings.");
                return;
            }

            var dialog = new UrlImportDialog(ytDlp) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            await RunUrlImportAsync(dialog.Result, replacing: null);
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not import that link: {ex.Message}");
            ShowImportResult($"Could not import that link. {ex.Message}", detail: null, isError: true);
        }
    }

    /// <summary>
    /// Downloads a request into the library, reports it, and applies it to every display.
    /// </summary>
    /// <param name="replacing">
    /// The file name this import supersedes, or null for a plain add. Set by "Edit…": the old clip
    /// is recycled only AFTER the replacement has landed, so a failed re-import costs nothing.
    /// </param>
    /// <remarks>
    /// SHARED BY "Add from URL" AND "Edit…" so the two cannot drift. Everything a finished import
    /// must do - record its source, refresh, apply, report - belongs here exactly once.
    /// </remarks>
    private async Task RunUrlImportAsync(ImportRequest request, string? replacing)
    {
        {
            AddFromUrl.IsEnabled = false;
            ShowImportStatus("Starting the download…");
            try
            {
                // Progress<T> raises its callback on the SynchronizationContext captured where it
                // was constructed - the UI thread, here - so touching ImportStatusText from it is
                // correct and needs no Dispatcher hop. Still reported to the log too: the log keeps
                // the whole sequence, the strip shows only the newest line.
                var progress = new Progress<string>(m =>
                {
                    _reporter.Report(m);
                    ImportStatusText.Text = m;
                });

                var outcome = await _model.ImportFromUrlAsync(request, progress);

                if (!outcome.Success)
                {
                    // Success == false always carries a user-facing Message, never null - shown
                    // as-is rather than composing a new one.
                    ShowImportResult(outcome.Message, detail: null, isError: true);
                    return;
                }

                // RECORDED BEFORE ANYTHING ELSE can fail. The library is bare .mp4 files - nothing
                // on disk says where a clip came from - so this is the only moment the link is in
                // hand. It feeds the Source row in Details and prefills "Edit…".
                try
                {
                    var landed = System.IO.Path.GetFileName(outcome.LibraryPath!);

                    // THE GENRE IS CARRIED ACROSS, and must be: Record REPLACES the whole entry, so
                    // a re-import ("Edit…") that lands under the same name would otherwise reset
                    // Genre to its null default and quietly drop the clip into Ungrouped. A genre is
                    // the owner's filing, not source metadata, and survives a re-import.
                    //
                    // The landing name is asked first (the usual case: the replacement overwrote the
                    // clip that was tagged), then the name being replaced, which covers a re-import
                    // that landed under a DIFFERENT name - the tag follows the clip, not the file.
                    var genre = _model.GenreOf(landed)
                                ?? (replacing is null ? null : _model.GenreOf(replacing));

                    _model.RecordSource(
                        landed,
                        new ClipSource(request.Url, request.Start,
                                       request.Length, DateTimeOffset.Now, genre));
                }
                catch (Exception ex)
                {
                    // A bookkeeping record must never fail an import that worked.
                    _reporter.Report($"imported, but could not record its source: {ex.Message}");
                }

                await _model.LoadAsync();
                RefreshGenreView();

                // A THEMED CARD, NOT A MessageBox. The old one was a stock system dialog - light
                // chrome in a dark app, four lines of prose, and a modal interruption for news
                // that is usually just "it worked".
                //
                // The caveat half is still load-bearing and must not be dropped in a tidy-up: the
                // URL dialog deliberately predicts nothing in advance, so this is the ONLY place a
                // user learns the network capped their clip or ignored their start time.
                // ImportSummary decides the wording and is unit-tested for exactly that.
                var summary = ImportSummary.Describe(
                    System.IO.Path.GetFileNameWithoutExtension(outcome.LibraryPath ?? string.Empty),
                    request.Length, request.Start,
                    outcome.ActualLength, outcome.Strategy);

                // APPLIED TO EVERY DISPLAY THE MOMENT IT LANDS, at the owner's request
                // (2026-09-11). Importing a wallpaper and then having to right-click it to see it
                // was a step with no decision in it - the reason you fetched the clip is to look
                // at it. Passing null as the device name is the same path as "Apply to → All
                // displays"; it overwrites existing per-display pins, which is what "change all of
                // the backgrounds" asks for.
                //
                // AFTER the library reload above, so the file is on disk and listed before anything
                // tries to resolve it, and guarded separately: a clip that imported correctly must
                // still be reported as imported even if applying it fails.
                try
                {
                    _model.ApplyClip(outcome.LibraryPath!, null);
                }
                catch (Exception ex)
                {
                    _reporter.Report($"imported, but could not apply it: {ex.Message}");
                }

                // THE OLD CLIP GOES LAST, and only once the replacement is on disk, listed and
                // applied. Recycling it first would mean a failed re-import left the owner with
                // neither clip - and the reason to press "Edit…" is that they want a different cut
                // of a video they already have.
                //
                // Skipped when the replacement landed under the same name: the importer would have
                // had to overwrite it, so there is nothing left to remove and deleting by that name
                // would recycle the NEW clip.
                if (replacing is not null &&
                    !string.Equals(replacing, System.IO.Path.GetFileName(outcome.LibraryPath),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var old = System.IO.Path.Combine(_model.Config.LibraryFolder, replacing);
                        if (await _model.DeleteClipAsync(old))
                        {
                            await _model.LoadAsync();
                            RefreshGenreView();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Both clips now exist, which is untidy but not harmful - and the new one
                        // is already applied. Say so rather than failing an import that worked.
                        _reporter.Report($"imported, but could not remove {replacing}: {ex.Message}");
                    }
                }

                ShowImportResult(summary.Headline, summary.Detail, isError: false);
            }
            finally
            {
                // The BAR stops in the finally, not the whole card: every path above leaves a
                // result worth reading, but an import that failed, was cancelled or threw must not
                // leave a bar spinning over a library that is importing nothing.
                ImportProgress.Visibility = Visibility.Collapsed;
                AddFromUrl.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// "Edit…": re-import the clip's original link, so a different section can be chosen.
    /// </summary>
    /// <remarks>
    /// async void, wrapped end to end - the shape every Click handler that awaits must take.
    ///
    /// Only offered for clips Whallee imported and recorded. The menu disables it otherwise rather
    /// than hiding it, so the owner can see the option exists and why it does not apply here.
    /// </remarks>
    private async void EditClip(ClipTile tile)
    {
        try
        {
            var source = _model.SourceOf(tile.FileName);
            if (source is null) return;   // the menu item is disabled in this case

            var ytDlp = await Task.Run(() => YtDlpLocator.Locate(_model.ConfiguredYtDlpPath));
            if (ytDlp is null)
            {
                Dialogs.MessageDialog.Show(Window.GetWindow(this),
                    "yt-dlp was not found on this machine.",
                    "It is needed to re-import from a link. Set its location in Settings.");
                return;
            }

            var dialog = new UrlImportDialog(ytDlp, source.Url) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            await RunUrlImportAsync(dialog.Result, replacing: tile.FileName);
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not re-import {tile.FileName}: {ex.Message}");
            ShowImportResult($"Could not re-import that clip. {ex.Message}", detail: null, isError: true);
        }
    }

    /// <summary>Puts the card into its working state: live message, bar running, no Dismiss.</summary>
    private void ShowImportStatus(string message)
    {
        ImportStatusText.Text = message;
        ImportStatusText.Foreground = Brush("TextPrimaryBrush", SystemColors.ControlTextBrush);
        ImportStatusDetail.Visibility = Visibility.Collapsed;
        ImportProgress.Visibility = Visibility.Visible;
        ImportStatusDismiss.Visibility = Visibility.Collapsed;
        ImportStatus.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Puts the card into its finished state and LEAVES IT THERE until dismissed.
    /// </summary>
    /// <remarks>
    /// Not auto-hidden after a delay. The detail line can be the only notice a user ever gets that
    /// their start time was ignored, and a message that removes itself on a timer is one the user
    /// can miss entirely by looking away - which is the failure the modal it replaced could not
    /// have.
    /// </remarks>
    private void ShowImportResult(string headline, string? detail, bool isError)
    {
        ImportStatusText.Text = headline;
        ImportStatusText.Foreground = isError
            ? Brush("DangerBrush", SystemColors.ControlTextBrush)
            : Brush("TextPrimaryBrush", SystemColors.ControlTextBrush);

        ImportStatusDetail.Text = detail ?? string.Empty;
        ImportStatusDetail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;

        ImportProgress.Visibility = Visibility.Collapsed;
        ImportStatusDismiss.Visibility = Visibility.Visible;
        ImportStatus.Visibility = Visibility.Visible;
    }

    private void ImportStatusDismiss_Click(object sender, RoutedEventArgs e) =>
        ImportStatus.Visibility = Visibility.Collapsed;

    /// <summary>A theme brush by key, falling back rather than throwing while building a row.</summary>
    private System.Windows.Media.Brush Brush(string key, System.Windows.Media.Brush fallback) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    /// <summary>
    /// Fills the tile's (empty) ContextMenu just before it opens.
    /// </summary>
    /// <remarks>
    /// SYNCHRONOUS, AND MUST STAY THAT WAY. Setting e.Handled to suppress the menu is only
    /// meaningful before this returns, so this handler can never become async void.
    /// </remarks>
    private void TileHost_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            var button = FindTileButton(e.OriginalSource);

            // Right-clicking the empty area around the tiles: show nothing rather than an empty
            // menu, which would look like a bug.
            if (button?.DataContext is not ClipTile tile || button.ContextMenu is null)
            {
                e.Handled = true;
                return;
            }

            ClipContextMenu.Populate(
                button.ContextMenu, tile, _menuActions, _model.SourceOf(tile.FileName) is not null,
                _model.Genres, _model.GenreOf(tile.FileName));
        }
        catch (Exception ex)
        {
            e.Handled = true;
            _reporter.Report($"could not open that menu: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks up from whatever was under the cursor - the Image or the TextBlock, not the Button -
    /// to the tile's Button.
    /// </summary>
    /// <remarks>
    /// e.OriginalSource, not e.Source: ContextMenuService raises this on the element that owns the
    /// menu, but a routed event's Source can be retargeted on its way up to TileHost, and
    /// OriginalSource is the one that is not.
    /// </remarks>
    private static Button? FindTileButton(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is Button { DataContext: ClipTile } button) return button;

            // Guarded: VisualTreeHelper.GetParent throws on anything that is not a Visual.
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }

        return null;
    }

    /// <summary>
    /// The menu's "Apply to" items. Synchronous for the same reason Tile_Click is: ApplyClip runs
    /// a full engine restart (~3 s) inline on this thread, which is correct here because Restart
    /// pumps throughout, so the desktop stays responsive.
    /// </summary>
    private void ApplyFromMenu(ClipTile tile, string? deviceName)
    {
        try
        {
            _model.ApplyClip(tile.Path, deviceName);
        }
        catch (Exception ex)
        {
            // A menu Click is a Dispatcher callback: an escaping exception terminates the app.
            _reporter.Report($"could not apply that clip: {ex.Message}");
        }
    }

    /// <summary>
    /// async void, and wrapped end to end for it. Process.Start with UseShellExecute resolves the
    /// file association through the shell, which on a OneDrive-backed path is real I/O - hence the
    /// Task.Run - and throws Win32Exception when there is no association at all. That is a normal
    /// outcome on a machine with no video player installed, not a crash, so it gets its own
    /// message rather than a raw error string.
    /// </summary>
    private async void PlayInDefaultPlayer(ClipTile tile)
    {
        try
        {
            await Task.Run(() =>
            {
                using var process = Process.Start(
                    new ProcessStartInfo(tile.Path) { UseShellExecute = true });
            });
        }
        catch (Win32Exception)
        {
            Dialogs.MessageDialog.Show(Window.GetWindow(this),
                $"Windows has no app associated with {Path.GetExtension(tile.Path)} files.");
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not play {tile.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// async void, wrapped end to end.
    /// </summary>
    /// <remarks>
    /// Arguments, NOT ArgumentList, and this is the one place in the codebase where that is
    /// correct. explorer.exe wants "/select,&lt;path&gt;" as a SINGLE token with the path quoted
    /// inside it; ArgumentList would escape it as a separate argument and explorer would silently
    /// open the Documents folder instead of highlighting the file. That is the classic failure of
    /// this call.
    /// </remarks>
    private async void OpenFileLocation(ClipTile tile)
    {
        try
        {
            await Task.Run(() =>
            {
                using var process = Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{tile.Path}\"",
                    UseShellExecute = true
                });
            });
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not open the folder for {tile.FileName}: {ex.Message}");
        }
    }

    /// <summary>Rebuilds the bar and re-applies the filter and grouping.</summary>
    private void RefreshGenreView()
    {
        var hasUngrouped = _model.Tiles.Any(t => t.Genre is null);

        // A genre the owner just deleted or renamed must not stay selected, or the page would show
        // an empty library with no way back other than clicking All.
        //
        // THE Ungrouped SENTINEL IS SUBJECT TO THE SAME RESET, via hasUngrouped. Its tab is only
        // drawn while something is actually untagged, so tagging the last untagged clip while
        // standing on it removes the tab underneath the owner - leaving an empty grid with no tab
        // highlighted, recoverable only by finding All.
        if (_selectedGenre is not null &&
            (_selectedGenre == GenreBar.Ungrouped
                ? !hasUngrouped
                : !_model.Genres.Any(g => Whallee.Core.Configuration.ClipGenres.Same(g, _selectedGenre))))
            _selectedGenre = null;

        GenreBar.Populate(
            GenreBarHost, _model.Genres, _selectedGenre,
            hasUngrouped: hasUngrouped,
            new GenreBarActions(SelectGenre, RenameGenre, DeleteGenre, CreateGenre, MoveGenreTo));

        ApplyGenreView();
    }

    /// <summary>
    /// Points the tile host at a filtered, optionally grouped view of the clips.
    /// </summary>
    /// <remarks>
    /// A CollectionViewSource rather than a second collection: TileHost is an ItemsControl, which
    /// supports both filtering and GroupStyle, so the dividers come from WPF instead of being laid
    /// out by hand - and a hand-built divider would not survive a re-sort.
    ///
    /// GROUPED ONLY IN "All". Inside one genre every heading would say the same thing.
    ///
    /// THE GROUPS ARE SORTED, AND HAVE TO BE. A PropertyGroupDescription alone orders the headings
    /// by whichever clip it meets first - i.e. by library file order - so "Move left"/"Move right"
    /// would reorder the bar and change nothing in the one view that shows every genre, and
    /// Ungrouped could land in the middle. CustomSort puts the tiles in the owner's genre order
    /// (untagged last, per the spec, so untagged clips never push the filed ones off the top) and
    /// the groups follow, because WPF groups in the view's order.
    ///
    /// SORTS THE VIEW, NEVER _model.Tiles. That collection is the library's own order and other
    /// code reads it.
    /// </remarks>
    private void ApplyGenreView()
    {
        var view = new System.Windows.Data.CollectionViewSource { Source = _model.Tiles };

        if (_selectedGenre is null)
        {
            view.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription(nameof(ClipTile.Genre)));

            if (view.View is System.Windows.Data.ListCollectionView sortable)
                sortable.CustomSort = new GenreOrderComparer(_model.Genres, _model.Tiles);
        }
        else
        {
            var wanted = _selectedGenre;
            view.View.Filter = item => item is ClipTile tile && (wanted == GenreBar.Ungrouped
                ? tile.Genre is null
                : Whallee.Core.Configuration.ClipGenres.Same(tile.Genre, wanted));
        }

        TileHost.ItemsSource = view.View;

        // THE PANEL MUST FOLLOW THE GROUPING. With grouping on, ItemsPanel arranges the GROUPS, so
        // leaving it a WrapPanel lays the genres out side by side and stacks the tiles vertically
        // inside each one.
        TileHost.GroupStyle.Clear();

        if (_selectedGenre is null)
        {
            TileHost.ItemsPanel = (ItemsPanelTemplate)FindResource("GroupedTilePanel");
            TileHost.GroupStyle.Add((GroupStyle)FindResource("GenreGroupStyle"));
        }
        else
        {
            TileHost.ItemsPanel = (ItemsPanelTemplate)FindResource("FlatTilePanel");
        }
    }

    /// <summary>
    /// Wrapped like every sibling handler here: this is reached from a tab's Click, which is a
    /// Dispatcher callback, and the rebuild it triggers resolves theme resources - FindResource
    /// throws on a missing key. An escaping exception would terminate the process.
    /// </summary>
    private void SelectGenre(string? genre)
    {
        try
        {
            _selectedGenre = genre;
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not show that genre: {ex.Message}");
        }
    }

    private void RenameGenre(string genre)
    {
        try
        {
            var name = Dialogs.TextPromptDialog.Ask(
                Window.GetWindow(this), "Rename genre", "Genre name", genre,
                value => Whallee.Core.Configuration.ClipGenres.Validate(
                    value, _model.Genres, currentName: genre));

            if (name is null) return;

            if (_model.RenameGenre(genre, name))
            {
                // Follow the rename rather than falling back to All: the owner was looking at this
                // genre and still is.
                if (Whallee.Core.Configuration.ClipGenres.Same(_selectedGenre, genre))
                    _selectedGenre = name;

                RetagLoadedTiles(genre, name);
                RefreshGenreView();
            }
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not rename {genre}: {ex.Message}");
        }
    }

    private void DeleteGenre(string genre)
    {
        try
        {
            // SAYS THE CLIPS SURVIVE. This is the one action in the bar that touches many clips at
            // once, and "Delete" beside a library of videos reads as destructive unless told
            // otherwise.
            if (!Dialogs.MessageDialog.Confirm(
                    Window.GetWindow(this), $"Delete the genre {genre}?",
                    "Its clips stay in your library — they just stop being filed under it.",
                    confirmText: "Delete genre"))
                return;

            if (_model.DeleteGenre(genre))
            {
                RetagLoadedTiles(genre, null);
                RefreshGenreView();
            }
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not delete {genre}: {ex.Message}");
        }
    }

    /// <summary>Drops a dragged genre tab at a new position in the bar.</summary>
    /// <remarks>
    /// REPLACED "Move left" / "Move right" on 2026-09-14 at the owner's request - they asked for
    /// Chrome's tab dragging. GenreBar computes the index; this only applies and redraws.
    /// </remarks>
    private void MoveGenreTo(string genre, int index)
    {
        try
        {
            _model.MoveGenreTo(genre, index);
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not move {genre}: {ex.Message}");
        }
    }

    /// <summary>
    /// "+ New genre" in the bar: creates a genre with no clips in it.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM THE CLIP MENU'S "New genre…", which creates one AND tags the clip it was
    /// opened from. This one exists because setting up categories should not require finding a clip
    /// to hang the first one on — which is why an empty genre has to survive being stored.
    /// </remarks>
    private void CreateGenre()
    {
        try
        {
            var name = Dialogs.TextPromptDialog.Ask(
                Window.GetWindow(this), "New genre", "Genre name", null,
                value => Whallee.Core.Configuration.ClipGenres.Validate(value, _model.Genres));

            if (name is null) return;

            if (_model.AddGenre(name)) RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not create that genre: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the tiles already on screen after a genre is renamed or deleted.
    /// </summary>
    /// <remarks>
    /// The index is the record; these are the copies the grouping reads. Re-running LoadAsync would
    /// be correct too and costs a full thumbnail pass, which is seconds for a change that moved no
    /// files.
    /// </remarks>
    private void RetagLoadedTiles(string from, string? to)
    {
        foreach (var tile in _model.Tiles)
            if (Whallee.Core.Configuration.ClipGenres.Same(tile.Genre, from))
                tile.Genre = to;
    }

    /// <summary>Files a clip under a genre, or under none when <paramref name="genre"/> is null.</summary>
    private void SetGenre(ClipTile tile, string? genre)
    {
        try
        {
            _model.AssignGenre(tile.FileName, genre);
            tile.Genre = genre;
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not set the genre for {tile.FileName}: {ex.Message}");
        }
    }

    /// <summary>Creates a genre and files this clip under it in one step.</summary>
    private void NewGenre(ClipTile tile)
    {
        try
        {
            var name = Dialogs.TextPromptDialog.Ask(
                Window.GetWindow(this), "New genre", "Genre name", null,
                value => Whallee.Core.Configuration.ClipGenres.Validate(value, _model.Genres));

            if (name is null) return;

            _model.AssignGenre(tile.FileName, name);
            tile.Genre = name;
            RefreshGenreView();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not create that genre: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronous: the dialog does its own probing off-thread and shows instantly, so there is
    /// nothing to await here. Still wrapped - a menu Click is a Dispatcher callback.
    /// </summary>
    private void ShowDetails(ClipTile tile)
    {
        try
        {
            new ClipDetailsDialog(
                tile.Path, _model.ConfiguredFfmpegPath, _model.SourceOf(tile.FileName),
                // Saved straight away rather than on Close: the dialog has no OK button, so there
                // is no other moment to save on. Enables "Edit…" for this clip from here on.
                source => _model.RecordSource(tile.FileName, source))
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not show details for {tile.FileName}: {ex.Message}");
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string clipPath }) return;

        var index = TargetSelector.SelectedIndex;
        if (index < 0 || index >= _model.TargetDeviceNames.Count) return;

        try
        {
            // Runs a full engine restart (~3 s) inline on the UI thread. That is correct here and
            // not an oversight: the host windows belong to this thread, and Restart pumps
            // throughout, so the desktop stays responsive. It CAN re-enter XAML, so nothing after
            // this call may assume the UI is untouched.
            _model.ApplyClip(clipPath, _model.TargetDeviceNames[index]);
        }
        catch (Exception ex)
        {
            // A click handler is a Dispatcher callback: an escaping exception terminates the app.
            _reporter.Report($"could not apply that clip: {ex.Message}");
        }
    }

    /// <summary>
    /// async void, wrapped end to end. A menu Click is a Dispatcher callback, and an exception
    /// escaping an async void handler terminates the process.
    /// </summary>
    private async void RenameClip(ClipTile tile)
    {
        try
        {
            var dialog = new RenameClipDialog(tile.FileName) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.NewBaseName is null) return;

            // Only refresh when something actually changed: a refused rename leaves a grid that is
            // still correct, and LoadAsync re-runs the whole thumbnail pass.
            if (await _model.RenameClipAsync(tile.Path, dialog.NewBaseName))
            {
                await _model.LoadAsync();
                RefreshGenreView();
            }
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not rename {tile.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// async void, wrapped end to end. The only destructive action in the app.
    /// </summary>
    /// <remarks>
    /// DEFAULTS TO No. MessageBoxResult.No as the fourth argument is what makes Enter cancel rather
    /// than confirm; without it a stray keypress deletes a clip.
    /// </remarks>
    private async void DeleteClip(ClipTile tile)
    {
        try
        {
            var pinned = ClipRemoval.PinnedDisplayNames(_model.Config, tile.FileName);

            var question = $"Delete {tile.FileName} from the library?";

            // The pin warning moves into the detail line rather than being glued to the question
            // with blank lines - MessageDialog has a second, quieter line for exactly this, and a
            // themed dialog does not need \n\n to get breathing room.
            var detail = "It will be moved to the Recycle Bin.";
            if (pinned.Count > 0)
                detail = $"It is pinned to {string.Join(", ", pinned)}, which will go back to "
                       + "shuffling. " + detail;

            // Cancel is the default inside MessageDialog.Confirm, preserving what the old
            // MessageBoxResult.No fourth argument bought: Enter cancels rather than deletes. See
            // MessageDialog's remarks - this is the only destructive action in the app.
            if (!Dialogs.MessageDialog.Confirm(
                    Window.GetWindow(this), question, detail, confirmText: "Delete"))
                return;

            // Only refresh when something actually changed: a refused delete leaves a grid that is
            // still correct, and LoadAsync re-runs the whole thumbnail pass.
            if (await _model.DeleteClipAsync(tile.Path))
            {
                await _model.LoadAsync();
                RefreshGenreView();
            }
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not delete {tile.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Orders tiles by their genre's position in the owner's bar, with the untagged ones last.
    /// </summary>
    /// <remarks>
    /// EXISTS SO THE HEADINGS UNDER "All" FOLLOW "Move left"/"Move right". WPF builds the groups in
    /// the view's order, so ordering the ITEMS by genre rank is what orders the HEADINGS - there is
    /// no "sort the groups" knob to reach for instead.
    ///
    /// UNGROUPED SORTS LAST, not first and not alphabetically among the genres: the spec puts
    /// untagged clips at the bottom so they cannot push the filed ones off the top of the page.
    ///
    /// TIES BREAK ON LIBRARY ORDER, by the tile's index in the source collection. Within one genre
    /// the clips must keep the order the library lists them in, and ListCollectionView's sort is not
    /// guaranteed stable - so the original position is part of the comparison rather than something
    /// left to chance.
    /// </remarks>
    private sealed class GenreOrderComparer : System.Collections.IComparer
    {
        private readonly IReadOnlyList<string> _genres;
        private readonly IList<ClipTile> _order;

        public GenreOrderComparer(IReadOnlyList<string> genres, IList<ClipTile> order)
        {
            _genres = genres;
            _order = order;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not ClipTile left || y is not ClipTile right) return 0;

            var byGenre = Rank(left.Genre).CompareTo(Rank(right.Genre));
            return byGenre != 0 ? byGenre : _order.IndexOf(left).CompareTo(_order.IndexOf(right));
        }

        /// <summary>
        /// The genre's place in the bar; int.MaxValue for ungrouped, and for a genre the list has
        /// somehow lost - an unknown tag is untagged as far as the bar is concerned.
        /// </summary>
        private int Rank(string? genre)
        {
            if (genre is null) return int.MaxValue;

            for (var i = 0; i < _genres.Count; i++)
                if (Whallee.Core.Configuration.ClipGenres.Same(_genres[i], genre)) return i;

            return int.MaxValue;
        }
    }
}
