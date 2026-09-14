using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipGenresTests
{
    private static readonly string[] Existing = { "Cars", "Dystopia" };

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        Assert.Equal("Cars", ClipGenres.Normalise("  Cars  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalisingNothingGivesAnEmptyString(string? name)
    {
        Assert.Equal(string.Empty, ClipGenres.Normalise(name));
    }

    [Fact]
    public void AcceptsAFreshName()
    {
        Assert.Null(ClipGenres.Validate("Nature", Existing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankName(string? name)
    {
        Assert.Equal("Enter a name.", ClipGenres.Validate(name, Existing));
    }

    [Fact]
    public void RejectsAnOverlongName()
    {
        var tooLong = new string('x', ClipGenres.MaxNameLength + 1);

        var reason = ClipGenres.Validate(tooLong, Existing);

        Assert.NotNull(reason);
        Assert.Contains(ClipGenres.MaxNameLength.ToString(), reason);
    }

    [Fact]
    public void AcceptsANameExactlyAtTheCap()
    {
        Assert.Null(ClipGenres.Validate(new string('x', ClipGenres.MaxNameLength), Existing));
    }

    /// <summary>
    /// Windows users do not expect "Cars" and "cars" to be different things, and two tabs differing
    /// only in case would be unusable.
    /// </summary>
    [Theory]
    [InlineData("Cars")]
    [InlineData("cars")]
    [InlineData("  CARS  ")]
    public void RejectsADuplicateIgnoringCase(string name)
    {
        var reason = ClipGenres.Validate(name, Existing);

        Assert.NotNull(reason);
        Assert.Contains("already", reason);
    }

    /// <summary>
    /// THE CASE THAT MAKES currentName NECESSARY. Renaming "cars" to "Cars" is a real edit - fixing
    /// the capitalisation - and comparing against the whole list alone would reject it as a
    /// duplicate of itself.
    /// </summary>
    [Fact]
    public void AllowsRecasingAGenreToItsOwnName()
    {
        Assert.Null(ClipGenres.Validate("CARS", Existing, currentName: "Cars"));
    }

    [Fact]
    public void StillRejectsRenamingOntoADifferentGenre()
    {
        Assert.NotNull(ClipGenres.Validate("Dystopia", Existing, currentName: "Cars"));
    }

    [Theory]
    [InlineData("Cars", "cars", true)]
    [InlineData("Cars", "  CARS ", true)]
    [InlineData("Cars", "Dystopia", false)]
    [InlineData(null, null, true)]
    [InlineData("Cars", null, false)]
    public void SameComparesWithoutRegardToCaseOrPadding(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, ClipGenres.Same(a, b));
    }


    /// <summary>
    /// THE CASE THAT DECIDES THE WHOLE RULE. Dragging A to the far right of [A,B,C] must land it
    /// last. The index is read AFTER A is lifted out, so 2 means "after B and C". Reading it before
    /// the removal would give [B,A,C] and the tab would stop one short of where it was dropped.
    /// </summary>
    [Fact]
    public void MovesAGenreToAnAbsolutePositionOnTheRight()
    {
        var genres = new List<string> { "Cars", "Dystopia", "Nature" };

        Assert.Equal(2, ClipGenres.MoveTo(genres, "Cars", 2));
        Assert.Equal(new[] { "Dystopia", "Nature", "Cars" }, genres);
    }

    [Fact]
    public void MovesAGenreToAnAbsolutePositionOnTheLeft()
    {
        var genres = new List<string> { "Cars", "Dystopia", "Nature" };

        Assert.Equal(0, ClipGenres.MoveTo(genres, "Nature", 0));
        Assert.Equal(new[] { "Nature", "Cars", "Dystopia" }, genres);
    }

    /// <summary>
    /// Dropping past the last tab is the ordinary way to say "put it at the end", and a drag that
    /// ends outside the bar must not throw.
    /// </summary>
    [Theory]
    [InlineData(99)]
    [InlineData(-5)]
    public void ClampsAnOutOfRangeDropRatherThanThrowing(int index)
    {
        var genres = new List<string> { "Cars", "Dystopia" };

        var to = ClipGenres.MoveTo(genres, "Cars", index);

        Assert.InRange(to, 0, genres.Count - 1);
        Assert.Equal(2, genres.Count);
        Assert.Contains("Cars", genres);
    }

    /// <summary>Dropping a tab back where it started leaves the order untouched.</summary>
    [Fact]
    public void MovingToItsOwnPositionChangesNothing()
    {
        var genres = new List<string> { "Cars", "Dystopia", "Nature" };

        Assert.Equal(1, ClipGenres.MoveTo(genres, "Dystopia", 1));
        Assert.Equal(new[] { "Cars", "Dystopia", "Nature" }, genres);
    }

    [Fact]
    public void MovingSomethingAbsentToAPositionDoesNothing()
    {
        var genres = new List<string> { "Cars" };

        Assert.Equal(-1, ClipGenres.MoveTo(genres, "Missing", 0));
        Assert.Equal(new[] { "Cars" }, genres);
    }

    [Fact]
    public void MovesToAPositionByNameWithoutRegardToCase()
    {
        var genres = new List<string> { "Cars", "Dystopia" };

        Assert.Equal(1, ClipGenres.MoveTo(genres, "cars", 1));
        Assert.Equal(new[] { "Dystopia", "Cars" }, genres);
    }
}
