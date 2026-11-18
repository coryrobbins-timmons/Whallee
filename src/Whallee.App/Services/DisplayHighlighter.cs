using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Whallee.Core.Displays;

namespace Whallee.App.Services;

/// <summary>
/// Draws a glowing accent-coloured frame around a monitor while the owner hovers a choice that
/// names it - "Display 2" in Apply to, the clip menu, or a Settings row - so it is obvious which
/// screen is about to change.
/// </summary>
/// <remarks>
/// ONE OVERLAY WINDOW PER MONITOR, created on first use and then only shown and hidden. Each is a
/// borderless, transparent, topmost WPF window with WS_EX_TRANSPARENT (clicks pass straight
/// through), WS_EX_NOACTIVATE (never steals focus from the menu being hovered) and
/// WS_EX_TOOLWINDOW (no taskbar or Alt+Tab entry).
///
/// POSITIONED WITH SetWindowPos IN THE SAME COORDINATES DisplayEnumerator REPORTS, not with WPF's
/// Left/Top. WPF's are device-independent units, and converting per monitor would need each
/// monitor's DPI; the Win32 call takes the enumerator's numbers as they are, in the same process
/// DPI context, so the frame lands on the monitor exactly.
///
/// THE COLOUR IS A DynamicResource TO AccentBrush, so it follows Settings › Appearance live.
///
/// HIDING IS DEBOUNCED by a few frames. Moving the pointer from "Display 1" to "Display 2" raises
/// leave-then-enter; without the delay the frames would blink off and on between every item.
///
/// UI THREAD ONLY. Wrapped throughout: it runs inside hover handlers, which are Dispatcher
/// callbacks, and a highlight is never worth crashing over.
/// </remarks>
internal sealed class DisplayHighlighter
{
    public static DisplayHighlighter Instance { get; } = new();

    private readonly Dictionary<string, Window> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _hideTimer;

    private DisplayHighlighter()
    {
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HideNow();
        };
    }

    /// <summary>Frames one display, or every display when <paramref name="deviceName"/> is null.</summary>
    public void Show(string? deviceName)
    {
        try
        {
            _hideTimer.Stop();

            var displays = DisplayEnumerator.Enumerate();
            var wanted = displays
                .Where(d => deviceName is null ||
                            string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var (name, frame) in _frames)
                if (!wanted.Any(d => string.Equals(d.DeviceName, name, StringComparison.OrdinalIgnoreCase)))
                    frame.Hide();

            foreach (var display in wanted)
            {
                var frame = FrameFor(display.DeviceName);
                if (!frame.IsVisible) frame.Show();
                Place(frame, display);
            }
        }
        catch (Exception)
        {
            // Purely a visual aid. Failing to draw it must not disturb the menu being hovered.
        }
    }

    /// <summary>Hides every frame after a short delay, unless another Show arrives first.</summary>
    public void Hide()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideNow()
    {
        try
        {
            foreach (var frame in _frames.Values) frame.Hide();
        }
        catch (Exception)
        {
        }
    }

    private Window FrameFor(string deviceName)
    {
        if (_frames.TryGetValue(deviceName, out var existing)) return existing;

        // THREE LAYERS, made bolder on 2026-11-18 after the owner found a single 6 px line too
        // subtle: a solid 12 px rim, a wider translucent band inside it that reads as glow
        // spilling onto the screen, and a large label naming the display in the middle of the top
        // edge, so the frame says WHICH screen as well as where.
        var label = new TextBlock
        {
            Text = Whallee.Core.Desktop.DisplayLabel.For(deviceName),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(22, 8, 22, 10)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");

        var badge = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(0, 0, 14, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        // Siblings in a Grid, not nested: Opacity on the band would otherwise fade the badge too.
        var band = new Border { BorderThickness = new Thickness(30), Opacity = 0.35 };
        band.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

        var layers = new Grid();
        layers.Children.Add(band);
        layers.Children.Add(badge);

        var border = new Border
        {
            BorderThickness = new Thickness(12),
            Child = layers,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 60, Opacity = 1 }
        };
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

        // The glow takes the accent too. DropShadowEffect.Color is a Color, not a brush, so it is
        // read from the brush each time the frame is shown (see Place) rather than bound.
        var frame = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            Content = border,
            Width = 1, Height = 1
        };

        frame.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(frame).Handle;
            var style = GetWindowLongPtr(handle, GWL_EXSTYLE);
            SetWindowLongPtr(handle, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        _frames[deviceName] = frame;
        return frame;
    }

    private static void Place(Window frame, DisplayInfo display)
    {
        if (frame.Content is Border { Effect: DropShadowEffect glow } outer)
        {
            if (Application.Current.TryFindResource("AccentBrush") is SolidColorBrush accent)
                glow.Color = accent.Color;

            // Re-read each time: the frame is cached per device, but Windows' numbering can change
            // after a dock or undock, and the badge must say what Apply to says.
            if (outer.Child is Grid { Children: [_, Border { Child: TextBlock label }] })
                label.Text = Whallee.Core.Desktop.DisplayLabel.For(display.DeviceName);
        }

        var handle = new WindowInteropHelper(frame).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HWND_TOPMOST, display.X, display.Y, display.Width, display.Height,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TRANSPARENT = 0x00000020;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
