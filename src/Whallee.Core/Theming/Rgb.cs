using System.Globalization;

namespace Whallee.Core.Theming;

/// <summary>An opaque sRGB colour. Core has no WPF, so the theme math works on this instead.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>Parses "#RRGGBB" or "RRGGBB". False for anything else, including alpha forms.</summary>
    public static bool TryParse(string? text, out Rgb value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 ||
            !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;

        value = new Rgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    /// <summary>Parses, or returns <paramref name="fallback"/> - a bad saved value never breaks the theme.</summary>
    public static Rgb ParseOr(string? text, Rgb fallback) => TryParse(text, out var value) ? value : fallback;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public double Luminance =>
        0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (light, dark) = a.Luminance >= b.Luminance ? (a, b) : (b, a);
        return (light.Luminance + 0.05) / (dark.Luminance + 0.05);
    }

    /// <summary>Blends toward <paramref name="other"/>: 0 is this colour, 1 is the other.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        return new Rgb(Lerp(R, other.R, amount), Lerp(G, other.G, amount), Lerp(B, other.B, amount));
    }

    /// <summary>Adds the same amount to every channel, clamped. Negative darkens.</summary>
    public Rgb Shift(int amount)
    {
        static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
        return new Rgb(Clamp(R + amount), Clamp(G + amount), Clamp(B + amount));
    }

    /// <summary>Hue 0-360, saturation and value 0-1.</summary>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - chroma;

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };

        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }
}
