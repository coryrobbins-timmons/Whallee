# HANDOFF — Plan 2 (the WPF control panel), Tasks 8–13

**Covers:** resuming Plan 2 mid-execution. **Written:** 2026-08-31.
**Tasks 1–7 are done and owner-verified. Task 8 is next.**

Read `CLAUDE.md` in this folder first for the pump-thread rule, the standing rulings, and the
environment facts — this document does not repeat them.

> This is a snapshot and it will rot. The **ledger** at
> `.superpowers\sdd\2026-08-20-whallee-engine\progress.md` is the running record and it was kept
> current after every task. **Where the two disagree, the ledger wins.** Read its tail before this.

## 1. State of the tree

**Confirmed** immediately before writing this (`git status`, `dotnet test`, `dotnet build`):

- Branch `main`, HEAD **`eaf81d8`** ("apply a clip to one display or all"). **Tree clean.**
- Build 0 warnings. `dotnet test` → **81 passed** (was 29 at the end of Plan 1).
- `src/` holds `Whallee.Core`, `Whallee.Harness`, and **`Whallee.App`** — the WPF app now exists
  and runs. No git remote; local `main` only.

You are not inheriting a bug. Nothing is half-finished.

## 2. What works right now, on screen

Tasks 1–7, each verified by the owner on real hardware:

| # | What | Verified |
|---|---|---|
| 1–2 | WPF shell hosting the engine on the UI thread | 6 checks, incl. Explorer restart |
| 3 | Single instance, tray icon, hide-to-tray, Exit | after a tray-threading fix |
| 4–5 | ffmpeg discovery + cached thumbnail extraction | cold-cache run, ~1.2 s/clip |
| 6 | Library page, tile grid, lazy thumbnails, target selector | UI stayed smooth during a cold pass |
| 7 | Click a tile to apply a clip to one display or all | incl. persistence across a restart |

Live state as left: `DISPLAY1` + `DISPLAY5` pinned to `clouds-1.mp4`, `DISPLAY6` to `forest.mp4`.

## 3. Where the work stopped, and why

Nothing broke. The session's context filled up. Task 7 was verified, the ledger updated, and this
document rewritten — a clean seam.

**Next: Task 8 (Add from file).** The `Add from file…` button already exists in
`Pages/LibraryPage.xaml` with **no click handler wired** — that is Task 8's job, not a bug.

Then 9 (Settings page — note `MainWindow.ShowPage` index 1 is a `TextBlock` placeholder today),
10 (Start with Windows), 11 (Tray Next clip / Pause-Resume over mpv IPC), 12 (OneDrive hydration
check), 13 (icon — **blocked**, see §7).

## 4. The one thing to internalise before writing code

**Treat Plan 2's code blocks as a sketch to verify, not as source to copy.** Eight defects were
found in them across seven tasks — not typos, defects that shipped would have broken the app:

- a `NullReferenceException` that killed the app at startup (`SelectionChanged` fires *during*
  `InitializeComponent`, before the element it touches exists);
- tray callbacks calling `Application.Shutdown()` from H.NotifyIcon's own message-window thread,
  which crashed the app on Exit via `Dispatcher.VerifyAccess`;
- a mutex the second instance could not release, crashing it;
- two omitted `ConfigStore.Save` calls; wrong `ShellWatcher` arity; wrong `ScalingMode` members;
  a missing `onStartFailure`.

The plan's prose and its rulings are sound and worth following. Its code is a draft. **Check every
signature it quotes against the source before compiling against it.**

**Two opposite thread rules coexist in this app, and a fix shaped like one is wrong for the other:**

- **Engine / host windows:** *never block the UI thread.* It owns windows `SetParent`-ed into
  Explorer's desktop, so a stall there freezes the owner's entire desktop.
- **Tray:** *never touch the UI thread from anywhere else.* `H.NotifyIcon` raises callbacks on its
  own thread; everything it hands you must be marshalled with `Dispatcher.InvokeAsync`.

## 5. Tools added this session — use them

- **File log sink** (`Whallee.Core/Diagnostics/LogFileSink.cs`) → `%LOCALAPPDATA%\Whallee\whallee.log`.
  Unplanned, and it paid for itself the same hour: it found a spurious-restart bug on its first run,
  proved the fix, and its `=== session end ===` line caught a crash the owner had reported as a
  success. **Read this log to check the owner's work.**
- **`tools/smoke-app.ps1`** — launch, dwell 3 s, assert the process is alive and no new
  Application-log error, kill. **Owner-approved for builds that do not construct `WallpaperEngine`
  only**; it refuses with exit 3 if it finds one. Extending it to engine builds was **declined** —
  do not.

**Verify owner checks from artifacts whenever the pass condition is "nothing visibly changed".**
In Task 7 the owner reported three checks passed when only two had run; the log showed no session
end and the process still alive. A screen report cannot distinguish "state persisted" from "the app
never restarted".

**Prefer a continuous owner interaction over a timed one.** Clicking during a 5 s window is a race
the owner has to win; dragging the window is continuous and a freeze is unmistakable.

## 6. Facts you must re-verify

Do not trust these values. The commands print and exit.

| Fact | How to check | Last known |
|---|---|---|
| HEAD, branch, tree | `git status`, `git rev-parse --short HEAD` | `eaf81d8`, clean |
| Test count | `dotnet test --nologo -v q` | 81 passing |
| Build warnings | `dotnet build -v q --nologo` | 0 |
| Displays / device IDs | `…\Whallee.Harness.exe displays` | 3 displays |
| WorkerW / Progman handles | `…\Whallee.Harness.exe workerw` | **churn constantly; never cache** |
| ffmpeg path | `%LOCALAPPDATA%\Whallee\config.json` → `FfmpegPath` | a version-stamped winget path |

The ffmpeg path is version-stamped (`ffmpeg-N-125875-…`) and **moves on every update**. It is now
persisted to config on first discovery, so a stale value there is a likely future failure.

## 7. Open with the owner

- **The WALL-E application icon (Task 13) is blocked.** The source image has never been saved to
  disk — ask for a path. The spec records it as Disney/Pixar property: fine as a personal icon on
  this machine, but it cannot ship if Whallee is ever distributed.
- **~1 s of input lag after Exit**, parked by the owner ("not worth getting hung up on"). Teardown
  itself was *measured* at 126–214 ms, so teardown is **not** the cause and the obvious reorder
  would have bought ≤130 ms of a 1000 ms symptom while risking a visible flash. **Root cause
  unknown.** Do not "fix" it without investigating first.
- **Plan 1 Task 13 Step 6** (monitor unplug/replug) still unverified. Coalescing on a real
  `WM_DISPLAYCHANGE` *is* now proven — a spurious broadcast was restarting the engine, and the fix
  (compare a `DisplayTopology` signature before acting) was confirmed live. What remains unproven is
  **repositioning against a negative-Y virtual-screen origin**, which needs real unplug/replug.
  Worth offering when a display change is convenient.

## 8. Pointers, not copies

- **Plan 2 (what to execute):**
  `..\Visual Studio Code\Claude Code\docs\superpowers\plans\2026-08-27-whallee-control-panel.md`
  — 13 tasks; Task 8 starts at line 1621. Its `AMENDED` notes are authoritative. Read its
  "Read this before Task 1" section anyway; it carries the interfaces and the owner's rulings.
- **Ledger (authoritative running record):**
  `.superpowers\sdd\2026-08-20-whallee-engine\progress.md` — 1596 lines. **Read the tail.**
- **Deferred findings:** `.superpowers\sdd\2026-08-20-whallee-engine\deferred-findings.txt`
- **Spec:** `..\Visual Studio Code\Claude Code\docs\superpowers\specs\2026-08-20-live-wallpaper-app-design.md`
- **This folder's `CLAUDE.md`** — constraints and environment facts, auto-loaded. Not repeated here.

## 9. Handover etiquette on this machine (learned the hard way)

The owner's terminal **duplicates pasted text**. A two-line paste produced
`Set-Location : A positional parameter cannot be found that accepts argument 'cd'` and cost a round
trip to a wrong diagnosis — I blamed the fence tag; the owner had to say "I pasted them into my
terminal" before I read the actual terminal and saw it. Hand over **one short command at a time**,
in a `powershell`-tagged fence (never `bash`), and say explicitly to press Enter.

**The owner runs the app; you never do** — except the smoke script in §5, under its stated limit.
**Never kill or restart Explorer, and never change display settings.**

---

*When Plan 2 is finished or superseded, this handoff is spent — say so and offer to remove or
rewrite it rather than leaving it here looking current.*
