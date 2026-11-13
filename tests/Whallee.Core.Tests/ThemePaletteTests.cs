using Whallee.Core.Configuration;
using Whallee.Core.Theming;

namespace Whallee.Core.Tests;

public class ThemePaletteTests
{
    /// <summary>An owner who never opens Appearance must see exactly the palette that shipped.</summary>
    [Fact]
    public void DefaultsReproduceTheShippedTokens()
    {
        var tokens = ThemePalette.Derive(new AppearanceSettings());

        Assert.Equal("#141414", tokens["SurfaceBrush"].ToHex());
        Assert.Equal("#1C1C1C", tokens["SurfaceRaisedBrush"].ToHex());
        Assert.Equal("#242424", tokens["SurfaceOverlayBrush"].ToHex());
        Assert.Equal("#2E2E2E", tokens["StrokeBrush"].ToHex());
        Assert.Equal("#F0EDE8", tokens["TextPrimaryBrush"].ToHex());
        Assert.Equal("#E8A33D", tokens["AccentBrush"].ToHex());
        Assert.Equal("#141414", tokens["OnAccentBrush"].ToHex());
        Assert.Equal("#E5484D", tokens["DangerBrush"].ToHex());
    }

    [Theory]
    [InlineData("#141414")]
    [InlineData("#0B1A2E")]
    [InlineData("#F4F1EA")]
    [InlineData("#FFFFFF")]
    [InlineData("#7FB3D5")]
    public void PrimaryTextIsReadableOnAnyBackground(string background)
    {
        var tokens = ThemePalette.Derive(Rgb.ParseOr(background, default), ThemePalette.DefaultAccent);

        Assert.True(Rgb.Contrast(tokens["TextPrimaryBrush"], tokens["SurfaceBrush"]) >= 4.5);
    }

    [Fact]
    public void ALightBackgroundFlipsTextAndStepsSurfacesDarker()
    {
        var tokens = ThemePalette.Derive(new Rgb(0xF4, 0xF1, 0xEA), ThemePalette.DefaultAccent);

        Assert.True(tokens["TextPrimaryBrush"].Luminance < 0.1);
        Assert.True(tokens["SurfaceRaisedBrush"].Luminance < tokens["SurfaceBrush"].Luminance);
    }

    [Theory]
    [InlineData("#E8A33D")]
    [InlineData("#1E3A8A")]
    [InlineData("#F9E79F")]
    [InlineData("#8E44AD")]
    public void TextOnTheAccentFillPicksTheMoreReadableShade(string accent)
    {
        var fill = Rgb.ParseOr(accent, default);
        var on = ThemePalette.Derive(ThemePalette.DefaultBackground, fill)["OnAccentBrush"];

        var other = on.Luminance < 0.5 ? new Rgb(0xF5, 0xF5, 0xF5) : new Rgb(0x14, 0x14, 0x14);
        Assert.True(Rgb.Contrast(on, fill) >= Rgb.Contrast(other, fill));
    }

    [Fact]
    public void HoverIsLighterAndPressedIsDarkerThanTheAccent()
    {
        var tokens = ThemePalette.Derive(ThemePalette.DefaultBackground, new Rgb(0x3D, 0x8B, 0xE8));

        Assert.True(tokens["AccentHoverBrush"].Luminance > tokens["AccentBrush"].Luminance);
        Assert.True(tokens["AccentPressedBrush"].Luminance < tokens["AccentBrush"].Luminance);
    }

    [Fact]
    public void AnUnreadableSavedColourFallsBackToTheDefault()
    {
        var tokens = ThemePalette.Derive(new AppearanceSettings { Background = "blue", Accent = "#12" });

        Assert.Equal("#141414", tokens["SurfaceBrush"].ToHex());
        Assert.Equal("#E8A33D", tokens["AccentBrush"].ToHex());
    }
}

public class RgbTests
{
    [Theory]
    [InlineData("#E8A33D", 0xE8, 0xA3, 0x3D)]
    [InlineData("e8a33d", 0xE8, 0xA3, 0x3D)]
    [InlineData("  #000000 ", 0, 0, 0)]
    public void ParsesHex(string text, int r, int g, int b)
    {
        Assert.True(Rgb.TryParse(text, out var value));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#FFFFFFFF")]
    [InlineData("#GGGGGG")]
    public void RejectsAnythingElse(string? text)
    {
        Assert.False(Rgb.TryParse(text, out _));
    }

    [Theory]
    [InlineData("#E8A33D")]
    [InlineData("#1E3A8A")]
    [InlineData("#808080")]
    [InlineData("#FF0000")]
    [InlineData("#00FF7F")]
    public void HsvRoundTrips(string hex)
    {
        var colour = Rgb.ParseOr(hex, default);
        var (h, s, v) = colour.ToHsv();

        Assert.Equal(colour, Rgb.FromHsv(h, s, v));
    }

    [Fact]
    public void ContrastOfBlackOnWhiteIsTwentyOne()
    {
        Assert.Equal(21, Rgb.Contrast(Rgb.Black, Rgb.White), 3);
    }
}
