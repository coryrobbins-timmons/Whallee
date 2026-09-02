# HANDOFF — after Plan 3 (YouTube import), before the thumbnail context menu

**Covers:** picking Whallee up with no plan in flight. **Written:** 2026-09-02.
**Supersedes** the 2026-08-31 handoff, which described Plan 2 Task 8 as next. That is long done.

Read `CLAUDE.md` in this folder first — the pump-thread rule, the standing rulings, the environment
facts and the measured download ceiling all live there and are **not** repeated here.

> Snapshots rot. The **ledgers** under `.superpowers\sdd\` are the running record.
> **Where this document and a ledger disagree, the ledger wins.** Read the tail of
> `.superpowers\sdd\2026-09-01-whallee-youtube-import\progress.md` before this.

## 1. State of the tree

Confirmed immediately before writing this:

- Branch `main`, HEAD **`7c92319`** ("record the fifth download measurement"). **Tree clean.**
- `dotnet build -v q --nologo` → **0 warnings, 0 errors**. `dotnet test` → **142 passing**.
  (Both confirmed with the app closed. A later re-run failed on a locked DLL because the owner's
  app was still open from a test — see §7. Same tree, no code change.)
- No git remote; local `main` only. Committing directly to `main` is pre-approved on this repo.

**Nothing is half-finished and no plan is mid-execution.** This is a clean seam, not a rescue.

## 2. What works on screen

Plans 1–3 are complete and owner-verified on real hardware. The engine plays looping video across
three displays; the WPF control panel adds clips from a file or a YouTube URL and pins them per
display or to all of them.

The YouTube feature was confirmed end to end twice, on 2026-09-01 and 2026-09-02, the second time
with a 4K60 source and a non-zero start time — which exercised both of the message paths added that
day in a single run.

## 3. Where the work stopped, and why

The owner asked to stop and hand off. Two things had just landed, and one had just been declined:

- **Done and committed** — the URL dialog redesign (`3829316`) and the fifth download measurement
  (`7c92319`). Details in the ledger tail; the durable facts are in `CLAUDE.md`.
- **Requested, then explicitly stopped before any design work** — a **right-click context menu on
  library thumbnails**, modelled on Lively's (About / Set as Wallpaper / Preview / Customise /
  Open file location / Share / Delete / Edit; the owner supplied a screenshot of Lively's menu).
  Brainstorming was opened and halted before the first question. **Nothing was written or decided.**
  Start it from scratch at `superpowers:brainstorming`.

There is real design work waiting there, not just wiring: several of Lively's items have no
meaningful Whallee equivalent yet (**Customise** is Lively's per-wallpaper property system;
**Preview** and **Edit** have no host to run in), and *Set as Wallpaper* already exists as the
plain left-click on a tile. Expect the menu to be shorter than Lively's, and expect the useful
question to be which items to leave out.

## 4. What to read for the next feature

The whole surface is small and worth reading before proposing anything:

| File | Why |
|---|---|
| `src/Whallee.App/ViewModels/ClipTile.cs` | 27 lines. `Path`, `FileName`, lazily-filled `ThumbnailPath`. |
| `src/Whallee.App/Pages/LibraryPage.xaml` | The tile `DataTemplate` — the menu attaches here. |
| `src/Whallee.App/Pages/LibraryPage.xaml.cs` | Click handlers; note the `async void` wrapping rule. |
| `src/Whallee.App/ViewModels/LibraryViewModel.cs` | `ApplyClip`, `ImportFileAsync`, `LoadAsync`. |

`ApplyClip` is the model for any new command: it writes config, reports through `UiReporter`, and
calls `_engine.ApplyConfigChange()`. **A delete command has no equivalent and would be the first
destructive action in the app** — it must also clear any `PinnedPath` that referenced the file, or a
display is left pinned to a clip that no longer exists.

## 5. Rules that have already cost real bugs

Both remain live. Neither is theoretical.

- **Two opposite thread rules coexist.** *Engine / host windows:* never block the UI thread — it
  owns windows `SetParent`-ed into Explorer's desktop, so a stall freezes the owner's whole desktop.
  *Tray:* never touch the UI thread from anywhere else — `H.NotifyIcon` raises callbacks on its own
  thread. A fix shaped like one is wrong for the other.
- **Subscribe WPF handlers in code after `InitializeComponent()`, never in XAML.** A XAML-wired
  handler fires while the tree is still being built, before later-declared elements exist. This has
  killed the app twice — once at startup in Plan 2, once in the URL dialog in Plan 3. The surviving
  comment in `UrlImportDialog.xaml.cs` explains it at the subscription site.
- **`async void` handlers wrap their entire body.** An exception escaping one reaches the Dispatcher
  unhandled and terminates the process. Every handler in `LibraryPage.xaml.cs` follows this.

## 6. Open with the owner

| Item | State |
|---|---|
| **Thumbnail context menu** | Requested 2026-09-02, design not started. §3. |
| **Import latency (~6 s)** | Open. `yt-dlp` launches **twice** for overlapping data (`--print`, then `-g`); the 144 MiB download itself took ~0.5 s. A real change to the probe/resolve split. |
| **Format selection for bandwidth** | Raised 2026-09-02, undecided. 4K costs ~144 MiB for a 57-second loop that 1080p would deliver in ~10 MiB. **Bandwidth only — it buys no extra length.** |
| **Plan 2 tasks 9–13** | **Deferred, explicitly NOT cancelled** — the owner said so in as many words. Settings page, Start with Windows, tray Next/Pause over mpv IPC, OneDrive hydration, icon. |
| **App icon (Plan 2 task 13)** | Blocked: source image never saved to disk. Disney/Pixar property — fine personally, cannot ship. |
| **~1 s input lag after Exit** | Parked by the owner. Teardown measured at 126–214 ms, so teardown is **not** the cause. Root cause unknown; do not "fix" it without investigating. |
| **Monitor unplug/replug** | Still unverified: repositioning against the negative-Y virtual-screen origin. Offer when a display change is convenient. |

## 7. Facts to re-verify, never trust

These commands print and exit.

| Fact | How | Last known |
|---|---|---|
| HEAD, branch, tree | `git status`, `git rev-parse --short HEAD` | `7c92319`, clean |
| Tests | `dotnet test --nologo -v q` | 142 passing |
| Build warnings | `dotnet build -v q --nologo` | 0 |
| Displays | `…\Whallee.Harness.exe displays` | 3 displays |
| WorkerW / Progman | `…\Whallee.Harness.exe workerw` | **churn constantly; never cache** |
| ffmpeg / yt-dlp paths | `%LOCALAPPDATA%\Whallee\config.json` | version-stamped winget paths that **move on update** |

**The app's exe is `Whallee.exe`, in a `win-x64\` subfolder** — not `Whallee.App.exe`, and not
directly under the TFM folder. Getting this wrong cost two round trips on 2026-09-02:

```
src\Whallee.App\bin\Debug\net8.0-windows\win-x64\Whallee.exe
```

**A build failing with MSB3021/MSB3027 on `Whallee.Core.dll` means the app is still running**, not
that the code is broken — the error names the holder, e.g. `locked by: "Whallee (40336)"`. Ask the
owner to exit via the tray icon; **do not kill the process**, it owns the wallpaper windows. Seen
on 2026-09-02 immediately after an owner test, on a tree that had just built clean.

**`one` and `all` are not yours to run.** The smoke script `tools/smoke-app.ps1` is owner-approved
for builds that do **not** construct `WallpaperEngine` only; it refuses with exit 3 otherwise, and
extending it to engine builds was declined.

## 8. Handover etiquette on this machine

- **The owner runs the app and the harness verbs; you never do.** Hand over the exact command and
  wait. **Never kill or restart Explorer, and never change display settings.**
- **The owner's terminal duplicates pasted text.** One **short** command at a time, in a
  `powershell`-tagged fence. `$env:OneDrive` halves a long path.
- **Verify owner checks from artifacts when the pass condition is "nothing visibly changed".**
  `%LOCALAPPDATA%\Whallee\whallee.log` has caught a reported success that was actually a crash.
- **Read `whallee.log` after any import** — it now carries the byte count that was removed from the
  user-facing message.
- **Beware silent text corruption when scripting file edits.** Backslashes in a non-raw Python
  string are escape sequences: `\2026-09-02` became an invisible U+0082 control character in
  `CLAUDE.md` on 2026-09-02, survived `grep` and the build, and broke every later exact-match edit
  with no visible cause. Build Windows paths with `chr(92)`, or use the Edit tool. The same file
  family also flips CRLF→LF if written back with `newline=''`.

---

*Plan 3 is finished. This handoff covers a repo with no plan in flight — when the context menu (or
Plan 2's remainder) is picked up and planned, rewrite this rather than leaving it looking current.*
