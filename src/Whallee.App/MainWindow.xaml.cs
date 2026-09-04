using System.Windows;

namespace Whallee.App;

public partial class MainWindow : Window
{
    private Pages.LibraryPage? _libraryPage;
    private Pages.SettingsPage? _settingsPage;

    public MainWindow()
    {
        InitializeComponent();

        // No HWND exists yet at this point; Apply defers itself to SourceInitialized. Called here
        // rather than from a XAML SourceInitialized attribute to match this codebase's convention
        // of subscribing in code after InitializeComponent, never in markup.
        Interop.DarkTitleBar.Apply(this);

        // SET BEFORE THE HANDLER IS SUBSCRIBED, deliberately. Checking a RadioButton raises
        // Checked, and doing that from markup would fire it while InitializeComponent was still
        // walking the tree - the exact defect ShowPage's null guard was written for. Setting it
        // here, before there is a handler, means nothing fires at all and the initial page is
        // drawn once, explicitly, below.
        LibraryNav.IsChecked = true;

        LibraryNav.Checked += (_, _) => ShowPage(0);
        SettingsNav.Checked += (_, _) => ShowPage(1);
        LogToggle.Checked += (_, _) => SetLogExpanded(true);
        LogToggle.Unchecked += (_, _) => SetLogExpanded(false);

        ShowPage(0);

        // Bound after InitializeComponent, so the engine's start-up messages - queued while
        // App.OnStartup ran Engine.Start() before this window existed - appear as soon as the
        // collection is attached.
        var messages = ((App)Application.Current).Reporter.Messages;
        LogView.ItemsSource = messages;
        HookLatestMessage(messages);
    }

    /// <summary>
    /// Keeps the collapsed strip showing the newest message.
    /// </summary>
    /// <remarks>
    /// Tracked rather than bound because WPF has no "last item" binding. CollectionChanged arrives
    /// on the UI thread here, because UiReporter only ever mutates the collection from inside a
    /// Dispatcher callback - see its remarks for why that is not merely incidental.
    /// </remarks>
    private void HookLatestMessage(System.Collections.ObjectModel.ObservableCollection<string> messages)
    {
        messages.CollectionChanged += (_, _) =>
        {
            LatestMessage.Text = messages.Count > 0 ? messages[^1] : string.Empty;

            // Only while the pane is actually open. An engine restart emits a burst of messages,
            // and scrolling a collapsed ScrollViewer for every one of them is pure waste.
            if (LogExpander.Visibility == Visibility.Visible) LogExpander.ScrollToEnd();
        };

        if (messages.Count > 0) LatestMessage.Text = messages[^1];
    }

    private void SetLogExpanded(bool expanded)
    {
        LogExpander.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) LogExpander.ScrollToEnd();
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

    // Both pages are built once and reused, so each gets a Refresh() on show.
    //
    // PageHost is still null-checked, though the defect that forced it is gone. It was written for
    // a nav ListBox carrying SelectedIndex="0", which raised SelectionChanged from
    // ItemsControl.EndInit - before the ContentControl declared after it existed. The rail no
    // longer does that: nothing is subscribed until the constructor has run. The guard stays
    // because it keeps this method independent of element order and of when a caller wires
    // itself up, and removing it would trade nothing for a new way to crash.
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

        var host = (App)Application.Current;

        // BUILT ONCE AND REUSED, for the same reason LibraryPage is: constructing it enumerates
        // displays and reads the logon shortcut off disk, and a new instance on every page switch
        // would also throw away any pending, unapplied edits.
        _settingsPage ??= new Pages.SettingsPage(host.Engine!, host.Reporter);

        // Cheap, and it keeps the rows honest: displays change under this app while it runs, and
        // the shortcut can be deleted by hand from outside it.
        _settingsPage.Refresh();

        PageHost.Content = _settingsPage;
    }
}
