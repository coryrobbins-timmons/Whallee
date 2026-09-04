namespace Whallee.Core.Playback;

/// <summary>
/// What Windows reports the user is doing — the QUERY_USER_NOTIFICATION_STATE values.
/// </summary>
/// <remarks>
/// THE NUMBERS ARE THE WIN32 VALUES and must not be renumbered: PlaybackGovernor receives them
/// straight out of SHQueryUserNotificationState with no translation layer.
/// </remarks>
public enum UserNotificationState
{
    /// <summary>Machine locked, or a screensaver is running.</summary>
    NotPresent = 1,

    /// <summary>A full-screen application is running.</summary>
    Busy = 2,

    /// <summary>A full-screen exclusive Direct3D application — typically a game.</summary>
    RunningD3dFullScreen = 3,

    /// <summary>Presentation mode.</summary>
    PresentationMode = 4,

    /// <summary>An ordinary desktop, accepting notifications.</summary>
    AcceptsNotifications = 5,

    /// <summary>Notification quiet hours. NOT a fullscreen app.</summary>
    QuietTime = 6,

    /// <summary>A full-screen modern app is running.</summary>
    App = 7
}

/// <summary>
/// Decides whether wallpaper playback should be paused.
/// </summary>
/// <remarks>
/// PURE, AND THE WHOLE TESTABLE SURFACE OF THIS FEATURE. Everything around it - the timer, the two
/// P/Invokes, the pipe writes - is outside standing ruling 3's boundary, so the rules live here
/// where they can be wrong in a way tests will catch.
/// </remarks>
public static class PauseDecision
{
    /// <summary>Would this state hide the wallpaper from the user?</summary>
    /// <remarks>
    /// QuietTime is deliberately absent: it means notification quiet hours, not a fullscreen app.
    ///
    /// NotPresent - locked or screensaver - IS included, on the owner's decision of 2026-09-04.
    /// Nothing is visible behind a lock screen, so decoding three video streams there is free
    /// waste, and it matters most overnight on battery.
    ///
    /// A MERELY MAXIMIZED WINDOW IS NOT ANY OF THESE. Windows offers no system-wide way to ask "is
    /// the wallpaper covered?" - only "is something fullscreen?" - so a maximized window still
    /// leaves playback running even though nothing is visible. That gap is what the deferred
    /// per-display work would close; it is a limitation, not an oversight.
    /// </remarks>
    public static bool HidesWallpaper(UserNotificationState state) => state switch
    {
        UserNotificationState.Busy => true,
        UserNotificationState.RunningD3dFullScreen => true,
        UserNotificationState.PresentationMode => true,
        UserNotificationState.App => true,
        UserNotificationState.NotPresent => true,

        // FAIL SAFE TOWARD PLAYING. AcceptsNotifications, QuietTime, and any value a future
        // Windows build invents all land here. A wallpaper that keeps playing is today's
        // behaviour; one frozen by an unrecognised number is a bug nobody could diagnose.
        _ => false
    };

    /// <summary>True when playback should be paused right now.</summary>
    /// <remarks>
    /// The two rules are independent and either alone is enough. Each is gated on its own setting,
    /// so unticking one cannot leave the other silently reacting to it.
    /// </remarks>
    public static bool ShouldPause(
        UserNotificationState state, bool onBattery,
        bool pauseOnFullscreen, bool pauseOnBattery)
    {
        if (pauseOnFullscreen && HidesWallpaper(state)) return true;
        if (pauseOnBattery && onBattery) return true;

        return false;
    }
}
