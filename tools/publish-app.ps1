<#
.SYNOPSIS
    Publishes Whallee to its stable local install path and, optionally, refreshes the logon shortcut.

.DESCRIPTION
    The autostarted copy of Whallee deliberately does NOT live in the repo's bin/ folder:

      * bin/ is git-ignored, so `git clean -fdx` would delete it and autostart would silently
        stop working at the next logon, with nothing to point at the cause.
      * The repo sits under OneDrive-redirected Documents. OneDrive has a history on this machine
        of locking files and of turning them into cloud placeholders. An executable that Windows
        launches at logon is the last thing that should depend on a sync client.
      * Most practically: if the copy Windows autostarts were the same one you rebuild, Whallee
        would be running at every logon holding the very DLLs you are about to overwrite, and
        MSB3021/MSB3027 lock failures would become the default build experience.

    So the published copy under %LOCALAPPDATA% is what runs, and bin/ stays purely for development.
    Changes reach the autostarted copy only when you re-run this script.

.PARAMETER Shortcut
    Also create or refresh the Startup-folder shortcut that launches Whallee at logon.
    Registering a scheduled task is NOT an option here - this is a GPO-managed machine where
    Register-ScheduledTask and `schtasks /SC ONLOGON` are both denied.

.EXAMPLE
    .\tools\publish-app.ps1
    .\tools\publish-app.ps1 -Shortcut
    .\tools\publish-app.ps1 -Shortcut -DesktopShortcut
#>
[CmdletBinding()]
param(
    [switch] $Shortcut,
    [switch] $DesktopShortcut
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\Whallee.App\Whallee.App.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Whallee\app'
$startupLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Whallee.lnk'

# GetFolderPath, not "$env:USERPROFILE\Desktop". This machine's Desktop is OneDrive-redirected, so
# the literal path does not exist and a shortcut written there would land somewhere nobody looks.
$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Whallee.lnk'

# REFUSE rather than kill. Whallee owns windows re-parented into Explorer's desktop; terminating it
# from outside is exactly how you end up with orphaned host windows on the wallpaper. Exiting via
# the tray icon is the only correct way to stop it.
$running = Get-Process Whallee -ErrorAction SilentlyContinue
if ($running) {
    throw "Whallee is running (pid $($running.Id -join ', ')). Exit it via the tray icon first, " +
          "then re-run. Do not kill the process - it owns the wallpaper windows."
}

Write-Host "Publishing to $installDir ..."
dotnet publish $project -c Release -o $installDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $installDir 'Whallee.exe'
if (-not (Test-Path $exe)) { throw "Publish reported success but $exe is missing." }

$sizeMb = [math]::Round(((Get-ChildItem $installDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Published $exe ($sizeMb MB)."

# NO IconLocation IS EVER SET, on purpose. A shortcut that names no icon shows its target exe's own
# embedded icon, so the .ico built into Whallee.exe is picked up automatically and there is nothing
# here to keep in sync when the icon changes.
function Write-WhalleeShortcut {
    param([string] $Path)

    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($Path)
    $lnk.TargetPath       = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.Description      = 'Whallee live wallpaper'
    $lnk.Save()
}

if ($Shortcut) {
    Write-WhalleeShortcut -Path $startupLnk

    Write-Host "Logon shortcut written to $startupLnk"
    Write-Host "To stop Whallee starting at logon, delete that file. Nothing else needs undoing."
}

if ($DesktopShortcut) {
    Write-WhalleeShortcut -Path $desktopLnk

    Write-Host "Desktop shortcut written to $desktopLnk"
}
