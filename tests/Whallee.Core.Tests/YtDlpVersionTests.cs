using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class YtDlpVersionTests
{
    private const string WingetRoot = @"C:\Users\someone\AppData\Local\Microsoft\WinGet\Packages";

    [Fact]
    public void ReadsAStableVersion()
    {
        Assert.Equal(new DateOnly(2026, 8, 19), YtDlpVersion.Parse("2026.08.19"));
    }

    /// <summary>
    /// Nightly and master builds carry a fourth component. The date is still the first three, and
    /// refusing to parse these would silently disable the whole feature for anyone on a nightly.
    /// </summary>
    [Fact]
    public void ReadsANightlyBuildsFourthComponent()
    {
        Assert.Equal(new DateOnly(2026, 8, 19), YtDlpVersion.Parse("2026.08.19.232709"));
    }

    [Fact]
    public void IgnoresSurroundingWhitespaceAndTrailingText()
    {
        Assert.Equal(new DateOnly(2026, 8, 19), YtDlpVersion.Parse("  2026.08.19  \n"));
        Assert.Equal(new DateOnly(2026, 8, 19), YtDlpVersion.Parse("2026.08.19 [a1b2c3d]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    [InlineData("2026.08")]
    [InlineData("2026.13.45")]
    public void GivesNothingForOutputItCannotRead(string? output)
    {
        Assert.Null(YtDlpVersion.Parse(output));
    }

    [Fact]
    public void AgeIsWholeDays()
    {
        Assert.Equal(45, YtDlpVersion.Age(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 15)));
    }

    /// <summary>
    /// THE BOUNDARY, ASSERTED FROM BOTH SIDES. 45 is the whole policy; an off-by-one here either
    /// nags a day early forever or hides the warning for a day, and neither would ever be noticed.
    /// </summary>
    [Theory]
    [InlineData(44, false)]
    [InlineData(45, true)]
    [InlineData(46, true)]
    public void IsStaleOnlyAtOrPastTheThreshold(int days, bool expected)
    {
        var today = new DateOnly(2026, 9, 15);
        var released = today.AddDays(-days);

        Assert.Equal(expected, YtDlpVersion.IsStale(released, today));
    }

    /// <summary>
    /// Clock skew, or a nightly built on a machine ahead of this one. A negative age must read as
    /// "fine", never wrap into a large positive number and warn.
    /// </summary>
    [Fact]
    public void AVersionFromTheFutureIsNotStale()
    {
        var today = new DateOnly(2026, 9, 15);

        Assert.False(YtDlpVersion.IsStale(today.AddDays(10), today));
    }

    [Theory]
    [InlineData(45, "6 weeks old")]
    [InlineData(59, "8 weeks old")]
    [InlineData(60, "2 months old")]
    [InlineData(90, "3 months old")]
    public void PhrasesAnAgeTheWayAPersonWould(int days, string expected)
    {
        Assert.Equal(expected, YtDlpVersion.DescribeAge(days));
    }

    [Fact]
    public void NamesWingetWhenTheExeLivesUnderTheWingetTree()
    {
        var path = Path.Combine(WingetRoot, "yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe", "yt-dlp.exe");

        Assert.Equal("winget upgrade yt-dlp", YtDlpVersion.UpdateCommandFor(path, WingetRoot));
    }

    [Fact]
    public void NamesSelfUpdateForALooseExe()
    {
        Assert.Equal("yt-dlp -U", YtDlpVersion.UpdateCommandFor(@"C:\tools\yt-dlp.exe", WingetRoot));
    }

    /// <summary>
    /// WHY THIS INFERS FROM THE PATH RATHER THAN FROM WHICH LOCATOR BRANCH FIRED. A configured
    /// path can point straight into the winget tree, and there "winget upgrade" is still the right
    /// command - a branch-based answer would say "yt-dlp -U" and be wrong.
    /// </summary>
    [Fact]
    public void NamesWingetEvenForAConfiguredPathInsideTheWingetTree()
    {
        var path = Path.Combine(WingetRoot, "yt-dlp.yt-dlp_x", "yt-dlp.exe");

        Assert.Equal("winget upgrade yt-dlp", YtDlpVersion.UpdateCommandFor(path, WingetRoot));
    }

    [Fact]
    public void ADirectoryMerelyStartingWithTheRootsNameIsNotInsideIt()
    {
        Assert.Equal("yt-dlp -U", YtDlpVersion.UpdateCommandFor(WingetRoot + "Extra\\yt-dlp.exe", WingetRoot));
    }

    [Fact]
    public void WarnsWithTheAgeAndTheCommand()
    {
        var today = new DateOnly(2026, 9, 15);
        var warning = YtDlpVersion.DialogWarning(today.AddDays(-90), today, @"C:\tools\yt-dlp.exe", WingetRoot);

        Assert.NotNull(warning);
        Assert.Contains("3 months old", warning);
        Assert.Contains("yt-dlp -U", warning);
    }

    [Fact]
    public void SaysNothingWhenTheVersionIsCurrent()
    {
        var today = new DateOnly(2026, 9, 15);

        Assert.Null(YtDlpVersion.DialogWarning(today.AddDays(-3), today, @"C:\tools\yt-dlp.exe", WingetRoot));
    }

    /// <summary>
    /// Silence is the design for every failure: an unreadable version must not produce a warning,
    /// because a check that cries wolf teaches the owner to ignore the one message it exists for.
    /// </summary>
    [Fact]
    public void SaysNothingWhenTheVersionCouldNotBeRead()
    {
        Assert.Null(YtDlpVersion.DialogWarning(null, new DateOnly(2026, 9, 15), @"C:\tools\yt-dlp.exe", WingetRoot));
    }
}
