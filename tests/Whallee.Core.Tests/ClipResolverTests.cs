using Whallee.Core;
using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipResolverTests
{
    private const string Library = @"C:\clips";

    private static ClipResolver ResolverOver(params string[] files) =>
        new(_ => files, new Random(1234));

    [Fact]
    public void Pinned_ExistingFile_ReturnsFullPath()
    {
        var resolver = ResolverOver(@"C:\clips\a.mp4", @"C:\clips\b.mp4");
        var assignment = new DisplayAssignment { Mode = DisplayMode.Pinned, PinnedPath = "b.mp4" };

        Assert.Equal(@"C:\clips\b.mp4", resolver.Resolve(assignment, Library));
    }

    [Fact]
    public void Pinned_MissingFile_FallsBackToAnotherClip()
    {
        var files = new[] { @"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\c.mp4" };
        var random = new LastIndexRandom();
        var resolver = new ClipResolver(_ => files, random);
        var assignment = new DisplayAssignment { Mode = DisplayMode.Pinned, PinnedPath = "gone.mp4" };

        var result = resolver.Resolve(assignment, Library);

        // Proves the fallback goes through the injected Random over the whole candidate
        // list, rather than returning candidates[0] or the pinned path.
        Assert.Equal(@"C:\clips\c.mp4", result);
        Assert.Equal(3, random.RequestedMax);
    }

    [Fact]
    public void Shuffle_ReturnsOneOfTheAvailableFiles()
    {
        var resolver = ResolverOver(@"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\c.mp4");
        var assignment = new DisplayAssignment { Mode = DisplayMode.Shuffle };

        var result = resolver.Resolve(assignment, Library);

        Assert.Contains(result, new[] { @"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\c.mp4" });
    }

    [Fact]
    public void EmptyLibrary_ReturnsNull()
    {
        var resolver = ResolverOver();
        var assignment = new DisplayAssignment { Mode = DisplayMode.Shuffle };

        Assert.Null(resolver.Resolve(assignment, Library));
    }

    [Fact]
    public void Shuffle_IsDeterministicForAGivenSeed()
    {
        var files = new[] { @"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\c.mp4" };
        var first = new ClipResolver(_ => files, new Random(99));
        var second = new ClipResolver(_ => files, new Random(99));
        var assignment = new DisplayAssignment { Mode = DisplayMode.Shuffle };

        Assert.Equal(first.Resolve(assignment, Library), second.Resolve(assignment, Library));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Pinned_WithNoUsablePinnedPath_FallsBackToARandomPick(string? pinnedPath)
    {
        var files = new[] { @"C:\clips\a.mp4", @"C:\clips\b.mp4", @"C:\clips\c.mp4" };
        var random = new LastIndexRandom();
        var resolver = new ClipResolver(_ => files, random);
        var assignment = new DisplayAssignment { Mode = DisplayMode.Pinned, PinnedPath = pinnedPath };

        var result = resolver.Resolve(assignment, Library);

        // Mode says Pinned but there is nothing to pin to. The display must still get a clip, and
        // it must come from the injected Random over the whole list — not from candidates[0], and
        // not from a Path.Combine against a null.
        Assert.Equal(@"C:\clips\c.mp4", result);
        Assert.Equal(3, random.RequestedMax);
    }

    [Fact]
    public void Pinned_AbsolutePathInsideTheLibrary_Resolves()
    {
        var resolver = ResolverOver(@"C:\clips\a.mp4", @"C:\clips\b.mp4");
        var assignment = new DisplayAssignment
        {
            Mode = DisplayMode.Pinned,
            PinnedPath = @"C:\clips\b.mp4"
        };

        Assert.Equal(@"C:\clips\b.mp4", resolver.Resolve(assignment, Library));
    }

    [Fact]
    public void Pinned_AbsolutePathOutsideTheLibrary_ThrowsInsteadOfPickingAtRandom()
    {
        var resolver = ResolverOver(@"C:\clips\a.mp4", @"C:\clips\b.mp4");
        var assignment = new DisplayAssignment
        {
            Mode = DisplayMode.Pinned,
            PinnedPath = @"D:\elsewhere\other.mp4"
        };

        // Path.Combine ignores the library folder when the second argument is rooted, so this used
        // to match nothing and silently fall back to a random clip — indistinguishable from
        // "pinning is broken".
        var ex = Assert.Throws<ArgumentException>(() => resolver.Resolve(assignment, Library));
        Assert.Contains("outside the library folder", ex.Message);
    }

    /// <summary>Forces selection of the last candidate, and records the range it was asked for.</summary>
    private sealed class LastIndexRandom : Random
    {
        public int? RequestedMax { get; private set; }

        public override int Next(int maxValue)
        {
            RequestedMax = maxValue;
            return maxValue - 1;
        }
    }
}
