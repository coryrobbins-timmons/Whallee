<#
    Headless startup smoke check for Whallee.exe.

    WHY THIS EXISTS: Task 1 shipped a NullReferenceException that killed the app inside
    InitializeComponent, before it drew a pixel. The build was clean and all 29 unit tests
    passed. Nothing automated noticed. This closes that gap.

    WHAT IT DOES AND DELIBERATELY DOES NOT DO: it starts the exe, waits, and asserts the
    process is still alive with no new Application-log error. It judges NOTHING visual.
    Whether the UI is *correct* remains the owner's call, by eye - see ruling 1.

    KNOWN LIMIT: on a crash, Windows Error Reporting can keep the process object alive past the
    dwell, so HasExited alone reads as healthy - the run that validated this script showed exactly
    that (alive=True, yet crashed). The Application-log check is therefore the load-bearing half,
    not a belt-and-braces extra. If a crash's event were ever written later than -DwellSeconds it
    would slip through as a PASS; raise the dwell if that is ever observed.

    VALIDATED red/green on 2026-08-31: FAILs (exit 1, full stack printed) against the pre-fix
    MainWindow, PASSes against the fixed one. A check never seen to fail is not a check.

    *** ONLY FOR BUILDS THAT DO NOT START THE WALLPAPER ENGINE. ***
    The owner approved this on 2026-08-31 for non-engine builds only. From Task 2 the app
    hosts WallpaperEngine and putting video on three live monitors unattended is exactly what
    ruling 1 forbids. -Force is required to run it anyway, and is the owner's to type.
#>
[CmdletBinding()]
param(
    [string] $Exe = "$PSScriptRoot\..\src\Whallee.App\bin\Debug\net8.0-windows\win-x64\Whallee.exe",
    [int]    $DwellSeconds = 3,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { Write-Host "FAIL: exe not found at $Exe" -ForegroundColor Red; exit 2 }

# Refuse to run once the engine is wired in, unless explicitly overridden.
$hostsEngine = Select-String -Path "$PSScriptRoot\..\src\Whallee.App\*.cs","$PSScriptRoot\..\src\Whallee.App\**\*.cs" `
                             -Pattern 'new\s+WallpaperEngine' -ErrorAction SilentlyContinue
if ($hostsEngine -and -not $Force) {
    Write-Host "REFUSED: this build constructs WallpaperEngine, so starting it would put video on" -ForegroundColor Yellow
    Write-Host "         the live monitors unattended. That is the owner's to run (ruling 1)." -ForegroundColor Yellow
    Write-Host "         Re-run with -Force only if the owner has said to." -ForegroundColor Yellow
    exit 3
}

$started = Get-Date
Write-Host "starting $(Split-Path $Exe -Leaf) ..."
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds $DwellSeconds

$alive = -not $p.HasExited
$errs = @()
try {
    $errs = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2; StartTime=$started} -ErrorAction Stop |
            Where-Object { $_.Message -match 'Whallee' }
} catch { }   # no matching events is the healthy case and throws

if ($alive) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit(5000) | Out-Null }

if ($alive -and $errs.Count -eq 0) {
    Write-Host "PASS: stayed alive $DwellSeconds s, no Application-log error." -ForegroundColor Green
    exit 0
}

Write-Host "FAIL: alive=$alive exitCode=$(if(-not $alive){$p.ExitCode}else{'n/a'}) errors=$($errs.Count)" -ForegroundColor Red
foreach ($e in $errs | Select-Object -First 2) { Write-Host "--- $($e.TimeCreated) $($e.ProviderName) ---"; Write-Host $e.Message }
exit 1
