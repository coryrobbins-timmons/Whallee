using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Whallee.App.Theme;
using Whallee.Core.Theming;

namespace Whallee.App.Pages;

/// <summary>
/// Preset swatches plus a "Custom" panel: hue, saturation and brightness sliders and a hex box.
/// </summary>
/// <remarks>
/// BUILT IN CODE, like the rest of SettingsPage's dynamic rows. One instance per colour setting.
///
/// THE SLIDERS AND THE HEX BOX DRIVE EACH OTHER, guarded by _syncing so moving one does not bounce
/// back through the other and round the value. A hex that does not parse yet (mid-typing) is simply
/// ignored rather than flagged - the swatch shows the last good colour.
///
/// Every handler is wrapped: these are Dispatcher callbacks, and the Changed handler repaints the
/// whole app, so an exception escaping one would end the process.
/// </remarks>
internal sealed class ColorChooser : StackPanel
{
    private readonly (string Name, Rgb Colour)[] _presets;
    private readonly WrapPanel _swatches = new();
    private readonly StackPanel _custom = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Slider _hue = Make(0, 360);
    private readonly Slider _saturation = Make(0, 100);
    private readonly Slider _brightness = Make(0, 100);
    private readonly TextBox _hex = new() { Width = 90 };
    private readonly Border _preview = new() { Width = 36, Height = 24, CornerRadius = new CornerRadius(4), Margin = new Thickness(10, 0, 0, 0) };
    private readonly ToggleButton _customToggle = new() { Content = "Custom…", Margin = new Thickness(6, 0, 0, 6) };

    private Rgb _value;
    private bool _syncing;

    /// <summary>Raised with the new colour whenever the owner picks or adjusts one.</summary>
    public event Action<Rgb>? Changed;

    public ColorChooser((string Name, Rgb Colour)[] presets, Rgb initial)
    {
        _presets = presets;
        _value = initial;

        Children.Add(_swatches);
        BuildCustomPanel();
        Children.Add(_custom);

        _customToggle.Checked += (_, _) => _custom.Visibility = Visibility.Visible;
        _customToggle.Unchecked += (_, _) => _custom.Visibility = Visibility.Collapsed;

        Show(initial, fromSliders: false);

        // A saved colour that is not a preset was custom-made: open the panel so the owner sees it.
        if (!presets.Any(p => p.Colour == initial)) _customToggle.IsChecked = true;
    }

    public Rgb Value => _value;

    /// <summary>Sets the colour from outside (Reset) without raising Changed.</summary>
    public void SetValue(Rgb colour)
    {
        Show(colour, fromSliders: false);
        _customToggle.IsChecked = !_presets.Any(p => p.Colour == colour);
    }

    private void BuildSwatches()
    {
        _swatches.Children.Clear();

        foreach (var (name, colour) in _presets)
        {
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(ThemeApplier.ToColor(colour)),
                BorderThickness = new Thickness(colour == _value ? 3 : 1)
            };
            dot.SetResourceReference(Border.BorderBrushProperty,
                colour == _value ? "TextPrimaryBrush" : "StrokeBrush");

            // A bare Border, not a Button: the themed Button template would draw its own chrome
            // around the dot. Clicked via MouseLeftButtonUp, with a hand cursor and a tooltip so it
            // still reads as clickable.
            var captured = colour;
            dot.Cursor = System.Windows.Input.Cursors.Hand;
            dot.ToolTip = $"{name} ({colour.ToHex()})";
            dot.Margin = new Thickness(0, 0, 8, 6);
            dot.MouseLeftButtonUp += (_, _) => Pick(captured);

            _swatches.Children.Add(dot);
        }

        _swatches.Children.Add(_customToggle);
    }

    private void BuildCustomPanel()
    {
        // The hue slider sits under a rainbow strip, so "hue" needs no explaining.
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var h = 0; h <= 360; h += 60)
            rainbow.GradientStops.Add(new GradientStop(ThemeApplier.ToColor(Rgb.FromHsv(h, 1, 1)), h / 360.0));
        rainbow.Freeze();

        _custom.Children.Add(Row("Hue", _hue, new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = rainbow, Margin = new Thickness(0, 0, 0, 2) }));
        _custom.Children.Add(Row("Saturation", _saturation, null));
        _custom.Children.Add(Row("Brightness", _brightness, null));

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        hexRow.Children.Add(Label("Hex"));
        hexRow.Children.Add(_hex);
        hexRow.Children.Add(_preview);
        _custom.Children.Add(hexRow);

        _hue.ValueChanged += (_, _) => FromSliders();
        _saturation.ValueChanged += (_, _) => FromSliders();
        _brightness.ValueChanged += (_, _) => FromSliders();
        _hex.TextChanged += (_, _) => FromHex();
    }

    private void FromSliders()
    {
        if (_syncing) return;
        Commit(Rgb.FromHsv(_hue.Value, _saturation.Value / 100, _brightness.Value / 100), fromSliders: true);
    }

    private void FromHex()
    {
        if (_syncing || !Rgb.TryParse(_hex.Text, out var colour)) return;
        Commit(colour, fromSliders: false, fromHex: true);
    }

    private void Pick(Rgb colour) => Commit(colour, fromSliders: false);

    private void Commit(Rgb colour, bool fromSliders, bool fromHex = false)
    {
        try
        {
            if (colour == _value) return;
            Show(colour, fromSliders, fromHex);
            Changed?.Invoke(colour);
        }
        catch (Exception)
        {
            // Swallowed here; the page reports failures from its own Changed handler.
        }
    }

    /// <summary>Brings every control in line with <paramref name="colour"/>, without re-raising.</summary>
    private void Show(Rgb colour, bool fromSliders, bool fromHex = false)
    {
        _value = colour;
        _syncing = true;
        try
        {
            // The sliders are left alone when they are the source: re-deriving HSV from a rounded
            // RGB would nudge the thumb the owner is dragging.
            if (!fromSliders)
            {
                var (h, s, v) = colour.ToHsv();
                _hue.Value = h;
                _saturation.Value = s * 100;
                _brightness.Value = v * 100;
            }

            // Likewise the hex box keeps the owner's own typing ("#3d8be8" stays lower case).
            if (!fromHex) _hex.Text = colour.ToHex();

            _preview.Background = new SolidColorBrush(ThemeApplier.ToColor(colour));
            BuildSwatches();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static Slider Make(double min, double max) => new()
    {
        Minimum = min, Maximum = max, Width = 220, IsMoveToPointEnabled = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static UIElement Row(string label, Slider slider, UIElement? above)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(Label(label));

        if (above is null)
        {
            row.Children.Add(slider);
        }
        else
        {
            var stack = new StackPanel { Width = 220 };
            stack.Children.Add(above);
            stack.Children.Add(slider);
            row.Children.Add(stack);
        }

        return row;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, Width = 80, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return block;
    }
}
