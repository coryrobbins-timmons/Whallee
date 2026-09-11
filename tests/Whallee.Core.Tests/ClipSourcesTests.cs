using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipSourcesTests
{
    private static ClipSource Source(string url = "https://www.youtube.com/watch?v=abc") =>
        new(url, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);

    [Fact]
    public void RemembersWhereAClipCameFrom()
    {
        var index = new ClipSourceIndex();
        index.Record("City Skyline.mp4", Source());

        Assert.Equal("https://www.youtube.com/watch?v=abc", index.Get("City Skyline.mp4")!.Url);
    }

    /// <summary>
    /// A clip with no entry is NORMAL, not an error: every clip imported before 2026-09-11, and
    /// every clip added from a local file, has no link to remember. Callers show "not recorded".
    /// </summary>
    [Theory]
    [InlineData("Never Imported.mp4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AMissIsNullRatherThanAFailure(string? fileName)
    {
        Assert.Null(new ClipSourceIndex().Get(fileName));
    }

    /// <summary>
    /// Windows file names are case-insensitive, so a case-sensitive map would hold two entries for
    /// one clip and Details would show whichever was written last.
    /// </summary>
    [Fact]
    public void MatchesFileNamesWithoutRegardToCase()
    {
        var index = new ClipSourceIndex();
        index.Record("City Skyline.mp4", Source());

        Assert.NotNull(index.Get("city skyline.MP4"));
    }

    /// <summary>
    /// Re-importing to the same name means the clip genuinely came from somewhere new. Keeping the
    /// old entry would make Details lie about the file on disk.
    /// </summary>
    [Fact]
    public void RecordingAgainReplacesTheOldEntry()
    {
        var index = new ClipSourceIndex();
        index.Record("Clip.mp4", Source("https://example.com/first"));
        index.Record("Clip.mp4", Source("https://example.com/second"));

        Assert.Equal("https://example.com/second", index.Get("Clip.mp4")!.Url);
        Assert.Single(index.Entries);
    }

    /// <summary>
    /// Without this a rename loses the link silently and permanently - the same class of bug as a
    /// rename orphaning a pin, which ClipRename.Repoint exists to prevent.
    /// </summary>
    [Fact]
    public void FollowsARename()
    {
        var index = new ClipSourceIndex();
        index.Record("Old.mp4", Source());

        Assert.True(index.Rename("Old.mp4", "New.mp4"));
        Assert.Null(index.Get("Old.mp4"));
        Assert.NotNull(index.Get("New.mp4"));
    }

    [Fact]
    public void RenamingSomethingUnrecordedChangesNothing()
    {
        var index = new ClipSourceIndex();
        index.Record("Kept.mp4", Source());

        Assert.False(index.Rename("Missing.mp4", "New.mp4"));
        Assert.Single(index.Entries);
        Assert.NotNull(index.Get("Kept.mp4"));
    }

    [Theory]
    [InlineData(null, "New.mp4")]
    [InlineData("Old.mp4", null)]
    [InlineData("", "New.mp4")]
    public void RenameIgnoresBlankNames(string? from, string? to)
    {
        var index = new ClipSourceIndex();
        index.Record("Old.mp4", Source());

        Assert.False(index.Rename(from, to));
        Assert.NotNull(index.Get("Old.mp4"));
    }

    [Fact]
    public void ForgetsADeletedClip()
    {
        var index = new ClipSourceIndex();
        index.Record("Gone.mp4", Source());

        Assert.True(index.Remove("Gone.mp4"));
        Assert.Null(index.Get("Gone.mp4"));
        Assert.False(index.Remove("Gone.mp4"));
    }

    /// <summary>
    /// A hand-edited or partially-written file must cost a Details row, never the library.
    /// </summary>
    [Fact]
    public void RebuildingDropsBlankKeysRatherThanThrowing()
    {
        var index = ClipSourceIndex.From(new Dictionary<string, ClipSource>
        {
            ["Good.mp4"] = Source(),
            ["   "] = Source()
        });

        Assert.Single(index.Entries);
        Assert.NotNull(index.Get("Good.mp4"));
    }

    [Fact]
    public void RebuildingFromNothingGivesAnEmptyIndex()
    {
        Assert.Empty(ClipSourceIndex.From(null).Entries);
    }

    /// <summary>
    /// A round trip has to preserve the start and length too, not just the URL - they are what
    /// prefill the Edit dialog.
    /// </summary>
    [Fact]
    public void SurvivesARoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whallee-sources-{Guid.NewGuid():N}.json");
        try
        {
            var written = new ClipSourceIndex();
            written.Record("Clip.mp4", new ClipSource(
                "https://www.youtube.com/watch?v=abc",
                TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5),
                new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)));

            ClipSourceStore.Save(written, path);
            var read = ClipSourceStore.Load(path).Get("Clip.mp4");

            Assert.NotNull(read);
            Assert.Equal("https://www.youtube.com/watch?v=abc", read!.Url);
            Assert.Equal(TimeSpan.FromSeconds(90), read.Start);
            Assert.Equal(TimeSpan.FromMinutes(5), read.Length);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Load never throws: a corrupt or missing file costs one row in a dialog, never the ability
    /// to open the library.
    /// </summary>
    [Fact]
    public void LoadingSomethingUnreadableGivesAnEmptyIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whallee-sources-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Empty(ClipSourceStore.Load(path).Entries);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    [Fact]
    public void LoadingAMissingFileGivesAnEmptyIndex()
    {
        Assert.Empty(ClipSourceStore.Load(
            Path.Combine(Path.GetTempPath(), $"whallee-absent-{Guid.NewGuid():N}.json")).Entries);
    }
}
