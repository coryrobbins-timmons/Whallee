using System.Reflection;

namespace Whallee.Core.Configuration;

/// <summary>
/// The logon shortcut: where it lives, what it must point at, and creating or removing it.
/// </summary>
/// <remarks>
/// A STARTUP-FOLDER SHORTCUT, NOT A SCHEDULED TASK. This machine is GPO-managed; both
/// Register-ScheduledTask and `schtasks /SC ONLOGON` are denied. The Startup folder is what works.
///
/// THE TARGET IS ALWAYS THE INSTALLED COPY, never the running exe. Pointing autostart at a dev
/// build under bin\ means Whallee is running at every logon holding the DLLs about to be rebuilt,
/// which turns MSB3021 lock failures into the default build experience. That is the entire reason
/// %LOCALAPPDATA%\Whallee\app exists - see CLAUDE.md.
///
/// THE SHORTCUT ON DISK IS THE SOURCE OF TRUTH, not WhalleeConfig.StartWithWindows. Deleting the
/// .lnk by hand is the documented way to turn autostart off, so Exists() is what the UI reads back
/// and the config flag follows it rather than leading it.
///
/// COM LATE BINDING IS VERIFIED, NOT ASSUMED. Type.GetTypeFromProgID + InvokeMember against
/// WScript.Shell was proved on 2026-09-03 to create a .lnk, read its target back and delete it,
/// under net8.0-windows in BOTH framework-dependent and self-contained builds - self-contained
/// being how Whallee actually ships. No IShellLink P/Invoke fallback is needed.
/// </remarks>
public static class StartupShortcut
{
    /// <summary>Where the logon shortcut lives.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Whallee.lnk");

    /// <summary>The installed copy the shortcut must point at.</summary>
    public static string InstalledExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "app", "Whallee.exe");

    /// <summary>Has the app been published to its install path yet?</summary>
    public static bool IsInstalled() => File.Exists(InstalledExePath);

    /// <summary>Is Whallee set to start at logon?</summary>
    public static bool Exists() => File.Exists(DefaultPath);

    /// <summary>
    /// Creates or overwrites the logon shortcut. Returns false when there is no installed copy to
    /// point at, which is not an error - it is the "not published yet" case the UI shows as a
    /// disabled checkbox with a reason.
    /// </summary>
    public static bool Create()
    {
        var target = InstalledExePath;
        if (!File.Exists(target)) return false;

        var progId = Type.GetTypeFromProgID("WScript.Shell")
                     ?? throw new InvalidOperationException("WScript.Shell is not registered.");

        var shell = Activator.CreateInstance(progId)
                    ?? throw new InvalidOperationException("Could not create WScript.Shell.");

        var link = progId.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                       new object[] { DefaultPath })
                   ?? throw new InvalidOperationException("CreateShortcut returned nothing.");

        var type = link.GetType();
        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link,
                          new object[] { target });
        type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                          new object[] { Path.GetDirectoryName(target)! });
        type.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                          new object[] { "Whallee live wallpaper" });

        // NO IconLocation, ON PURPOSE. A shortcut that names no icon shows its target exe's own
        // embedded icon, so the .ico built into Whallee.exe is picked up automatically and there
        // is nothing here to keep in sync when the icon changes.
        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

        return true;
    }

    /// <summary>
    /// Removes the logon shortcut. An absent shortcut is success: the desired end state is reached.
    /// </summary>
    public static void Remove()
    {
        if (File.Exists(DefaultPath)) File.Delete(DefaultPath);
    }
}
