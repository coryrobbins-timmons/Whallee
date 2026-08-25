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
        var resolver = ResolverOver(@"C:\clips\a.mp4");
        var assignment = new DisplayAssignment { Mode = DisplayMode.Pinned, PinnedPath = "gone.mp4" };

        Assert.Equal(@"C:\clips\a.mp4", resolver.Resolve(assignment, Library));
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
}
