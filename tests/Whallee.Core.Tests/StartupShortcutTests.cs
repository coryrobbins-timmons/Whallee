using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class StartupShortcutTests
{
    [Fact]
    public void ShortcutLivesInTheStartupFolder() =>
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                     Path.GetDirectoryName(StartupShortcut.DefaultPath));

    [Fact]
    public void ShortcutIsNamedWhalleeLnk() =>
        Assert.Equal("Whallee.lnk", Path.GetFileName(StartupShortcut.DefaultPath));

    // The target is the INSTALLED copy, never the running exe - pointing autostart at a dev build
    // makes Whallee hold the DLLs you rebuild. See CLAUDE.md.
    [Fact]
    public void InstalledExeLivesUnderLocalAppDataWhalleeApp()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Whallee", "app");

        Assert.Equal(expected, Path.GetDirectoryName(StartupShortcut.InstalledExePath));
    }

    [Fact]
    public void InstalledExeIsNamedWhalleeExe() =>
        Assert.Equal("Whallee.exe", Path.GetFileName(StartupShortcut.InstalledExePath));

    // Neither path may sit under the repo: the whole point of the install layout is that the
    // autostarted copy is not the one being rebuilt.
    [Fact]
    public void NeitherPathPointsIntoTheRepositoryBinFolder()
    {
        Assert.DoesNotContain(@"\bin\", StartupShortcut.InstalledExePath);
        Assert.DoesNotContain(@"\bin\", StartupShortcut.DefaultPath);
    }
}
