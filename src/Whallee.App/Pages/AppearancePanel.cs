using System.Windows;
using System.Windows.Controls;
using Whallee.App.Theme;
using Whallee.Core.Configuration;
using Whallee.Core.Theming;

namespace Whallee.App.Pages;

/// <summary>
/// The Settings page's Appearance section: accent, background, and an optional gradient backdrop.
/// </summary>
/// <remarks>
/// APPLIES AND SAVES ON EVERY CHANGE, outside the page's Apply button. That button restarts
/// playback; a colour change touches no player, so making the owner press it (and sit through a
/// restart) to see a colour would be all cost. ThemeApplier repaints live; <c>save</c> only writes
/// config.
///
/// Requested 2026-11-13. Gradient is the window backdrop only (owner's choice): cards and controls
/// stay solid so text on them stays readable.
/// </remarks>
internal static class AppearancePanel
{
    private static readonly (string, Rgb)[] AccentPresets =
    {
        ("Amber", new Rgb(0xE8, 0xA3, 0x3D)),
        ("Teal", new Rgb(0x2B, 0xB3, 0xA3)),
        ("Blue", new Rgb(0x3D, 0x8B, 0xE8)),
        ("Violet", new Rgb(0x9B, 0x6B, 0xE8)),
        ("Rose", new Rgb(0xE8, 0x57, 0x7D)),
        ("Green", new Rgb(0x5C, 0xB8, 0x5C)),
        ("Coral", new Rgb(0xF0, 0x7C, 0x4A)),
        ("Slate", new Rgb(0x8F, 0xA3, 0xB8))
    };

    private static readonly (string, Rgb)[] BackgroundPresets =
    {
        ("Charcoal", new Rgb(0x14, 0x14, 0x14)),
        ("Black", new Rgb(0x00, 0x00, 0x00)),
        ("Midnight", new Rgb(0x0E, 0x16, 0x26)),
        ("Plum", new Rgb(0x1C, 0x12, 0x26)),
        ("Forest", new Rgb(0x0F, 0x1F, 0x17)),
        ("Paper (light)", new Rgb(0xF4, 0xF1, 0xEA))
    };

    private static readonly (string, Rgb)[] GradientPresets =
    {
        ("Indigo", new Rgb(0x2A, 0x1B, 0x3D)),
        ("Navy", new Rgb(0x10, 0x25, 0x3F)),
        ("Deep teal", new Rgb(0x0F, 0x3B, 0x3A)),
        ("Wine", new Rgb(0x3A, 0x12, 0x22)),
        ("Ember", new Rgb(0x3B, 0x24, 0x10)),
        ("Charcoal", new Rgb(0x14, 0x14, 0x14))
    };

    private static readonly (string Label, GradientDirection Direction)[] Directions =
    {
        ("Diagonal", GradientDirection.Diagonal),
        ("Top to bottom", GradientDirection.TopToBottom),
        ("Left to right", GradientDirection.LeftToRight)
    };

    /// <param name="save">Persists config. Called after every change.</param>
    /// <param name="report">Where a failure is written - never thrown, see the remarks on handlers.</param>
    public static void Build(Panel host, AppearanceSettings settings, Action save, Action<string> report)
    {
        host.Children.Clear();

        var accent = new ColorChooser(AccentPresets, Rgb.ParseOr(settings.Accent, ThemePalette.DefaultAccent));
        var background = new ColorChooser(BackgroundPresets, Rgb.ParseOr(settings.Background, ThemePalette.DefaultBackground));
        var gradientEnd = new ColorChooser(GradientPresets, Rgb.ParseOr(settings.GradientEnd, GradientPresets[0].Item2));

        var gradientBox = new CheckBox { Content = "Gradient background", IsChecked = settings.Gradient, Margin = new Thickness(0, 14, 0, 0) };
        var gradientNote = Caption("Fades the window backdrop from the background colour to this one. Cards and buttons stay solid.");
        var direction = new ComboBox { Width = 190, ItemsSource = Directions.Select(d => d.Label).ToList() };
        direction.SelectedIndex = Math.Max(0, Array.FindIndex(Directions, d => d.Direction == settings.GradientDirection));

        var gradientPanel = new StackPanel { Margin = new Thickness(26, 8, 0, 0) };
        gradientPanel.Children.Add(gradientNote);
        gradientPanel.Children.Add(Labelled("Fade to", gradientEnd));
        gradientPanel.Children.Add(Labelled("Direction", direction));
        gradientPanel.Visibility = settings.Gradient ? Visibility.Visible : Visibility.Collapsed;

        var reset = new Button { Content = "Reset to defaults", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };

        void Changed(Action edit)
        {
            try
            {
                edit();
                ThemeApplier.Apply(settings);
                save();
            }
            catch (Exception ex)
            {
                report($"could not change the colours: {ex.Message}");
            }
        }

        accent.Changed += c => Changed(() => settings.Accent = c.ToHex());
        background.Changed += c => Changed(() => settings.Background = c.ToHex());
        gradientEnd.Changed += c => Changed(() => settings.GradientEnd = c.ToHex());

        gradientBox.Checked += (_, _) => Changed(() => { settings.Gradient = true; gradientPanel.Visibility = Visibility.Visible; });
        gradientBox.Unchecked += (_, _) => Changed(() => { settings.Gradient = false; gradientPanel.Visibility = Visibility.Collapsed; });

        direction.SelectionChanged += (_, _) =>
        {
            if (direction.SelectedIndex >= 0)
                Changed(() => settings.GradientDirection = Directions[direction.SelectedIndex].Direction);
        };

        reset.Click += (_, _) => Changed(() =>
        {
            var defaults = new AppearanceSettings();
            settings.Accent = defaults.Accent;
            settings.Background = defaults.Background;
            settings.Gradient = defaults.Gradient;
            settings.GradientEnd = defaults.GradientEnd;
            settings.GradientDirection = defaults.GradientDirection;

            accent.SetValue(ThemePalette.DefaultAccent);
            background.SetValue(ThemePalette.DefaultBackground);
            gradientEnd.SetValue(Rgb.ParseOr(defaults.GradientEnd, GradientPresets[0].Item2));
            gradientBox.IsChecked = false;   // raises Unchecked, which hides the panel
            direction.SelectedIndex = Array.FindIndex(Directions, d => d.Direction == defaults.GradientDirection);
        });

        host.Children.Add(Labelled("Accent", accent));
        host.Children.Add(Labelled("Background", background));
        host.Children.Add(gradientBox);
        host.Children.Add(gradientPanel);
        host.Children.Add(reset);
    }

    private static UIElement Labelled(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };

        var text = new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DockPanel.SetDock(text, Dock.Left);

        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Application.Current.TryFindResource("CaptionStyle") is Style style) block.Style = style;
        return block;
    }
}
