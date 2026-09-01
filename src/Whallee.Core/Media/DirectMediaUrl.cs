using System.Globalization;
using System.Web;

namespace Whallee.Core.Media;

/// <summary>A resolved media URL plus the two facts strategy C needs from its query string.</summary>
public sealed record DirectMedia(string Url, long ContentLength, TimeSpan Duration);

/// <summary>
/// Reads <c>clen</c> and <c>dur</c> out of the URL that <c>yt-dlp -g</c> resolves.
/// </summary>
/// <remarks>
/// These two values are what make a bounded prefix fetch possible at all: clen sizes the request
/// plan and, with dur, converts bytes actually received into seconds of video for the ffmpeg trim.
/// </remarks>
public static class DirectMediaUrl
{
    public static DirectMedia? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var query = HttpUtility.ParseQueryString(uri.Query);

        if (!long.TryParse(query["clen"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                           out var clen) || clen <= 0)
            return null;

        if (!double.TryParse(query["dur"], NumberStyles.Float, CultureInfo.InvariantCulture,
                             out var dur) || dur <= 0)
            return null;

        return new DirectMedia(url, clen, TimeSpan.FromSeconds(dur));
    }

    /// <summary>First usable line. yt-dlp -g prints the video URL before the audio one.</summary>
    public static DirectMedia? ParseFirst(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var media = Parse(line.Trim());
            if (media is not null) return media;
        }
        return null;
    }
}
