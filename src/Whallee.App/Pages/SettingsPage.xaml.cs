using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Whallee.App.Services;
using Whallee.App.ViewModels;
using Whallee.Core.Configuration;

namespace Whallee.App.Pages;

/// <summary>
/// Settings: un-pin a display, choose scaling, start with Windows, and see where things live.
/// </summary>
/// <remarks>
/// ROWS AND THE ABOUT TABLE ARE BUILT IN C#, not with DataTemplates and bindings. Two reasons, and
/// neither is taste: the mode radio buttons need a GroupName per row and an IsEnabled that depends
/// on whether anything is playing, which in markup would mean converters this codebase has none
/// of; and every other interactive surface here is a code-behind handler, so bindings would be the
/// odd one out. Same approach as ClipContextMenu.
///
/// EVERY HANDLER IS SUBSCRIBED HERE, AFTER InitializeComponent, and none in the markup. MainWindow
/// already documents being bitten by a markup-wired SelectionChanged firing from
/// ItemsControl.EndInit before later-declared elements existed; ScalingBox is exactly that shape.
/// </remarks>
public partial class SettingsPage : UserControl
{
    private static readonly (string Label, ScalingMode Mode)[] ScalingChoices =
    {
        ("Fill", ScalingMode.Fill),
        ("Fit", ScalingMode.Fit),
        ("Fill & crop", ScalingMode.FillCrop)
    };

    private readonly SettingsViewModel _model;
    private readonly UiReporter _reporter;

    /// <summary>
    /// Suppresses handlers while controls are being populated. Setting SelectedIndex or IsChecked
    /// in code raises the same events a user click does, so without this every Refresh would look
    /// like an edit and mark the page dirty.
    /// </summary>
    private bool _loading;

    public SettingsPage(EngineHost engine, UiReporter reporter)
    {
        InitializeComponent();

        _reporter = reporter;
        _model = new SettingsViewModel(engine, reporter);

        ScalingBox.ItemsSource = ScalingChoices.Select(c => c.Label).ToList();

        ScalingBox.SelectionChanged += ScalingBox_SelectionChanged;
        StartWithWindowsBox.Checked += StartWithWindows_Toggled;
        StartWithWindowsBox.Unchecked += StartWithWindows_Toggled;
        ApplyButton.Click += Apply_Click;

        Refresh();
    }

    /// <summary>
    /// Re-reads everything. Called each time the page is shown, because this instance is reused
    /// rather than rebuilt - mirroring LibraryPage.RefreshTargets.
    /// </summary>
    public void Refresh()
    {
        _model.Refresh();

        _loading = true;
        try
        {
            StartWithWindowsBox.IsEnabled = SettingsViewModel.CanStartWithWindows;
            StartWithWindowsBox.IsChecked = SettingsViewModel.StartsWithWindows;

            if (SettingsViewModel.CanStartWithWindows)
            {
                StartWithWindowsNote.Visibility = Visibility.Collapsed;
            }
            else
            {
                // A disabled control with a stated reason beats a toggle that half-works. Enabling
                // it here would write a shortcut pointing at nothing.
                StartWithWindowsNote.Text =
                    "Whallee has not been installed yet. Run tools\\publish-app.ps1 to install it, "
                    + "then this can be turned on.";
                StartWithWindowsNote.Visibility = Visibility.Visible;
            }

            ScalingBox.SelectedIndex = Array.FindIndex(ScalingChoices, c => c.Mode == _model.Scaling);

            BuildDisplayRows();
            BuildAboutTable();
        }
        finally
        {
            _loading = false;
        }

        RefreshApplyButton();
    }

    private void RefreshApplyButton() => ApplyButton.IsEnabled = _model.IsDirty;

    private void BuildDisplayRows()
    {
        DisplayHost.Children.Clear();

        foreach (var row in _model.Displays)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = row.DeviceName,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var playing = new TextBlock
            {
                Text = row.NowPlaying ?? "(not playing)",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(playing, 1);
            grid.Children.Add(playing);

            // GROUPNAME IS PER ROW, AND THAT IS LOAD-BEARING. RadioButtons with no GroupName group
            // by their parent, and these live in one StackPanel - so without a distinct name per
            // display, choosing Pinned on one monitor would silently clear it on every other.
            var group = "mode-" + row.DeviceName;

            var shuffle = new RadioButton
            {
                Content = "Shuffle",
                GroupName = group,
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = row.Mode == DisplayMode.Shuffle
            };

            var pinned = new RadioButton
            {
                Content = "Pinned",
                GroupName = group,
                IsChecked = row.Mode == DisplayMode.Pinned,
                IsEnabled = row.CanPin,
                ToolTip = row.CanPin
                    ? null
                    : "Nothing is playing on this display, so there is no clip to pin to."
            };

            // Captured per iteration so each handler edits its own row.
            var captured = row;
            shuffle.Checked += (_, _) => ChooseMode(captured, DisplayMode.Shuffle);
            pinned.Checked += (_, _) => ChooseMode(captured, DisplayMode.Pinned);

            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            modes.Children.Add(shuffle);
            modes.Children.Add(pinned);
            Grid.SetColumn(modes, 2);
            grid.Children.Add(modes);

            DisplayHost.Children.Add(grid);
        }
    }

    private void ChooseMode(DisplayRow row, DisplayMode mode)
    {
        if (_loading) return;

        row.Mode = mode;
        RefreshApplyButton();
    }

    private void BuildAboutTable()
    {
        AboutHost.Children.Clear();
        AboutHost.RowDefinitions.Clear();
        AboutHost.ColumnDefinitions.Clear();
        AboutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        AboutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var entries = new (string Label, string Value)[]
        {
            ("Library", _model.LibraryFolder),
            ("mpv", _model.MpvPath),
            ("ffmpeg", _model.FfmpegPath),
            ("yt-dlp", _model.YtDlpPath),
            ("Config", SettingsViewModel.ConfigPath),
            ("Log", _model.LogPath)
        };

        for (var i = 0; i < entries.Length; i++)
        {
            AboutHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = entries[i].Label, Margin = new Thickness(0, 2, 12, 2) };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            AboutHost.Children.Add(label);

            // Selectable so a path can be copied out, but not editable - changing any of these is
            // out of scope, and the library folder in particular invalidates every pin and
            // thumbnail.
            var value = new TextBox
            {
                Text = entries[i].Value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            AboutHost.Children.Add(value);
        }
    }

    private void ScalingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var index = ScalingBox.SelectedIndex;
        if (index < 0 || index >= ScalingChoices.Length) return;

        _model.Scaling = ScalingChoices[index].Mode;
        RefreshApplyButton();
    }

    /// <summary>
    /// Synchronous, like Tile_Click: Apply runs a full engine restart (~3 s) inline on this thread,
    /// which is correct because Restart pumps throughout. Still wrapped - a Click handler is a
    /// Dispatcher callback and an escaping exception terminates the app.
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyButton.IsEnabled = false;
            _model.Apply();
            Refresh();
        }
        catch (Exception ex)
        {
            _reporter.Report($"could not apply settings: {ex.Message}");
            RefreshApplyButton();
        }
    }

    /// <summary>
    /// async void, and wrapped end to end for it. Writing the shortcut is shell and file I/O, so it
    /// goes on Task.Run - this thread owns windows parented into Explorer's desktop. The config
    /// write afterwards stays here: it is a small local file, and EngineHost already saves config
    /// on this thread.
    /// </summary>
    private async void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        try
        {
            var wanted = StartWithWindowsBox.IsChecked == true;

            StartWithWindowsBox.IsEnabled = false;
            try
            {
                var applied = await Task.Run(() => _model.ApplyStartupShortcut(wanted));
                if (!applied) _model.ReportNotInstalled();
            }
            finally
            {
                StartWithWindowsBox.IsEnabled = SettingsViewModel.CanStartWithWindows;
            }

            _model.RecordStartWithWindows();

            // Read the checkbox back from the shortcut on disk rather than leaving it where the
            // click put it: Create can decline, and the shortcut is the source of truth.
            _loading = true;
            StartWithWindowsBox.IsChecked = SettingsViewModel.StartsWithWindows;
            _loading = false;
        }
        catch (Exception ex)
        {
            _loading = false;
            _reporter.Report($"could not change the startup setting: {ex.Message}");
        }
    }
}
