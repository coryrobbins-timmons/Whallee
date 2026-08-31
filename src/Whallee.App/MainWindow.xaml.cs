using System.Windows;
using System.Windows.Controls;

namespace Whallee.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowPage(0);

        // Bound after InitializeComponent, so the engine's start-up messages - queued while
        // App.OnStartup ran Engine.Start() before this window existed - appear as soon as the
        // collection is attached.
        LogView.ItemsSource = ((App)Application.Current).Reporter.Messages;
    }

    private void PageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPage(PageSelector.SelectedIndex);

    // Pages are added in Tasks 6 and 9; until then the host shows a placeholder.
    //
    // PageHost is null-checked because SelectionChanged fires DURING InitializeComponent: the
    // ListBox carries SelectedIndex="0" and raises the event from ItemsControl.EndInit, which
    // runs before the ContentControl declared after it has been created and assigned. Guarding
    // here rather than reordering the XAML keeps the fix independent of element order — the
    // constructor's own ShowPage(0) draws the initial page once the tree is fully built.
    private void ShowPage(int index)
    {
        if (PageHost is null) return;
        PageHost.Content = new TextBlock { Text = index == 0 ? "Library" : "Settings" };
    }
}
