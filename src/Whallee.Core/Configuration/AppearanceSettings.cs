namespace Whallee.Core.Configuration;

public enum GradientDirection { TopToBottom, Diagonal, LeftToRight }

/// <summary>The owner's interface colours. See ThemePalette for how the rest are derived.</summary>
/// <remarks>
/// HEX STRINGS, not a colour type: config is JSON the owner can read and fix by hand, and an
/// unreadable value falls back to the default rather than breaking the window (Rgb.ParseOr).
/// Defaults are the shipped palette, so a config written before this existed looks unchanged.
/// </remarks>
public sealed class AppearanceSettings
{
    public string Background { get; set; } = "#141414";

    /// <summary>The amber, by default.</summary>
    public string Accent { get; set; } = "#E8A33D";

    /// <summary>When on, the window backdrop fades from Background to GradientEnd. Cards stay solid.</summary>
    public bool Gradient { get; set; }

    public string GradientEnd { get; set; } = "#2A1B3D";

    public GradientDirection GradientDirection { get; set; } = GradientDirection.Diagonal;
}
