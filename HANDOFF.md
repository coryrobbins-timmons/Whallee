# HANDOFF — after the 2026-09-03/04 feature run, with no plan in flight

**Covers:** picking Whallee up with nothing half-finished. **Written:** 2026-09-04.
**Supersedes** the 2026-09-02 handoff, which described a thumbnail context menu as "requested,
design not started". That shipped, along with four other features. Nothing in the old document
should be relied on.

Read `CLAUDE.md` in this folder first. The pump-thread rule, the standing rulings, every
environment fact and every measured constraint live there and are **not** repeated here.

> **There is no SDD ledger to consult any more.** All six plan workspaces under
> `.superpowers\sdd\` were deleted on 2026-09-04, once everything durable in them had been moved
> into `CLAUDE.md` — verified fact by fact before deleting, not assumed. **`CLAUDE.md` and
> `git log` are the record now.** A future plan will create its own workspace there; the folder and
> its `.gitignore` remain.

## 1. State of the tree

Confirmed by running the commands immediately before writing this:

- Branch `main`, HEAD **`8846dd2`** ("feat(ui): add the Performance section with the two pause
  rules"). **Tree clean** — `git status --short` printed nothing.
- `dotnet build -v q --nologo` → **0 warnings, 0 errors**. `dotnet test --nologo -v q` →
  **232 passing**.
- No git remote; local `main` only. Committing directly to `main` is pre-approved on this repo.

**Nothing is half-finished and no plan is mid-execution.** This is a clean seam.

## 2. What shipped, and what is actually verified

Five features landed on 2026-09-03 and 2026-09-04:

| Feature | Owner-verified? |
|---|---|
| Thumbnail context menu — Apply to ▸ / Play in default player / Rename… / Open file location / Details… / Delete | **Confirmed** — the owner renamed a clip five times and deleted one that was playing |
| Settings page — Displays, Playback (scaling), About | **Confirmed** — the owner sent a screenshot of it working |
| Start with Windows, app icon, tray icon, desktop + logon shortcuts | **Confirmed** — the owner reported the desktop shortcut and tray icon working |
| Rename a clip, repointing its pins | **Confirmed** — `config.json` and `whallee.log` both show three pins repointed |
| Pause on fullscreen / on battery | **UNVERIFIED — see §3** |

## 3. Where the work stopped, and why

The owner asked for this handoff to be rewritten and the plan workspaces cleared. Nothing was in
flight; no bug caused the stop.

**One thing is genuinely unverified: the pause feature has never been exercised on hardware.**
It is committed, reviewed and published, and **both rules default to off**, so an install whose
checkboxes are never touched behaves exactly as it did before. But nobody has confirmed it pauses.

The check that matters most is this one: **change something that forces an engine restart while
playback is paused** — applying a clip from the Library page does it — and confirm playback is
*still* paused a second later. Fresh mpv processes start **unpaused**, so this is the only check
that distinguishes "the governor re-asserts its state" from "it silently lost track". Every other
part of the feature could look fine while that is broken.

## 4. What to do next

1. **Get the pause feature verified**, or treat it as unproven. It is the only shipped thing with
   no hardware confirmation, and the restart check above is the one worth insisting on.
2. **Then pick from "Known follow-ups" in `CLAUDE.md`**, rewritten 2026-09-04 and ordered by how
   worthwhile each item last looked. Do not re-derive that list.

Nothing is blocked on a decision.

## 5. Standing constraints

All settled. Do not reopen them.

- **The owner runs the app and the harness verbs; you never do.** Hand over the exact command and
  wait. **Never kill or restart Explorer, and never change display settings.**
- **Never kill the Whallee process to unblock a build.** `MSB3021`/`MSB3027` naming
  `locked by: "Whallee (<pid>)"` means the app is running — ask the owner to exit via the tray icon.
  It owns windows re-parented into Explorer's desktop.
- **Testing boundary.** Unit tests cover pure logic in `Whallee.Core` only. No tests for Win32,
  `SetParent`, mpv, XAML, the shell, COM or named pipes. Settled long before this run.
- **No MVVM/ICommand.** Every interaction is a code-behind handler, deliberately.
- **The owner's terminal duplicates pasted text.** One **short** command at a time.

## 6. Facts to re-verify, never trust

These commands print and exit.

| Fact | How | Last known |
|---|---|---|
| HEAD, branch, tree | `git status`, `git rev-parse --short HEAD` | `8846dd2`, clean |
| Tests | `dotnet test --nologo -v q` | 232 passing |
| Build warnings | `dotnet build -v q --nologo` | 0 |
| Whether the app is running | `Get-Process Whallee` | not running when this was written |
| Displays | `…\Whallee.Harness.exe displays` | 3 |
| ffmpeg / yt-dlp / mpv paths | the settings page's About section, or `%LOCALAPPDATA%\Whallee\config.json` | version-stamped winget paths that **move on update** |

**The dev exe is `Whallee.exe` in a `win-x64\` subfolder** — not `Whallee.App.exe`:

```
src\Whallee.App\bin\Debug\net8.0-windows\win-x64\Whallee.exe
```

**But that is not what the owner launches.** The installed copy at
`%LOCALAPPDATA%\Whallee\app\Whallee.exe` is what the desktop and logon shortcuts point at, and it
only changes when `tools\publish-app.ps1` is run. `CLAUDE.md` explains why the two are deliberately
separate — the short version is that an autostarted copy inside `bin\` would hold the DLLs you are
about to rebuild.

## 7. Tried and rejected

Dead ends and false leads from this run, so they are not re-walked:

- **Deriving "can this display be pinned" from `WallpaperEngine.ActivePlayers`.** Shipped, then
  reported broken by the owner. `ActivePlayers` is empty for the whole of an engine restart, and a
  restart pumps messages — so the settings page could be built during that window and come up with
  Pinned greyed out until the user navigated away and back. Replaced by a per-row clip dropdown
  reading only config and the library. **The lesson generalises: never gate a config edit on
  transient engine state.**
- **Rejecting a trailing space in a rename.** Shipped, then removed at the owner's request. Windows
  strips trailing spaces and dots when creating the file anyway, so the only real choice was doing
  it silently or scolding the user for a slip with one obvious fix. Invalid *characters* still
  error, because silently stripping those would mangle a name into something the user did not
  choose — the two cases are not alike.
- **Unpinning a display before deleting its clip.** Designed, approved, then discarded after reading
  `ClipResolver.Resolve`: a *shuffling* display holds a clip open too, and a restart re-randomises,
  so unpinning could not guarantee the handle was released — and a dangling `PinnedPath` turned out
  to be harmless already. Delete is now optimistic, escalating only on failure.
- **Trusting a code review's verdict without its reasoning.** Twice in this run a reviewer approved
  code on reasoning that was wrong, and once a documentation fix introduced a factually incorrect
  comment that only its own re-review caught. Check the argument, not just the conclusion.

## 8. Environment gotchas

`CLAUDE.md` carries the full list. The two that cost the most time in this run:

- **Backslashes in a non-raw Python string silently corrupt files.** Writing a test via a Python
  heredoc turned `\forest` into a form-feed character — an invisible control byte that survived
  `grep` and broke a later exact-match edit with no visible cause. Use the Read/Write/Edit tools, or
  `bytes([92])`. Separately, `sed -i` flipped that file's CRLF endings to LF as a side effect.
- **A build failure is often not a code failure.** See §5.
