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

    [Fact]
    public void MovesAGenreLeftAndRight()
    {
        var genres = new List<string> { "Cars", "Dystopia", "Nature" };

        Assert.Equal(0, ClipGenres.Move(genres, "Dystopia", -1));
        Assert.Equal(new[] { "Dystopia", "Cars", "Nature" }, genres);

        Assert.Equal(2, ClipGenres.Move(genres, "Cars", +1));
        Assert.Equal(new[] { "Dystopia", "Nature", "Cars" }, genres);
    }

    /// <summary>
    /// The ends are NO-OPS, not errors: the menu offers "Move left" on the first tab, and throwing
    /// there would mean the caller had to know the position to avoid crashing.
    /// </summary>
    [Fact]
    public void MovingPastEitherEndDoesNothing()
    {
        var genres = new List<string> { "Cars", "Dystopia" };

        Assert.Equal(0, ClipGenres.Move(genres, "Cars", -1));
        Assert.Equal(1, ClipGenres.Move(genres, "Dystopia", +1));
        Assert.Equal(new[] { "Cars", "Dystopia" }, genres);
    }

    [Fact]
    public void MovingSomethingAbsentDoesNothing()
    {
        var genres = new List<string> { "Cars" };

        Assert.Equal(-1, ClipGenres.Move(genres, "Missing", +1));
        Assert.Equal(new[] { "Cars" }, genres);
    }

    [Fact]
    public void MovesByNameWithoutRegardToCase()
    {
        var genres = new List<string> { "Cars", "Dystopia" };

        Assert.Equal(1, ClipGenres.Move(genres, "cars", +1));
        Assert.Equal(new[] { "Dystopia", "Cars" }, genres);
    }
}
