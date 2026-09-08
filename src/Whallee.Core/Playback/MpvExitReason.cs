namespace Whallee.Core.Playback;

/// <summary>
/// Turns an mpv exit code into something worth putting in front of a person.
/// </summary>
/// <remarks>
/// Split out of <see cref="MpvHost"/> when the liveness wait moved to the engine: it is the one
/// piece of that path with no Process, no window handle and no timing in it, so it is the piece
/// that can actually be tested. Everything else about launching mpv is verified by looking at three
/// monitors.
/// </remarks>
public static class MpvExitReason
{
    /// <summary>
    /// A one-line explanation of <paramref name="exitCode"/>, naming the clip it applies to.
    /// </summary>
    /// <remarks>
    /// The codes come from mpv itself: 1 is an initialisation failure, 2 and 3 both mean it could
    /// not play what it was given (a missing or unreadable file, and an unsupported or corrupt
    /// stream). Anything else is reported verbatim rather than guessed at — a wrong explanation is
    /// worse than a bare number, because it sends the reader somewhere else entirely.
    ///
    /// Exit code 0 is included deliberately: mpv exiting cleanly and immediately still means the
    /// display is not playing anything, so it is a failure from the wallpaper's point of view even
    /// though it is a success from the process's.
    /// </remarks>
    public static string Describe(int exitCode, string videoPath) => exitCode switch
    {
        1 => $"mpv failed to initialise ({videoPath})",
        2 or 3 => $"mpv could not play the file ({videoPath})",
        0 => $"mpv exited immediately ({videoPath})",
        _ => $"mpv exited with code {exitCode} ({videoPath})"
    };
}
