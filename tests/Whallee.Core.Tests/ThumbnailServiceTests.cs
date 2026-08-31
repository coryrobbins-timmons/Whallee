using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ThumbnailServiceTests
{
    private static readonly DateTime T1 = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 8, 27, 12, 0, 1, DateTimeKind.Utc);

    // ---- cache key ---------------------------------------------------------

    [Fact]
    public void SamePathAndTimeGiveTheSameName() =>
        Assert.Equal(ThumbnailService.CacheFileName(@"C:\a\b.mp4", T1),
                     ThumbnailService.CacheFileName(@"C:\a\b.mp4", T1));

    [Fact]
    public void ADifferentModifiedTimeGivesADifferentName() =>
        Assert.NotEqual(ThumbnailService.CacheFileName(@"C:\a\b.mp4", T1),
                        ThumbnailService.CacheFileName(@"C:\a\b.mp4", T2));

    [Fact]
    public void ADifferentPathGivesADifferentName() =>
        Assert.NotEqual(ThumbnailService.CacheFileName(@"C:\a\b.mp4", T1),
                        ThumbnailService.CacheFileName(@"C:\a\c.mp4", T1));

    [Fact]
    public void PathCasingDoesNotChangeTheName() =>
        Assert.Equal(ThumbnailService.CacheFileName(@"C:\A\B.MP4", T1),
                     ThumbnailService.CacheFileName(@"c:\a\b.mp4", T1));

    [Fact]
    public void NameIsFilesystemSafeAndEndsInJpg()
    {
        var name = ThumbnailService.CacheFileName(@"C:\a\b c&d.mp4", T1);
        var invalid = Path.GetInvalidFileNameChars();

        Assert.EndsWith(".jpg", name);
        // Asserts the NAME contains no invalid character. The obvious spelling,
        // DoesNotContain(name, invalid.Select(c => c.ToString())), asserts something else entirely:
        // that a collection of single-character strings does not contain the whole name, which is
        // vacuously true and would pass for any name at all.
        Assert.DoesNotContain(name, c => invalid.Contains(c));
    }

    [Fact]
    public void CachePathCombinesFolderAndName()
    {
        var service = new ThumbnailService(@"C:\ff\ffmpeg.exe", @"C:\cache");

        Assert.Equal(
            Path.Combine(@"C:\cache", ThumbnailService.CacheFileName(@"C:\a\b.mp4", T1)),
            service.CachePathFor(@"C:\a\b.mp4", T1));
    }

    [Fact]
    public void DefaultCacheFolderSitsBesideTheConfig()
    {
        var folder = ThumbnailService.DefaultCacheFolder;

        Assert.EndsWith(Path.Combine("Whallee", "thumbs"), folder);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    // ---- seek offset -------------------------------------------------------
    // ffmpeg rejects "-ss 10%" outright ("Invalid duration for option ss"), so the percentage has
    // to be computed against a probed duration. This is that computation.

    [Fact]
    public void SeekOffsetIsTenPercentOfATypicalClip() =>
        // Compared in seconds to 3 decimal places, not as exact TimeSpans: the two routes to the
        // same value differ by a single 100 ns tick of floating-point rounding, and 3 dp is the
        // precision the offset is actually formatted to when it reaches ffmpeg.
        Assert.Equal(4.4735, ThumbnailService.SeekOffsetFor(TimeSpan.FromSeconds(44.735)).TotalSeconds, 3);

    [Fact]
    public void SeekOffsetFallsBackWhenDurationIsUnknown()
    {
        // ffprobe failed or returned nothing. A fixed second in is a safe guess.
        Assert.Equal(TimeSpan.FromSeconds(1), ThumbnailService.SeekOffsetFor(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), ThumbnailService.SeekOffsetFor(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void SeekOffsetIsCappedSoALongClipDoesNotSeekMinutesIn() =>
        Assert.Equal(10, ThumbnailService.SeekOffsetFor(TimeSpan.FromMinutes(30)).TotalSeconds, 3);

    [Fact]
    public void SeekOffsetStaysInsideAVeryShortClip()
    {
        // The failure this prevents: seeking past the end produces no frame at all, so the clip
        // silently gets no thumbnail.
        var duration = TimeSpan.FromSeconds(0.4);

        var offset = ThumbnailService.SeekOffsetFor(duration);

        Assert.True(offset < duration, $"offset {offset} must land inside a {duration} clip");
        Assert.True(offset > TimeSpan.Zero);
    }

    [Fact]
    public void SeekOffsetHasAFloorSoItClearsTheOpeningFrame() =>
        Assert.Equal(0.5, ThumbnailService.SeekOffsetFor(TimeSpan.FromSeconds(3)).TotalSeconds, 3);

    // ---- ffprobe discovery -------------------------------------------------

    [Fact]
    public void ProbeIsFoundBesideFfmpeg() =>
        Assert.Equal(@"C:\ff\bin\ffprobe.exe", ThumbnailService.ProbePathFor(@"C:\ff\bin\ffmpeg.exe"));

    [Fact]
    public void ProbePathIsNullWhenFfmpegPathHasNoDirectory() =>
        Assert.Null(ThumbnailService.ProbePathFor("ffmpeg.exe"));
}
