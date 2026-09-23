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

**Updated 2026-09-04.** Since this list was first written, five features shipped: the thumbnail
context menu (Apply to / Play / Rename / Open file location / Details / Delete), the settings page,
Start with Windows, the app and tray icon, and pause-on-fullscreen / pause-on-battery. That closes
**Plan 2 tasks 9, 10 and 13**. Specs and plans for each are in the sibling workspace under
`C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Visual Studio Code\Claude Code\docs\superpowers\`.

**Updated 2026-09-17.** The yt-dlp staleness check shipped — see the Environment facts entry and
standing ruling 5. Four items were raised by the owner while testing it and head this list; the
older items follow.

- ~~**Players are never synchronised across displays.**~~ **Shipped 2026-09-18, superseded
  2026-09-22.** The first fix seeked every player to zero before the reveal. Measurement on
  2026-09-21 disproved it: the players were already at zero (or hadn't started at all) when seeked,
  so the seek was a no-op — the stagger was in *when playback begins*, not in position. See the
  Environment facts entries below for both the measurement and the shipped mechanism. The
  superseding fix, shipped 2026-09-22, is exactly the pause/seek/resume-over-IPC approach an earlier
  version of this bullet rejected; that rejection turned on an undetectable failed unpause, which is
  answered by `MpvIpc` gaining a read (`QueryTimePosAsync`) plus a guaranteed resume on every exit
  path — see the Environment facts entry on the IPC pause below.
- **Shuffle ribbon** — owner's request, 2026-09-16. A third tab beside Library and Settings: rotate
  the wallpaper on a configurable interval, with a filter over which genres take part and any
  combination selectable (just Cars, or Cars + Dystopia, or everything). Genres already exist and
  are stored in `sources.json`, so the filter has something to read.
- ~~**A second copy exits silently.** Launching Whallee while it is already running logs
  `another copy is already running — exiting` and dies with no window, no message, and no focus
  handed to the copy that is running. On 2026-09-16 the owner clicked the shortcut **nine times in
  33 seconds** and got nothing back, having believed a tray Exit had taken when it had not. The
  normal behaviour for a single-instance tray app is for the second copy to surface the first one
  and then exit. Cheap, and it has already cost real time.~~ **Shipped 2026-09-22** — a second copy now signals a named event (`Whallee.ShowRequest`) and the running copy surfaces its window.
- **A button to update yt-dlp.** Owner's request, 2026-09-16, and safer than first judged: Whallee
  would launch `winget` rather than rewrite the binary itself, and a manual run needed no elevation.
  **Blocked on one real question, not on code:** winget's catalogue lags yt-dlp's own releases, so
  there will be windows where the warning says "old" and `winget upgrade` answers "nothing
  available" — a button that visibly does nothing is worse than no button. Note also that `winget`
  returns **exit code 43**, not 0, for "no applicable upgrade"; treating non-zero as failure would
  report an error on the common path.
- **Reduce `PlayerSettle.RenderGrace`.** It is a 200 ms guess covering "the pipe is up but no frame
  is drawn yet". Readiness is now measured directly over IPC, so the grace may be shrinkable or
  removable, which would recover latency on every pass. Untried, and it guards the
  wallpaper-flash defect, so it must be changed on its own and judged by looking at the screen.
- ~~**Settle whether `Shell_NotifyIcon` works from the Dispatcher thread.**~~ **Settled 2026-09-23.**
  `TrayIcon.Notify` calls it without marshalling, and that is fine, but not for the reason this
  bullet originally gave. Reflection over H.NotifyIcon 2.3.0 confirmed it runs its own internal pump
  thread (a private `Thread` field, a `ThreadId`, and P/Invokes to
  `GetMessage`/`TranslateMessage`/`DispatchMessage`/`PostThreadMessage`) — so the constructor's
  remarks about callbacks arriving off the Dispatcher thread were correct all along. What was wrong
  was the assumption that *calling* `Shell_NotifyIcon` inherits that same thread affinity:
  `Shell_NotifyIcon` itself has NO thread affinity — it hands data to the shell, which posts
  callback messages to the owning HWND — so calling it from the Dispatcher thread, as `Notify`
  does, is legal and works despite HWND ownership living on H.NotifyIcon's own pump thread. See
  `TrayIcon.Notify`'s remarks for the full account.

The older items, in the order they were last judged worth doing:

- **Import latency** — item 2 below. **Partly overtaken 2026-09-14:** a two-month-stale `yt-dlp` was
  resolving every video through YouTube's `android_vr` client, which googlevideo serves about a
  minute of and then refuses with 403. That made strategy A fail on every import and forced the slow
  strategy-C fallback. Updating `yt-dlp` made A work — full quality, any timestamp — so the ~60 s cap
  was never this network's, despite what several comments used to claim in capitals. The process-launch
  cost in item 2 still stands. **`ImportPlanner.ContentCeiling` now binds only the strategy-C
  fallback**; re-measure before trusting it if C ever becomes the common path again.
- **Format selection for bandwidth** — undecided. 4K costs ~144 MiB for a 57 s loop that 1080p would
  deliver in ~10 MiB. Now has somewhere to live: the settings page exists.
- **Per-display pause** — deferred deliberately when pause was built. It cannot reuse
  `SHQueryUserNotificationState`, which is system-wide; it needs window enumeration and per-monitor
  rectangle intersection. It is also what would close the *maximized-window* gap: Windows only
  answers "is something fullscreen?", never "is the wallpaper covered?", so a maximized window does
  not pause playback today.
- **Tray Next / Pause over mpv IPC** — Plan 2 task 11. The IPC half is now built and proven
  (`MpvIpc`), but `PlaybackGovernor` exposes no hook to coordinate with, and its act-only-on-
  transition logic would override a manual resume within a second. That precedence question is
  unanswered and should be settled before building it.
- **OneDrive hydration** — Plan 2 task 12.
- **~1 s input lag after Exit** — parked by the owner. Teardown measured at 126–214 ms, so teardown
  is *not* the cause. Root cause unknown.
- **Monitor unplug/replug** — never verified. Repositioning against the negative-Y virtual-screen
  origin is the risk.

The three original items follow, unchanged.

Recorded 2026-09-02. Items 1 and 3 were done the same day (commit below); item 2 remains open.
Plan 3 (YouTube import) is complete, reviewed, and confirmed working end to end; these are
improvements, not defects.

1. ~~Redesign the URL import dialog's explanatory text.~~ **Done 2026-09-02.** The dialog now
   predicts nothing: the ~60 s limit is reported after the import, where it is a fact rather than an
   estimate, because knowing it beforehand cannot change what the user does. The start-time field
   was **kept**, not dropped - it is inert on this network but the 403s look specific to the
   corporate egress IP, so it may work from home. Design:
   `C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Visual Studio Code\Claude Code\docs\superpowers\specs\2026-09-02-url-dialog-redesign-design.md`.
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
| `src/Whallee.App` | The WPF control panel. Code-behind handlers throughout; no MVVM, no `ICommand`. |
| `src/Whallee.Harness` | Console harness. The headless acceptance test for the engine. |
| `tests/Whallee.Core.Tests` | Unit tests for pure logic only. |

Plans and the design spec live in the sibling workspace at
`C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Visual Studio Code\Claude Code\docs\superpowers\`.

### The theme (`src/Whallee.App/Theme/`)

Dark, amber-accented, hand-rolled — no theme library, and no plan to add one. Three dictionaries
merged by `App.xaml` **in this order, which is load-bearing**: `Tokens.xaml` (every colour and
radius in the app, and the only place a hex value may appear), `Controls.xaml` (implicit styles for
stock controls), `Components.xaml` (keyed styles for Whallee's own parts).

**Two WPF traps were paid for here. Do not re-learn them:**

1. **A `StaticResource` inside a merged dictionary never resolves against a sibling dictionary** —
   only against its own chain. `Controls.xaml` and `Components.xaml` therefore each merge
   `Tokens.xaml` themselves. Listing them side by side under `Application.Resources` is not enough
   and throws `XamlParseException` at startup.
2. **An implicit style is matched on the element's exact runtime type, not its base type.** So
   `<Style TargetType="Window">` applies to no window in this app — `MainWindow` and all three
   dialogs are derived types. Those two styles are keyed (`AppWindowStyle`, `AppPageStyle`) and
   named explicitly on each window and page.

Also: a trigger's `TargetName` can only name an element in the template. Pointing one at a named
`RotateTransform` or `TranslateTransform` fails the build with `MC4111` — set the whole
`RenderTransform` instead.

**Neither of the first two is visible to `dotnet build`.** Both are load-time failures. There is an
out-of-tree harness pattern for catching them: merge the dictionaries with `XamlReader`, resolve
every token key, and `Measure()` an instance of every styled type. Worth rebuilding if the theme is
touched again.

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

## Library genres, and the per-clip record they live in

Added 2026-09-11 (source links) and 2026-09-14 (genres). Spec and plan are in the sibling workspace
under `docs\superpowers\`.

**`%LOCALAPPDATA%\Whallee\sources.json` holds one record per clip, keyed by BARE FILE NAME** — the
same convention as `DisplayAssignment.PinnedPath`, because the library folder is configurable and an
absolute path would break the moment it moved. Each record carries where the clip was downloaded
from (URL, requested start and length) and its genre. The file also carries the owner's **ordered
genre list**, stored rather than derived: the order is theirs and cannot be recovered from the
assignments, and a genre must survive having no clips in it or creating one would make the tab
vanish underneath them.

**It lives beside `config.json`, not in the library folder**, because the owner browses the library
in Explorer ("Open file location" is a menu item) and a bookkeeping file among their videos is
clutter. The cost is that moving the library leaves links and genres behind.

**The file has two on-disk shapes and `Load` must keep reading both.** Before genres it was a bare
map of file name to record; it is now an object with `clips` and `genres`. The discriminator is a
**case-insensitive** scan for a `clips` property — `JsonDocument.TryGetProperty` is case-sensitive
and does NOT consult `JsonSerializerOptions.PropertyNameCaseInsensitive`, while the writer emits
`Clips`. Getting that wrong sends every round trip through the legacy parser; it was caught in
review on 2026-09-14 before it shipped. Silent loss here surfaces as "Not recorded" in Details and
reads as an unrelated bug.

**Keep the index in step with renames and deletes**, exactly as pins are by `ClipRename.Repoint`. It
follows a file rename and is dropped on delete — but only on paths where the clip is actually gone.
Erasing it before a recycle that then fails loses the owner's filing while leaving the video.

Things that were got wrong once here and should not be re-derived:

- **`ItemsPanel` arranges the GROUPS when grouping is on**, not the items. The tiles' `WrapPanel`
  therefore lives in `GroupStyle.Panel`, and `LibraryPage` swaps between a flat `WrapPanel` and a
  vertical `StackPanel` in code. Backwards lays the genres out side by side with clips stacked under
  each one.
- **Group order needs an explicit sort.** `PropertyGroupDescription` alone orders groups by whichever
  item it meets first, so dragging tabs would reorder the bar and change nothing in the "All" view.
  `GenreOrderComparer` keys off the stored order, ungrouped last.
- **A re-import must carry the clip's genre across.** `Record` replaces the whole entry, so building
  a fresh record from the import alone silently unfiles the clip.
- **Setting `Style` to null BLOCKS implicit-style lookup** rather than falling back to it, so an
  unstyled tab renders as a native light-chrome button beside the dark ones. Ask for the implicit
  style by `FindResource(typeof(Button))`.
- **A disabled `MenuItem` needs opacity, not just `Foreground`.** The implicit `TextBlock` style sets
  a colour explicitly, and an explicit style setter beats an inherited value — so a disabled item
  looked fully enabled while refusing clicks.
- **`GenreBar.Ungrouped` is a sentinel**, never stored, renamed or deleted; its NUL prefix is what
  makes it uncollidable with a typed name. **Writing that escape into a source file through a shell
  heredoc turns it into a literal NUL byte** — it has happened twice. Use the editing tools, then
  check.
- **Dragging replaced "Move left" / "Move right"** on 2026-09-14. The drop index comes from which
  half of the target tab the pointer is over, and is an index among *genres*, not among the panel's
  children — "All" is child 0, "Ungrouped" and "+ New genre" sit at the end.

**Deleting a genre never deletes video.** It unassigns its clips and removes the tab; the
confirmation says so, and that wording is load-bearing.

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
5. **`UrlImportDialog`'s "predicts nothing" rule has exactly one recorded exception**: the stale
   yt-dlp line, added 2026-09-16. The rule came from removing a ~60 s estimate whose stated problem
   was that it *could not change what the user does*. A stale tool can — the owner goes and updates
   it. The exception is documented at `UrlImportDialog.CheckToolAge` and in
   `YtDlpVersion.DialogWarning`. Do not delete that line as a rule violation; do not take it as
   licence to reintroduce network predictions.
6. **`MessagePump.RunUntil(Task)` is for the console harness only.** It is a blocking `GetMessageW`
   pump and in a WPF app it fights the framework's own message loop. When a pass step needs to wait
   on background work — `WallpaperEngine.AlignPlayers` is the example — poll with
   `MessagePump.Sleep` the way `WaitForPlayersToSettle` does. The method whose name fits best is
   the one that must not be used.

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
- **yt-dlp's version IS its release date** (`2026.08.19`), so "how stale is it?" is subtraction
  against the system clock — no network call, no GitHub API, nothing that behaves differently
  offline. `YtDlpVersion` uses this; do not replace it with a latest-release lookup.
  `YtDlpVersion.StaleAfterDays` is 45, and the check runs **once per app session** because import
  latency here is process launches, not bandwidth.
- Four clips in the library. **Four clips across three displays means two displays showing the same
  clip is expected, not a bug.**
- **mpv does NOT lock a clip it is playing — deleting one while it is on screen succeeds.**
  Owner-confirmed 2026-09-03: deleting the clip a display was actively playing worked, and that
  display fell back to a random pick with no error. This had never been measured; only the
  *overwrite* case had, and predicting a sharing violation from it had already been wrong once.
  Consequences worth keeping: `ClipResolver.Resolve`'s "pinned clip is gone, fall through to a
  random pick" branch is a **live, exercised path**, not a defensive one; and
  `LibraryViewModel.DeleteClipAsync`'s optimistic order (recycle first, clear config after) is
  right, with its `IOException` escalation being genuinely rare rather than the expected route.
- `Documents` is OneDrive-redirected, so `SpecialFolder.MyDocuments` is *not* local `Documents`.
  **The repo and its `Library` now live on the local path** (`C:\Users\cory.robbins\Documents\coryrob\Whallee`),
  moved out of OneDrive on 2026-09-14, so library files are no longer cloud-only placeholders.
  Clips imported from elsewhere on the machine still can be.
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
  `-Shortcut` writes the logon shortcut to
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Whallee.lnk`, and `-DesktopShortcut`
  writes one to the Desktop. **Delete a .lnk to undo it — nothing else needs undoing.** Neither
  shortcut sets `IconLocation`, deliberately: a shortcut naming no icon shows its target exe's
  embedded icon, so `Whallee.ico` is picked up with nothing to keep in sync. Note the Desktop here
  is **OneDrive-redirected**, so the script resolves it with `GetFolderPath('Desktop')` rather than
  `$env:USERPROFILE\Desktop`, which does not exist. Three reasons the installed copy is not simply
  `bin\Release\`:
  - `bin\` is git-ignored, so `git clean -fdx` would delete it and autostart would fail at the next
    logon with nothing pointing at the cause.
  - Historically the repo sat under OneDrive-redirected Documents, which has a history of locking
    files and creating cloud placeholders. It was moved to local `Documents` on 2026-09-14, but the
    separate installed copy stands regardless: an exe Windows launches at logon should not be the
    one you rebuild.
  - Most practically: if the autostarted copy were the one you rebuild, Whallee would be running at
    every logon holding the DLLs you are about to overwrite, and **MSB3021/MSB3027 lock failures
    would become the default build experience.** Separating them keeps `bin\` free.

  Consequence: **code changes do not reach the autostarted copy until you re-run that script.** It
  refuses to publish while Whallee is running rather than killing it — exiting via the tray is the
  only correct way to stop it, since it owns windows re-parented into Explorer's desktop.
- `WhalleeConfig.StartWithWindows` **is wired** — Plan 2 task 10, done 2026-09-03. The Settings
  page's checkbox creates and deletes the logon shortcut via `StartupShortcut`. **The shortcut on
  disk is the source of truth, not the flag:** the page reads `StartupShortcut.Exists()` and the
  flag follows it, so deleting the `.lnk` by hand wins over a stale `true` in config. The checkbox
  disables itself, with a stated reason, when there is no installed copy to point at.
- **The Performance rules pause playback rather than restarting anything.** `PlaybackGovernor` polls
  `SHQueryUserNotificationState` and `GetSystemPowerStatus` once a second and, on a *transition*
  only, writes `{"command":["set_property","pause",…]}` to each player's `--input-ipc-server` pipe.
  Two consequences worth knowing before changing it: it re-asserts after an engine restart by
  tracking which pipes it last applied to (fresh mpv processes start **unpaused**), and its tick is
  `async void`, so it carries a `_ticking` re-entrancy guard — an `async void` handler returns to
  the dispatcher at its first `await`, so the next tick fires while the previous one is still
  writing.
- **A merely maximized window does not pause playback.** Windows offers no system-wide way to ask
  "is the wallpaper covered?" — only "is something fullscreen?" — so a maximized Civil 3D window
  leaves all three players running. A limitation, not a bug; per-display coverage detection is what
  would close it.
- **WinUI 3 does not build on this machine** — the XAML compiler fails. WPF builds clean. See the
  spec's Architecture amendment for the evidence before attempting WinUI again.
- **The owner's terminal duplicates pasted text onto one line**, which has broken several commands.
  Give them **short** commands, **one at a time**. `$env:OneDrive` =
  `C:\Users\cory.robbins\OneDrive - Timmons Group Inc` and halves a `cd`. Running the built exe
  directly beats `dotnet run` — no `--project` path and no `--` separator to mangle.
- **The players are aligned by pausing them, seeking to zero and resuming together — once they are
  all actually playing.** The offset between displays is in *when playback starts*, never in
  playback position. Measured 2026-09-21: at the moment the players were seeked their positions were
  `0.23 / 0.00 / 0.00`, and twice `0.17 / - / 0.00`, where a dash means that player reported no
  position because it had not begun. **Seeking them all to zero was a no-op**, which is why every
  delivery diagnostic came back clean while nothing improved. `launchMpv` in the pass profile
  measures three `Process.Start` calls, not time-to-first-frame, and `settle` waits for mpv's IPC
  pipe, which opens early in its init — so the stagger is born *after* the old sync step ran.
  **Do not reinstate a seek-only alignment**, and do not raise `PlayerSettle.RenderGrace` (200 ms)
  as a substitute: it is the guess that hid this, and a bigger guess only moves the failure.
  Reducing it is now an open opportunity, since readiness is measured properly.
- **The pause is applied over IPC, never as `--pause` on mpv's command line.** A player launched
  paused can only be freed over IPC, and this repo already records mpv's pipe bind failing silently
  (see `WallpaperEngine.PipeNameFor`) — that display would be stuck on frame 0 forever. Pausing
  after the fact means **we only ever pause a player we have just proven we can reach**; anything
  unreachable keeps playing, which is the pre-existing behaviour. A player that has been paused is
  resumed on every exit path including exceptions, and resuming is idempotent. **The wait is
  cancellable; the pause/seek/resume burst is not.** Cancelling the wait is free because nothing has
  been paused yet, and there is one final cancellation check immediately before the first pause for
  exactly that reason — but once a player has been paused, nothing cancels its resume, because that
  is how a display gets left frozen. This is why `PlayerSync.Timeout` must not be shortened casually.
