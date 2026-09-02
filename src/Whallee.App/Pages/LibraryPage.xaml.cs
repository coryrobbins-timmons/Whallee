using System.Windows;
using System.Windows.Controls;
using Whallee.App.Dialogs;
using Whallee.App.Services;
using Whallee.App.ViewModels;
using Whallee.Core.Media;

namespace Whallee.App.Pages;

public partial class LibraryPage : UserControl
{
    private readonly LibraryViewModel _model;
    private readonly UiReporter _reporter;
    private bool _loaded;

    public LibraryPage(EngineHost engine, UiReporter reporter)
    {
        InitializeComponent();

        _reporter = reporter;
        _model = new LibraryViewModel(engine, reporter);

        TargetSelector.ItemsSource = _model.Targets;
        TargetSelector.SelectedIndex = 0;
        TileHost.ItemsSource = _model.Tiles;

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
                MessageBox.Show("That file type is not supported. Use .mp4, .mkv, .webm or .mov.",
                    "Whallee", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _model.LoadAsync();
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
                MessageBox.Show("yt-dlp was not found on this machine.", "Whallee",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new UrlImportDialog(ytDlp) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            AddFromUrl.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(m => _reporter.Report(m));
                var outcome = await _model.ImportFromUrlAsync(dialog.Result, progress);

                if (!outcome.Success)
                {
                    // Success == false always carries a user-facing Message, never null - show it
                    // as-is rather than composing a new one.
                    MessageBox.Show(outcome.Message, "Whallee",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await _model.LoadAsync();

                // A short clip is the expected result on this network, not a failure - the message
                // already explains that plainly, so surface it rather than letting the user
                // discover the shortfall by watching a shorter-than-requested loop.
                if (outcome.ActualLength < dialog.Result.Length)
                    MessageBox.Show(outcome.Message, "Whallee",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                AddFromUrl.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not import that link: {ex.Message}");
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
}
