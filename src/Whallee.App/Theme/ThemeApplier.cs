using System.Windows;
using System.Windows.Media;
using Whallee.Core.Configuration;
using Whallee.Core.Theming;

namespace Whallee.App.Theme;

/// <summary>
/// Puts the owner's Appearance settings into effect, live, across every open window.
/// </summary>
/// <remarks>
/// SETS EACH KEY AT THE TOP OF Application.Resources. A top-level key wins over the same key in a
/// merged dictionary (Tokens.xaml), and every colour reference in the XAML is a DynamicResource,
/// so replacing the entry repaints everything that uses it - no window reload, no restart.
///
/// NEW BRUSHES, NOT MUTATED ONES. The brushes loaded from Tokens.xaml end up frozen once a style
/// setter has used them, and setting Color on a frozen brush throws. Each call builds fresh,
/// frozen brushes instead.
///
/// UI THREAD ONLY, like everything that touches Application.Resources.
/// </remarks>
internal static class ThemeApplier
{
    public static void Apply(AppearanceSettings settings)
    {
        var resources = Application.Current.Resources;

        foreach (var (key, colour) in ThemePalette.Derive(settings))
            resources[key] = Solid(colour);

        var start = Rgb.ParseOr(settings.Background, ThemePalette.DefaultBackground);
        resources["WindowBackdropBrush"] = settings.Gradient
            ? Gradient(start, Rgb.ParseOr(settings.GradientEnd, start), settings.GradientDirection)
            : Solid(start);
    }

    private static Brush Solid(Rgb colour)
    {
        var brush = new SolidColorBrush(ToColor(colour));
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Rgb from, Rgb to, GradientDirection direction)
    {
        var (start, end) = direction switch
        {
            GradientDirection.TopToBottom => (new Point(0, 0), new Point(0, 1)),
            GradientDirection.LeftToRight => (new Point(0, 0), new Point(1, 0)),
            _ => (new Point(0, 0), new Point(1, 1))
        };

        var brush = new LinearGradientBrush(ToColor(from), ToColor(to), start, end);
        brush.Freeze();
        return brush;
    }

    public static Color ToColor(Rgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);
}
