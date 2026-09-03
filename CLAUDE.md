# CLAUDE.md

Guidance for Claude Code working in this repository.

## Records that exist here

- `.superpowers/sdd/<plan-name>/progress.md` — the **authoritative record**: every task, every
  review finding, every ruling, and why. Read its tail when resuming work on that plan.
- `HANDOFF.md` — a session-to-session briefing, written when a session ends mid-flight. **Do not
  read it unless the owner asks you to** (e.g. "pick up from the handoff"). It is a snapshot as of
  the date at its top, so it goes stale; where it and the ledger disagree, **the ledger wins**.

> **The SDD ledgers are git-ignored.** Anything in them that is durably true — an environment fact,
> a measured constraint, a settled ruling — belongs in this file instead, or it dies with the
> workspace. Follow-up work belongs in the section at the bottom of this file for the same reason.

## Known follow-ups

Recorded 2026-09-02. Items 1 and 3 were done the same day (commit below); item 2 remains open.
Plan 3 (YouTube import) is complete, reviewed, and confirmed working end to end; these are
improvements, not defects.

1. ~~Redesign the URL import dialog's explanatory text.~~ **Done 2026-09-02.** The dialog now
   predicts nothing: the ~60 s limit is reported after the import, where it is a fact rather than an
   estimate, because knowing it beforehand cannot change what the user does. The start-time field
   was **kept**, not dropped - it is inert on this network but the 403s look specific to the
   corporate egress IP, so it may work from home. Design:
   `..\Visual Studio Code\Claude Code\docs\superpowers\specs\2026-09-02-url-dialog-redesign-design.md`.
2. **Import latency is process launches, not bandwidth.** Measured from a real import: the 22 MiB
   download took ~0.5 s; `yt-dlp` resolving the URL took ~6 s, on top of a separate probe at *Look
   up*. The feature launches `yt-dlp` **twice** for overlapping information - `--print` for
   metadata, then `-g` for the direct URL. Worth investigating whether one invocation can serve
   both. This is a real change to the probe/resolve split, not a tweak. **Still open.**
3. ~~A fully-satisfied strategy C import with a non-zero start time is silent about starting from
   0:00.~~ **Done 2026-09-02**, and promoted from optional to essential by item 1: with the dialog
   explaining nothing in advance, the result message became the only surface on which the user could
   ever learn a start time was ignored.

## What this is

**Whallee** — a live-wallpaper app for Windows 11. It plays looping local video as the desktop
wallpaper across three monitors: locate `WorkerW` (the window Explorer paints wallpaper into),
create one borderless child window per display, parent them into it, and run `mpv.exe --wid=<hwnd>`
in each.

`net8.0-windows`, x64. Branch `main` — the owner has consented to committing directly to main on
this greenfield repo. No git remote.

| Project | What it is |
|---|---|
| `src/Whallee.Core` | The engine. **No UI framework references, ever.** |
| `src/Whallee.Harness` | Console harness. The headless acceptance test for the engine. |
| `tests/Whallee.Core.Tests` | Unit tests for pure logic only. |

Plans and the design spec live in the sibling workspace at
`..\Visual Studio Code\Claude Code\docs\superpowers\`.

## The one rule that dominates this codebase

`HostWindow.Attach` uses `SetParent` to move a window this process owns into Explorer's desktop
hierarchy. **Cross-thread parenting attaches the two threads' input queues.** From that moment, a
thread that owns host windows and stops pumping messages freezes *the user's whole desktop* —
desktop icons stop responding to clicks and the wait cursor appears system-wide.

**Four separate bugs have come from violating this**, each found a different way:

| Blocking call | Where | Found by |
|---|---|---|
| `Console.ReadLine()` | harness `one` verb | the owner, looking at the screen |
| `Process.WaitForExit(1000)` ×3 | `WallpaperEngine.Start()` | a code review |
| `Thread.Sleep(500)` | `DesktopLayer.Locate()` retry | reviewing the fix for the above |
| `Console.WriteLine` | shell-watcher callbacks | a whole-branch review |

`Whallee.Core.Desktop.MessagePump` exists solely to satisfy the rule:

- `RunUntil(Task)` — blocking `GetMessageW` pump. **Console harness only.** Never use it in a UI app
  that already pumps (WPF, WinForms, WinUI) — it would fight the framework's own message loop.
- `WaitForExit(Process, TimeSpan)` — pumping replacement for `Process.WaitForExit`.
- `Sleep(TimeSpan)` — pumping replacement for `Thread.Sleep`.

**Ask "does this block the pump thread?" of every line you add.** Nothing slow, blocking, locking,
or I/O-bound may run on a thread that owns host windows. Background it and marshal the result back.

Note the two `MsgWaitForMultipleObjectsEx` loops in `MessagePump` map a `0` return to **opposite**
meanings — with `nCount = 1` it means the handle signalled; with `nCount = 0` it means input is
available. Do not "unify" them.

## Standing rulings from the owner

1. **The owner runs the harness verbs and the app; you never do.** `one` and `all` block while
   holding video on three live monitors, and correctness is judged by looking at the screen. Hand
   over the exact command and wait for their report.
2. **Never kill or restart Explorer**, and never change display settings. That is the owner's to do,
   via Task Manager → Windows Explorer → Restart.
3. **Testing boundary is deliberate.** No unit tests for Win32, `SetParent`, window handles, mpv
   playback, or XAML — mocking Win32 proves nothing about whether Windows accepts the call. Unit
   tests cover pure logic in `Whallee.Core` only. The plan governs this; do not relitigate it.
4. **mpv liveness window is 1 second.** Shortened from 5 deliberately. Do not revert.

## Build and test

```bash
dotnet build -v q --nologo
dotnet test --nologo -v q
```

Both must be clean. Safe verbs that print and exit — use these freely:

```bash
.\src\Whallee.Harness\bin\Debug\net8.0-windows\Whallee.Harness.exe displays
.\src\Whallee.Harness\bin\Debug\net8.0-windows\Whallee.Harness.exe workerw
```

**`one` and `all` are not yours to run.** See ruling 1.

## Environment facts

- Three displays: `\\.\DISPLAY6` 1920×1080 @ (0,0) **primary**; `\\.\DISPLAY5` 1920×1080 @ (1920,0);
  `\\.\DISPLAY1` 1920×1200 @ (3840, **−124**). **Display numbers do not match physical order** —
  `DISPLAY1` is the rightmost panel. The virtual-screen origin has a negative Y.
- `DISPLAY5` and `DISPLAY6` are near-identical BenQ panels differing by one character of hardware id
  and their UID, so `deviceId`-first matching is load-bearing, not theoretical.
- **WorkerW, Progman and ShellDefView handles change constantly** — observed moving three times in a
  single session. Always re-locate; never cache.
- mpv: `C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe`
- Config: `%LOCALAPPDATA%\Whallee\config.json`, **PascalCase keys**. Content: `<repo>\Library`
  (git-ignored — video files must not enter git).
- **YouTube downloads are capped at roughly 60 SECONDS OF VIDEO CONTENT per video — a duration, not
  a byte count.** Measured five times across bitrates spanning 21×; the byte counts spanned **24×**
  (6 MiB → 144 MiB) while the durations stayed flat at 51–60 s. Bracketing the first four
  measurements between the bytes served and what the next chunk would have completed, the intervals
  intersect at **59.8–61.3 s**.

  | Bitrate | Bytes served | Seconds of content |
  |---|---|---|
  | ~1.0 Mbps | 6 MiB | 51.0 |
  | ~3.8 Mbps | 26 MiB | 56.9 |
  | ~9.0 Mbps | 64 MiB | 59.8 |
  | ~21 Mbps (4K 60fps) | 144 MiB | 57 |

  The last row is from a real import on 2026-09-02, not a `budget` run, so it is a whole-pipeline
  confirmation rather than a bracketed measurement.
  - **The allowance is PER VIDEO, not per IP.** A second video requested immediately after a first
    had been refused received a full fresh allowance on the same connection.
  - **Lower bitrate buys no extra length** — the same ~60 s in fewer bytes. Do not choose a format
    expecting a longer clip. It does buy **bandwidth**, though, and at 4K that is not trivial: the
    144 MiB run above produced a 57-second loop that 1080p would have delivered in roughly 10 MiB.
    On a corporate connection, a dozen 4K imports is a couple of gigabytes for no visible gain at
    wallpaper size. Format selection is worth revisiting on those grounds, not on length.
  - **Do not model this as a byte ceiling.** The design spec's original 6 MiB figure was this same
    60 s allowance measured on a ~1 Mbps stream, and building on it produced a prediction that
    understated reality by 4×. Retuning a byte constant to a larger value repeats the mistake.
  - An open-ended request is answered **403**; an explicit `Range: bytes=N-M` is answered **206**.
    That difference is the entire mechanism, not an optimisation.
- Four clips in the library. **Four clips across three displays means two displays showing the same
  clip is expected, not a bug.**
- `Documents` is OneDrive-redirected, so `SpecialFolder.MyDocuments` is *not* local `Documents`, and
  library files may be cloud-only placeholders.
- **SentinelOne is the on-access scanner here, not Defender** (Defender real-time protection is
  *off*). It opens a file for a few milliseconds right after that file is written or renamed into
  place, anywhere on this machine - `%LOCALAPPDATA%\Temp` included, so tests are not exempt. While
  it holds that handle, a `File.Move(src, dst, overwrite: true)` **onto** that file is refused with
  **ERROR_ACCESS_DENIED**, surfacing as `UnauthorizedAccessException` - which is *not* an
  `IOException`, so catching `IOException` alone misses it - and the source is left on disk.
  Measured over 40,000 back-to-back write-then-replace pairs: **15 refusals idle, 132 with a build
  running alongside**, longest hold **19.5 ms**, every one cleared on the very next attempt. That
  load sensitivity is why it presented as a test that failed roughly 1 run in 6 under
  `dotnet build` + `dotnet test` and never under `dotnet test` alone. `ConfigStore.Save` waits it
  out with a bounded retry and deletes its temp file on failure; **any new replace-in-place code
  needs to do the same.** It is not OneDrive - `Path.GetTempPath()` is under `AppData\Local`, which
  is not redirected.
- GPO-managed machine: `Register-ScheduledTask` and `schtasks /SC ONLOGON` are **denied**. Logon
  autostart uses the Startup folder.
- **The installed copy is not the one you build.** Set up 2026-09-02. `tools\publish-app.ps1`
  publishes the self-contained Release build to `%LOCALAPPDATA%\Whallee\app\` (~160 MB, 249 files),
  and `-Shortcut` writes the logon shortcut to
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Whallee.lnk`. **Delete that .lnk to stop
  Whallee starting at logon — nothing else needs undoing.** Three reasons the installed copy is not
  simply `bin\Release\`:
  - `bin\` is git-ignored, so `git clean -fdx` would delete it and autostart would fail at the next
    logon with nothing pointing at the cause.
  - The repo is under OneDrive-redirected Documents, which on this machine has a history of locking
    files and creating cloud placeholders. An exe Windows launches at logon should not depend on a
    sync client.
  - Most practically: if the autostarted copy were the one you rebuild, Whallee would be running at
    every logon holding the DLLs you are about to overwrite, and **MSB3021/MSB3027 lock failures
    would become the default build experience.** Separating them keeps `bin\` free.

  Consequence: **code changes do not reach the autostarted copy until you re-run that script.** It
  refuses to publish while Whallee is running rather than killing it — exiting via the tray is the
  only correct way to stop it, since it owns windows re-parented into Explorer's desktop.
- `WhalleeConfig.StartWithWindows` **exists but is wired to nothing** — only config round-trip tests
  reference it. Autostart is the shortcut above and nothing else. Making the flag actually create and
  delete that shortcut is Plan 2 task 10, still deferred, and wants the Settings page (task 9) so
  there is a way to toggle it without hand-editing `config.json`.
- **WinUI 3 does not build on this machine** — the XAML compiler fails. WPF builds clean. See the
  spec's Architecture amendment for the evidence before attempting WinUI again.
- **The owner's terminal duplicates pasted text onto one line**, which has broken several commands.
  Give them **short** commands, **one at a time**. `$env:OneDrive` =
  `C:\Users\cory.robbins\OneDrive - Timmons Group Inc` and halves a `cd`. Running the built exe
  directly beats `dotnet run` — no `--project` path and no `--` separator to mangle.
