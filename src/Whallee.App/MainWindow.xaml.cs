using System.Windows;
using System.Windows.Controls;

namespace Whallee.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowPage(0);
    }

    private void PageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPage(PageSelector.SelectedIndex);

    // Pages are added in Tasks 6 and 9; until then the host shows a placeholder.
    private void ShowPage(int index) =>
        PageHost.Content = new TextBlock { Text = index == 0 ? "Library" : "Settings" };
}
