using Whallee.Core.Configuration;

namespace Whallee.Core.Theming;

/// <summary>
/// Turns the owner's two choices - a background and an accent - into every colour token the UI uses.
/// </summary>
/// <remarks>
/// TWO INPUTS, NOT TWELVE. Letting the owner set every token separately is how a theme ends up with
/// unreadable text; instead the surfaces, strokes, text and accent states are DERIVED, so whatever
/// is picked, the relationships the hand-tuned dark palette had still hold.
///
/// LIGHT BACKGROUNDS ARE ALLOWED (owner's choice, 2026-11-13) and flip everything that sits on them:
/// surfaces step darker instead of lighter, and text goes dark. The switch is luminance, not
/// "is it white", so a pale grey or a pastel flips too.
///
/// THE DEFAULT INPUTS REPRODUCE THE ORIGINAL TOKENS for the surfaces and text - the +8/+16/+26
/// steps are the shipped palette's own spacing - so an owner who never opens Appearance sees no
/// change at all. Tested.
///
/// The keys are the resource keys in Theme\Tokens.xaml, which is what the App layer swaps.
/// </remarks>
public static class ThemePalette
{
    public static readonly Rgb DefaultBackground = new(0x14, 0x14, 0x14);
    public static readonly Rgb DefaultAccent = new(0xE8, 0xA3, 0x3D);

    /// <summary>Above this luminance a background counts as light, and the text on it goes dark.</summary>
    public const double LightThreshold = 0.35;

    public static bool IsLight(Rgb background) => background.Luminance > LightThreshold;

    public static IReadOnlyDictionary<string, Rgb> Derive(Rgb background, Rgb accent)
    {
        var light = IsLight(background);

        // One step direction for everything layered on the background: lighter on dark, darker on
        // light, so "nearer the front" always reads as further from the backdrop.
        var step = light ? -1 : 1;

        var text = light
            ? (Primary: new Rgb(0x1A, 0x1A, 0x1A), Secondary: new Rgb(0x55, 0x55, 0x55), Disabled: new Rgb(0x99, 0x99, 0x99))
            : (Primary: new Rgb(0xF0, 0xED, 0xE8), Secondary: new Rgb(0x9A, 0x94, 0x8C), Disabled: new Rgb(0x5E, 0x5A, 0x55));

        return new Dictionary<string, Rgb>
        {
            ["SurfaceBrush"] = background,
            ["SurfaceRaisedBrush"] = background.Shift(8 * step),
            ["SurfaceOverlayBrush"] = background.Shift(16 * step),
            ["StrokeBrush"] = background.Shift(26 * step),

            ["TextPrimaryBrush"] = text.Primary,
            ["TextSecondaryBrush"] = text.Secondary,
            ["TextDisabledBrush"] = text.Disabled,

            ["AccentBrush"] = accent,
            ["AccentHoverBrush"] = accent.Mix(Rgb.White, 0.15),
            ["AccentPressedBrush"] = accent.Mix(Rgb.Black, 0.15),

            // Whichever of near-black and near-white reads better ON the accent fill. A pale accent
            // gets dark text, a deep one gets light text - picked by contrast, not by hue.
            ["OnAccentBrush"] = Rgb.Contrast(accent, new Rgb(0x14, 0x14, 0x14)) >=
                                Rgb.Contrast(accent, new Rgb(0xF5, 0xF5, 0xF5))
                ? new Rgb(0x14, 0x14, 0x14)
                : new Rgb(0xF5, 0xF5, 0xF5),

            // The dark-theme red is close to illegible on white, so light backgrounds get a deeper one.
            ["DangerBrush"] = light ? new Rgb(0xC6, 0x28, 0x28) : new Rgb(0xE5, 0x48, 0x4D)
        };
    }

    /// <summary>The saved appearance, with any unreadable value replaced by its default.</summary>
    public static IReadOnlyDictionary<string, Rgb> Derive(AppearanceSettings settings) =>
        Derive(Rgb.ParseOr(settings.Background, DefaultBackground),
               Rgb.ParseOr(settings.Accent, DefaultAccent));
}
