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

    [Fact]
    public void AssigningAGenreRecordsItAndListsIt()
    {
        var index = new ClipSourceIndex();
        index.Record("Clip.mp4", Source());

        index.Assign("Clip.mp4", "Cars");

        Assert.Equal("Cars", index.GenreOf("Clip.mp4"));
        Assert.Equal(new[] { "Cars" }, index.Genres);
    }

    /// <summary>
    /// A clip can be tagged before it has any recorded source - most of the library was added from
    /// local files and has no link at all. Assign must create the entry rather than do nothing.
    /// </summary>
    [Fact]
    public void TagsAClipThatHasNoRecordedSource()
    {
        var index = new ClipSourceIndex();

        index.Assign("Local.mp4", "Nature");

        Assert.Equal("Nature", index.GenreOf("Local.mp4"));

        // The entry now exists with an empty URL, which is what ClipDetailsDialog already renders
        // as "Not recorded" - tagging a clip must not invent a source for it.
        Assert.NotNull(index.Get("Local.mp4"));
        Assert.Equal(string.Empty, index.Get("Local.mp4")!.Url);
    }

    [Fact]
    public void AssigningNullUnassigns()
    {
        var index = new ClipSourceIndex();
        index.Assign("Clip.mp4", "Cars");

        index.Assign("Clip.mp4", null);

        Assert.Null(index.GenreOf("Clip.mp4"));
    }

    /// <summary>
    /// The genre survives losing its last clip: otherwise removing a clip from a genre would make
    /// the tab vanish underneath the owner mid-use.
    /// </summary>
    [Fact]
    public void AGenreOutlivesItsLastClip()
    {
        var index = new ClipSourceIndex();
        index.Assign("Clip.mp4", "Cars");

        index.Assign("Clip.mp4", null);

        Assert.Equal(new[] { "Cars" }, index.Genres);
    }

    [Fact]
    public void AssigningAgainMovesTheClipBetweenGenres()
    {
        var index = new ClipSourceIndex();
        index.Assign("Clip.mp4", "Cars");

        index.Assign("Clip.mp4", "Dystopia");

        Assert.Equal("Dystopia", index.GenreOf("Clip.mp4"));
        Assert.Equal(new[] { "Cars", "Dystopia" }, index.Genres);
        Assert.Empty(index.FileNamesIn("Cars"));
    }

    [Fact]
    public void AssigningAnExistingGenreDoesNotListItTwice()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "cars");

        Assert.Single(index.Genres);
    }

    [Fact]
    public void ListsTheClipsInAGenre()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Cars");
        index.Assign("C.mp4", "Nature");

        Assert.Equal(new[] { "A.mp4", "B.mp4" }, index.FileNamesIn("Cars").OrderBy(n => n));
    }

    /// <summary>Null lists the ungrouped clips, which is what the "Ungrouped" tab shows.</summary>
    [Fact]
    public void ListsUngroupedClipsForANullGenre()
    {
        var index = new ClipSourceIndex();
        index.Record("Untagged.mp4", Source());
        index.Assign("Tagged.mp4", "Cars");

        Assert.Equal(new[] { "Untagged.mp4" }, index.FileNamesIn(null));
    }

    /// <summary>Renaming must retag EVERY clip, not just the list entry.</summary>
    [Fact]
    public void RenamingAGenreRetagsAllOfItsClips()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Cars");
        index.Assign("C.mp4", "Nature");

        Assert.True(index.RenameGenre("Cars", "Driving"));

        Assert.Equal("Driving", index.GenreOf("A.mp4"));
        Assert.Equal("Driving", index.GenreOf("B.mp4"));
        Assert.Equal("Nature", index.GenreOf("C.mp4"));
    }

    /// <summary>A rename keeps the tab where it was; the owner did not ask to reorder.</summary>
    [Fact]
    public void RenamingKeepsThePositionInTheBar()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Dystopia");
        index.Assign("C.mp4", "Nature");

        index.RenameGenre("Dystopia", "Bleak");

        Assert.Equal(new[] { "Cars", "Bleak", "Nature" }, index.Genres);
    }

    [Fact]
    public void RenamingSomethingAbsentChangesNothing()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");

        Assert.False(index.RenameGenre("Missing", "Whatever"));
        Assert.Equal(new[] { "Cars" }, index.Genres);
    }

    /// <summary>
    /// THE ONE THAT MUST NEVER REGRESS: deleting a genre unassigns its clips and removes the tab.
    /// It does not remove the clips from the index and it never touches a file on disk.
    /// </summary>
    [Fact]
    public void DeletingAGenreOnlyUnassignsItsClips()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Nature");

        Assert.True(index.DeleteGenre("Cars"));

        Assert.Null(index.GenreOf("A.mp4"));
        Assert.NotNull(index.Get("A.mp4"));
        Assert.Equal(new[] { "Nature" }, index.Genres);
        Assert.Equal("Nature", index.GenreOf("B.mp4"));
    }

    [Fact]
    public void DeletingSomethingAbsentChangesNothing()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");

        Assert.False(index.DeleteGenre("Missing"));
        Assert.Equal(new[] { "Cars" }, index.Genres);
    }

    [Fact]
    public void MovesAGenreInTheBar()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Nature");

        Assert.Equal(0, index.MoveGenreTo("Nature", 0));
        Assert.Equal(new[] { "Nature", "Cars" }, index.Genres);
    }

    /// <summary>Dropping a dragged tab lands it where the indicator showed, not one short.</summary>
    [Fact]
    public void MovesAGenreToADroppedPosition()
    {
        var index = new ClipSourceIndex();
        index.Assign("A.mp4", "Cars");
        index.Assign("B.mp4", "Nature");
        index.Assign("C.mp4", "Dystopia");

        Assert.Equal(2, index.MoveGenreTo("Cars", 2));
        Assert.Equal(new[] { "Nature", "Dystopia", "Cars" }, index.Genres);
    }

    /// <summary>
    /// A genre no longer has to be born from a clip. Before 2026-09-14 the only way to create one
    /// was to tag something, so the owner had to find a clip before setting up a category.
    /// </summary>
    [Fact]
    public void CreatesAGenreWithNoClipsInIt()
    {
        var index = new ClipSourceIndex();

        Assert.True(index.AddGenre("Cars"));
        Assert.Equal(new[] { "Cars" }, index.Genres);
        Assert.Empty(index.FileNamesIn("Cars"));
    }

    [Fact]
    public void AnEmptyGenreCanThenBeTagged()
    {
        var index = new ClipSourceIndex();
        index.AddGenre("Cars");

        index.Assign("A.mp4", "Cars");

        Assert.Single(index.Genres);
        Assert.Equal(new[] { "A.mp4" }, index.FileNamesIn("Cars"));
    }

    /// <summary>
    /// A duplicate is a no-op rather than a failure: the caller has already validated the name, and
    /// reaching the same one twice should leave one genre rather than two.
    /// </summary>
    [Theory]
    [InlineData("Cars")]
    [InlineData("cars")]
    [InlineData("  CARS  ")]
    public void AddingAGenreThatExistsChangesNothing(string again)
    {
        var index = new ClipSourceIndex();
        index.AddGenre("Cars");

        Assert.False(index.AddGenre(again));
        Assert.Equal(new[] { "Cars" }, index.Genres);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesToCreateABlankGenre(string name)
    {
        var index = new ClipSourceIndex();

        Assert.False(index.AddGenre(name));
        Assert.Empty(index.Genres);
    }

    /// <summary>An empty genre must survive being written and read back, or the tab vanishes.</summary>
    [Fact]
    public void AnEmptyGenreSurvivesARoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whallee-empty-{Guid.NewGuid():N}.json");
        try
        {
            var written = new ClipSourceIndex();
            written.AddGenre("Cars");

            ClipSourceStore.Save(written, path);

            Assert.Equal(new[] { "Cars" }, ClipSourceStore.Load(path).Genres);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>A rename of the FILE must carry the genre across, like the source link.</summary>
    [Fact]
    public void RenamingAFileKeepsItsGenre()
    {
        var index = new ClipSourceIndex();
        index.Assign("Old.mp4", "Cars");

        index.Rename("Old.mp4", "New.mp4");

        Assert.Equal("Cars", index.GenreOf("New.mp4"));
    }

    [Fact]
    public void GenresAndAssignmentsSurviveARoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whallee-genres-{Guid.NewGuid():N}.json");
        try
        {
            var written = new ClipSourceIndex();
            written.Record("Clip.mp4", Source());
            written.Assign("Clip.mp4", "Cars");
            written.Assign("Other.mp4", "Nature");
            written.MoveGenreTo("Nature", 0);

            ClipSourceStore.Save(written, path);
            var read = ClipSourceStore.Load(path);

            Assert.Equal(new[] { "Nature", "Cars" }, read.Genres);
            Assert.Equal("Cars", read.GenreOf("Clip.mp4"));
            Assert.Equal("https://www.youtube.com/watch?v=abc", read.Get("Clip.mp4")!.Url);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>
    /// THE MIGRATION CHECK. sources.json was a BARE MAP before genres existed, and the owner already
    /// has recorded links in that shape. Discarding them would look exactly like a bug in Details.
    /// </summary>
    [Fact]
    public void LoadsTheOldBareMapFormatAndKeepsTheLinks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whallee-old-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "City Skyline.mp4": {
                    "Url": "https://www.youtube.com/watch?v=old",
                    "Start": "00:01:30",
                    "Length": "00:00:30",
                    "ImportedUtc": "2026-09-11T12:00:00+00:00"
                  }
                }
                """);

            var read = ClipSourceStore.Load(path);

            Assert.Equal("https://www.youtube.com/watch?v=old", read.Get("City Skyline.mp4")!.Url);
            Assert.Empty(read.Genres);
            Assert.Null(read.GenreOf("City Skyline.mp4"));
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
