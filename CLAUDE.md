# CLAUDE.md

Guidance for Claude Code working in this repository.

## Records that exist here

- `.superpowers/sdd/<plan-name>/progress.md` — the **authoritative record**: every task, every
  review finding, every ruling, and why. Read its tail when resuming work on that plan.
- `HANDOFF.md` — a session-to-session briefing, written when a session ends mid-flight. **Do not
  read it unless the owner asks you to** (e.g. "pick up from the handoff"). It is a snapshot as of
  the date at its top, so it goes stale; where it and the ledger disagree, **the ledger wins**.

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
- Four clips in the library. **Four clips across three displays means two displays showing the same
  clip is expected, not a bug.**
- `Documents` is OneDrive-redirected, so `SpecialFolder.MyDocuments` is *not* local `Documents`, and
  library files may be cloud-only placeholders.
- GPO-managed machine: `Register-ScheduledTask` and `schtasks /SC ONLOGON` are **denied**. Logon
  autostart uses the Startup folder.
- **WinUI 3 does not build on this machine** — the XAML compiler fails. WPF builds clean. See the
  spec's Architecture amendment for the evidence before attempting WinUI again.
- **The owner's terminal duplicates pasted text onto one line**, which has broken several commands.
  Give them **short** commands, **one at a time**. `$env:OneDrive` =
  `C:\Users\cory.robbins\OneDrive - Timmons Group Inc` and halves a `cd`. Running the built exe
  directly beats `dotnet run` — no `--project` path and no `--` separator to mangle.
