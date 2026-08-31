using System.Windows;
using System.Windows.Controls;

namespace Whallee.App;

public partial class MainWindow : Window
{
    private Pages.LibraryPage? _libraryPage;

    public MainWindow()
    {
        InitializeComponent();
        ShowPage(0);

        // Bound after InitializeComponent, so the engine's start-up messages - queued while
        // App.OnStartup ran Engine.Start() before this window existed - appear as soon as the
        // collection is attached.
        LogView.ItemsSource = ((App)Application.Current).Reporter.Messages;
    }

    /// <summary>
    /// Closing hides to tray; the tray's Exit is the only way out. Wallpapers are meant to keep
    /// playing with no window on screen - that is the entire point of the app.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A real shutdown must not be cancelled, or the app could never be quit at all: with
        // ShutdownMode=OnExplicitShutdown nothing else would ever close this window.
        if (Application.Current is App { IsShuttingDown: true })
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void PageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPage(PageSelector.SelectedIndex);

    // The settings page arrives in Task 9; until then index 1 shows a placeholder.
    //
    // PageHost is null-checked because SelectionChanged fires DURING InitializeComponent: the
    // ListBox carries SelectedIndex="0" and raises the event from ItemsControl.EndInit, which
    // runs before the ContentControl declared after it has been created and assigned. Guarding
    // here rather than reordering the XAML keeps the fix independent of element order — the
    // constructor's own ShowPage(0) draws the initial page once the tree is fully built.
    private void ShowPage(int index)
    {
        if (PageHost is null) return;

        if (index == 0)
        {
            // BUILT ONCE AND REUSED. The plan creates a new LibraryPage on every switch, which
            // would re-enumerate displays and re-run the entire thumbnail pass each time the user
            // clicked between the two pages - and would abandon an in-flight pass part-way,
            // leaving its ffmpeg processes running for a page nobody can see any more.
            var app = (App)Application.Current;
            _libraryPage ??= new Pages.LibraryPage(app.Engine!, app.Reporter);

            // Cheap (two user32 calls) and it keeps the target dropdown honest: because the page
            // is reused, its display list would otherwise be frozen at whatever was attached when
            // the window first opened.
            _libraryPage.RefreshTargets();

            PageHost.Content = _libraryPage;
            return;
        }

        PageHost.Content = new TextBlock { Text = "Settings" };
    }
}
