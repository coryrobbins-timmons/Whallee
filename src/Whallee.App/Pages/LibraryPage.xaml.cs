using System.Windows;
using System.Windows.Controls;
using Whallee.App.Services;
using Whallee.App.ViewModels;

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
