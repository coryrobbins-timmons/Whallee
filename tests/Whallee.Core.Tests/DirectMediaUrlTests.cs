using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class DirectMediaUrlTests
{
    private const string Sample =
        "https://rr3---sn-x.googlevideo.com/videoplayback?expire=1&ei=x&clen=5330721038&dur=43200.000&mime=video%2Fmp4";

    [Fact]
    public void Parse_ReadsClenAndDur()
    {
        var media = DirectMediaUrl.Parse(Sample);

        Assert.NotNull(media);
        Assert.Equal(5_330_721_038, media!.ContentLength);
        Assert.Equal(TimeSpan.FromSeconds(43_200), media.Duration);
        Assert.Equal(Sample, media.Url);
    }

    [Fact]
    public void Parse_ReturnsNullWhenClenIsMissing()
    {
        Assert.Null(DirectMediaUrl.Parse("https://host/videoplayback?dur=100.0"));
    }

    [Fact]
    public void Parse_ReturnsNullWhenDurIsMissing()
    {
        Assert.Null(DirectMediaUrl.Parse("https://host/videoplayback?clen=1000"));
    }

    [Fact]
    public void Parse_ReturnsNullForRubbish()
    {
        Assert.Null(DirectMediaUrl.Parse("not a url"));
    }

    [Fact]
    public void ParseFirst_TakesTheFirstUsableLine()
    {
        // yt-dlp -g prints video URL then audio URL. The video track is the one worth fetching,
        // and the audio URL may not carry the same fields.
        var media = DirectMediaUrl.ParseFirst(new[] { "https://host/x?no=fields", Sample });

        Assert.Equal(5_330_721_038, media!.ContentLength);
    }

    [Fact]
    public void ParseFirst_ReturnsNullWhenNoLineIsUsable()
    {
        Assert.Null(DirectMediaUrl.ParseFirst(new[] { "junk", "also junk" }));
    }
}
