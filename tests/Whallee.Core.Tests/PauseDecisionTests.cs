using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class PauseDecisionTests
{
    // Both rules on, unless a test says otherwise - most cases are about the STATE.
    private static bool Pause(UserNotificationState state, bool onBattery = false) =>
        PauseDecision.ShouldPause(state, onBattery, pauseOnFullscreen: true, pauseOnBattery: true);

    // ---- which states hide the wallpaper -------------------------------------

    [Fact]
    public void AFullScreenAppPauses() => Assert.True(Pause(UserNotificationState.Busy));

    [Fact]
    public void AFullScreenGamePauses() =>
        Assert.True(Pause(UserNotificationState.RunningD3dFullScreen));

    [Fact]
    public void PresentationModePauses() =>
        Assert.True(Pause(UserNotificationState.PresentationMode));

    [Fact]
    public void AFullScreenModernAppPauses() => Assert.True(Pause(UserNotificationState.App));

    // Owner's decision 2026-09-04: nothing is visible behind a lock screen, so playing three
    // videos there is free waste - it matters most overnight on battery.
    [Fact]
    public void ALockedMachineOrScreensaverPauses() =>
        Assert.True(Pause(UserNotificationState.NotPresent));

    [Fact]
    public void AnOrdinaryDesktopDoesNotPause() =>
        Assert.False(Pause(UserNotificationState.AcceptsNotifications));

    // Quiet time means notification quiet hours, NOT a fullscreen app. Pausing then would be
    // surprising and is deliberately excluded.
    [Fact]
    public void QuietTimeDoesNotPause() => Assert.False(Pause(UserNotificationState.QuietTime));

    // FAIL SAFE TOWARD PLAYING. A future Windows build could return a value this enum does not
    // know; a wallpaper that keeps playing is the current behaviour, one frozen by an unrecognised
    // number is a bug the user cannot diagnose.
    [Fact]
    public void AnUnrecognisedStateDoesNotPause() =>
        Assert.False(Pause((UserNotificationState)99));

    // ---- how the two rules combine -------------------------------------------

    [Fact]
    public void BothRulesOffNeverPauses() =>
        Assert.False(PauseDecision.ShouldPause(
            UserNotificationState.RunningD3dFullScreen, onBattery: true,
            pauseOnFullscreen: false, pauseOnBattery: false));

    [Fact]
    public void TheBatteryRuleAlonePausesOnBattery() =>
        Assert.True(PauseDecision.ShouldPause(
            UserNotificationState.AcceptsNotifications, onBattery: true,
            pauseOnFullscreen: false, pauseOnBattery: true));

    [Fact]
    public void TheBatteryRuleDoesNotPauseOnMains() =>
        Assert.False(PauseDecision.ShouldPause(
            UserNotificationState.AcceptsNotifications, onBattery: false,
            pauseOnFullscreen: false, pauseOnBattery: true));

    // The fullscreen rule must not quietly react to battery, or unticking "pause on battery"
    // would appear not to work.
    [Fact]
    public void TheFullscreenRuleIgnoresBattery() =>
        Assert.False(PauseDecision.ShouldPause(
            UserNotificationState.AcceptsNotifications, onBattery: true,
            pauseOnFullscreen: true, pauseOnBattery: false));

    [Fact]
    public void TheFullscreenRuleAlonePausesForAGame() =>
        Assert.True(PauseDecision.ShouldPause(
            UserNotificationState.RunningD3dFullScreen, onBattery: false,
            pauseOnFullscreen: true, pauseOnBattery: false));

    [Fact]
    public void BothRulesTriggeringStillPauses() =>
        Assert.True(PauseDecision.ShouldPause(
            UserNotificationState.RunningD3dFullScreen, onBattery: true,
            pauseOnFullscreen: true, pauseOnBattery: true));

    // ---- HidesWallpaper on its own -------------------------------------------

    [Fact]
    public void HidesWallpaperIsTrueForFullscreenStates() =>
        Assert.True(PauseDecision.HidesWallpaper(UserNotificationState.Busy));

    [Fact]
    public void HidesWallpaperIsFalseForAnOrdinaryDesktop() =>
        Assert.False(PauseDecision.HidesWallpaper(UserNotificationState.AcceptsNotifications));
}
