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
#>
[CmdletBinding()]
param(
    [switch] $Shortcut
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\Whallee.App\Whallee.App.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Whallee\app'
$startupLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Whallee.lnk'

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

if ($Shortcut) {
    # No IconLocation is set on purpose: a shortcut with no explicit icon shows the target exe's
    # own embedded icon, so once <ApplicationIcon> is added to Whallee.App.csproj the shortcut
    # picks it up on the next publish with no change here.
    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($startupLnk)
    $lnk.TargetPath       = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.Description      = 'Whallee live wallpaper'
    $lnk.Save()

    Write-Host "Logon shortcut written to $startupLnk"
    Write-Host "To stop Whallee starting at logon, delete that file. Nothing else needs undoing."
}
