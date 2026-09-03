using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipRenameTests
{
    private static WhalleeConfig ConfigWith(params DisplayAssignment[] displays) =>
        new() { Displays = displays.ToList() };

    private static DisplayAssignment Pinned(string device, string? clip) =>
        new() { DeviceName = device, Mode = DisplayMode.Pinned, PinnedPath = clip };

    private static DisplayAssignment Shuffling(string device, string? clip = null) =>
        new() { DeviceName = device, Mode = DisplayMode.Shuffle, PinnedPath = clip };

    // ---- validation ---------------------------------------------------------

    [Fact]
    public void AnOrdinaryNameIsAccepted() => Assert.Null(ClipRename.Validate("forest at dusk"));

    [Fact]
    public void AnEmptyNameIsRejected() => Assert.NotNull(ClipRename.Validate(""));

    [Fact]
    public void AWhitespaceOnlyNameIsRejected() => Assert.NotNull(ClipRename.Validate("   "));

    [Fact]
    public void ANullNameIsRejected() => Assert.NotNull(ClipRename.Validate(null));

    [Fact]
    public void ABackslashIsRejected() => Assert.NotNull(ClipRename.Validate(@"a\b"));

    [Fact]
    public void AColonIsRejected() => Assert.NotNull(ClipRename.Validate("a:b"));

    [Fact]
    public void AQuestionMarkIsRejected() => Assert.NotNull(ClipRename.Validate("what?"));

    // TRIMMED, NOT REJECTED, changed 2026-09-03. These two used to be errors. Windows strips both
    // when creating the file, so the only real choice was doing it ourselves or scolding the user
    // for a slip with one obvious fix.
    [Fact]
    public void ATrailingSpaceIsAcceptedAndTrimmed()
    {
        Assert.Null(ClipRename.Validate("forest "));
        Assert.Equal("forest", ClipRename.Normalise("forest "));
    }

    [Fact]
    public void ATrailingDotIsAcceptedAndTrimmed()
    {
        Assert.Null(ClipRename.Validate("forest."));
        Assert.Equal("forest", ClipRename.Normalise("forest."));
    }

    [Fact]
    public void LeadingWhitespaceIsTrimmedToo() =>
        Assert.Equal("forest", ClipRename.Normalise("   forest"));

    // Trim() runs before TrimEnd('.', ' '), so the dot is exposed and removed in one pass.
    [Fact]
    public void ATrailingDotFollowedByASpaceIsFullyTrimmed() =>
        Assert.Equal("forest", ClipRename.Normalise("forest. "));

    [Fact]
    public void InteriorSpacesAreKept() =>
        Assert.Equal("forest at dusk", ClipRename.Normalise("  forest at dusk  "));

    // A name that is nothing but trimmable characters leaves nothing to name the file.
    [Fact]
    public void ANameOfOnlyDotsAndSpacesIsRejected() => Assert.NotNull(ClipRename.Validate(" . . "));

    [Fact]
    public void NormaliseTreatsNullAsEmpty() => Assert.Equal("", ClipRename.Normalise(null));

    // The message must point at the character the user actually typed, not just recite the rule.
    [Fact]
    public void TheInvalidCharacterMessageNamesTheOffendingCharacter()
    {
        var message = ClipRename.Validate("what?");

        Assert.NotNull(message);
        Assert.Contains("?", message);
    }

    [Fact]
    public void TheInvalidCharacterMessageListsTheCommonOffenders()
    {
        var message = ClipRename.Validate("a:b");

        Assert.NotNull(message);
        Assert.Contains("*", message);
        Assert.Contains("|", message);
    }

    [Fact]
    public void AnOverLongNameIsRejected() =>
        Assert.NotNull(ClipRename.Validate(new string('a', 200)));

    // Validate does not know the old name; "nothing changed" is the dialog's job, not an error.
    [Fact]
    public void ValidateDoesNotCareWhetherTheNameChanged() =>
        Assert.Null(ClipRename.Validate("forest"));

    // ---- repointing ---------------------------------------------------------

    [Fact]
    public void RepointsTheDisplayPinnedToTheOldName()
    {
        var config = ConfigWith(Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
        Assert.Equal("woods.mp4", config.Displays[0].PinnedPath);
        Assert.Equal(DisplayMode.Pinned, config.Displays[0].Mode);
    }

    [Fact]
    public void RepointsEveryDisplayPinnedToIt()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "forest.mp4"),
            Pinned(@"\\.\DISPLAY1", "forest.mp4"));

        Assert.Equal(3, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
    }

    [Fact]
    public void RepointsNothingWhenNoDisplayNamesIt() =>
        Assert.Equal(0, ClipRename.Repoint(
            ConfigWith(Pinned(@"\\.\DISPLAY6", "ocean.mp4"), Shuffling(@"\\.\DISPLAY5")),
            "forest.mp4", "woods.mp4"));

    [Fact]
    public void MatchingTheOldNameIsCaseInsensitive()
    {
        var config = ConfigWith(Pinned(@"\\.\DISPLAY6", "Forest.MP4"));

        Assert.Equal(1, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
        Assert.Equal("woods.mp4", config.Displays[0].PinnedPath);
    }

    [Fact]
    public void AnotherClipsPinIsUntouched()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "ocean.mp4"));

        Assert.Equal(1, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
        Assert.Equal("ocean.mp4", config.Displays[1].PinnedPath);
    }

    // A stale reference on a shuffling display is carried across rather than left dangling -
    // mirroring how ClipRemoval clears one - and its mode must not change.
    [Fact]
    public void AStalePinOnAShufflingDisplayIsRepointedWithoutChangingMode()
    {
        var config = ConfigWith(Shuffling(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
        Assert.Equal("woods.mp4", config.Displays[0].PinnedPath);
        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
    }

    // ClipResolver accepts a rooted pin inside the library, so matching must use the bare name.
    [Fact]
    public void ARootedPinnedPathStillMatches()
    {
        var config = ConfigWith(Pinned(@"\\.\DISPLAY6", @"C:\lib\forest.mp4"));

        Assert.Equal(1, ClipRename.Repoint(config, "forest.mp4", "woods.mp4"));
        Assert.Equal("woods.mp4", config.Displays[0].PinnedPath);
    }

    [Fact]
    public void ANullPinnedPathIsIgnored() =>
        Assert.Equal(0, ClipRename.Repoint(
            ConfigWith(Pinned(@"\\.\DISPLAY6", null)), "forest.mp4", "woods.mp4"));
}
