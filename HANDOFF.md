# HANDOFF — execute Plan 2 (the WPF control panel)

**Covers:** starting execution of Plan 2. **Written:** 2026-08-27. Plan 1 (the engine) is finished.

Read `CLAUDE.md` in this folder first for the pump-thread rule, the standing rulings, and the
environment facts — this document does not repeat them.

## 1. State of the tree

**Confirmed** (ran `git status` and `dotnet test` immediately before writing this):

- Branch `main`, HEAD `7539272` (the commit that added this file and `CLAUDE.md`).
  **Nothing uncommitted, nothing half-finished.** The last code commit is `e8cad5c`.
- Build clean, 0 warnings. `dotnet test` → **29 passed**.
- `src/` holds `Whallee.Core` and `Whallee.Harness` only. **`Whallee.App` does not exist yet** —
  Plan 2 Task 1 creates it.
- No git remote. Local `main` only.

You are starting from a clean, verified baseline. **You are not inheriting a bug.**

## 2. Where the work stopped, and why

Nothing broke. The session ended because its context was getting long.

**Plan 1 is complete** — 13 tasks, each reviewed, plus a final whole-branch review with a fix wave
and a follow-up wave merged. **Confirmed** by the owner on screen: three monitors playing, icons
clickable on all three, no startup freeze, `FillCrop` correct on the 1920×1200 panel, pinning
surviving restarts, clean teardown, mpv not surviving a hard kill, and Explorer-restart re-attach.

**One Plan 1 gap, left open by the owner's choice.** Task 13 **Step 6** (unplug and replug a
monitor) was never run. It is the only check that exercises the coalescing `Restart()` path on a real
`WM_DISPLAYCHANGE` burst; the Explorer-restart path that *was* verified takes a different branch
(`TaskbarCreated`, which has a 30-second crash-loop guard). **Unknown** whether coalescing works on
real hardware. If display changes misbehave while you work on Plan 2, suspect this first — the fix
may belong to Plan 1's code, not yours.

**Plan 2 is written and not started.** 13 tasks, no code.

## 3. What to do next

1. **Read Plan 2's own "Read this before Task 1" section** (path in §6). It carries the interfaces,
   the WPF-specific pump rules, and the owner's rulings. Most of what can go wrong is described there.
2. **Read the tail of Plan 1's ledger** (path in §6) — the four pump-thread bugs and 24 findings
   parked with rulings. Several constrain Plan 2.
3. **Verify the interface signatures Plan 2 quotes, against the source, before coding against them.**
   Plan 2 says this too. Specifically: `ActivePlayers`' shape on `WallpaperEngine`, `ScalingMode`'s
   members, and whether `MpvHost.PipeName` holds a bare pipe name or a full `\\.\pipe\…` path.
   Plan 1's final fix wave changed several signatures, and Plan 2's quotes came from the ledger.
4. Execute Plan 2 task by task with `superpowers:subagent-driven-development`, as its header asks.

**Task 2 is the risky one** — the first time the engine runs outside the console harness, and where
the pump-thread rule bites. Its owner-verification step has six checks; do not go on to Task 3 until
all six pass.

## 4. Facts you must re-verify

Do not trust these values. The commands are safe — they print and exit.

| Fact | How to check | Last known |
|---|---|---|
| HEAD, branch, tree state | `git status`, `git rev-parse --short HEAD` | `7539272`, clean |
| Test count | `dotnet test --nologo -v q` | 29 passing |
| Build warnings | `dotnet build -v q --nologo` | 0 |
| Displays and device IDs | `…\Whallee.Harness.exe displays` | 3 displays, real EDID ids |
| WorkerW / Progman handles | `…\Whallee.Harness.exe workerw` | `mode: Classic` — **these churn; never cache** |
| ffmpeg path | Plan 2 Task 4 — version-stamped, moves on update | a winget `ffmpeg-N-…` path |
| Package versions | `dotnet restore` | see §5 |

The WorkerW handle genuinely moved three times in one session (`0x20B18` → `0x30C9C` → `0x1053CA`),
and Progman went `0x101EE` → `0x71FEE`. **Confirmed** by running the `workerw` verb at intervals.

## 5. Tried and rejected

Expensive knowledge. Do not re-walk these.

**WinUI 3 does not build on this machine — this is why Plan 2 is WPF.** **Confirmed** by building a
hand-authored minimal WinUI app:

- Packages restore fine: `Microsoft.WindowsAppSDK` **1.6.250602001**,
  `CommunityToolkit.WinUI.Controls.SettingsControls` **8.2.251219**, `H.NotifyIcon.WinUI` **2.3.0**.
- **The build fails in the XAML compiler** — `XamlCompiler.exe` reports its `input.json` does not
  exist, because the target that generates it never runs.
- Ruled out by experiment: `EnableMsixTooling=false`, and XAML item globbing (`Page` items *are*
  globbed by default; declaring them explicitly gives NETSDK1022).
- No WinUI template installed; no standalone Windows SDK (the NuGet ref pack covers the TFM).
- **Assumed:** what remains is the documented CLI-without-Visual-Studio-workload failure, which may
  be GPO-refused here.
- **WPF builds clean in ~5 s.** **Confirmed.**

**`H.NotifyIcon` 2.4.1 requires `net10.0-windows`** and fails NU1202 here. **Confirmed** by probing:
2.3.0 / 2.2.0 / 2.1.0 / 2.0.131 all restore on `net8.0-windows`. Plan 2 pins **2.3.0**.

**A broadcast probe gave a false negative before the right answer.** Testing whether a message-only
(`HWND_MESSAGE`) window receives broadcasts, the first attempt used
`SendMessageTimeout(HWND_BROADCAST, …)` and *neither* test window received anything — which reads as
"both fail" and is wrong. `SendMessageTimeout` blocks **per window** across every top-level window on
the system and never reached the probe's own windows in the time budget. `PostMessage` gave the clean
split: message-only **no**, top-level **yes**. Use `PostMessage` if you ever re-test this.

**A hand-rolled `FindWindow("Progman")` probe returned 0 and caused a false alarm** about a missing
desktop layer. The harness's `workerw` verb — the real code path — immediately showed
`progman 0x71FEE`, `mode: Classic`. **Check the real code path before reporting an anomaly from a
hand-written probe.**

**Two amendment notes written during Plan 1 went stale within hours** and were then restated as fact
by later agents. Both are corrected now, but the pattern is worth knowing: a note describing code
rots like a comment. Re-read your own amendments when the thing they describe changes.

## 6. Pointers, not copies

- **Plan 2 (what to execute):**
  `..\Visual Studio Code\Claude Code\docs\superpowers\plans\2026-08-27-whallee-control-panel.md`
  — 2167 lines, 13 tasks. Read its first section before Task 1.
- **Plan 1's ledger (authoritative record — read the tail):**
  `.superpowers\sdd\2026-08-20-whallee-engine\progress.md` — ~1000 lines.
- **Plan 1:** `..\Visual Studio Code\Claude Code\docs\superpowers\plans\2026-08-20-whallee-engine.md`
  — its `AMENDED` notes are authoritative.
- **Spec:** `..\Visual Studio Code\Claude Code\docs\superpowers\specs\2026-08-20-live-wallpaper-app-design.md`
  — amended for the WPF decision, `ffmpegPath`, the application icon, and the pump-thread rule.
- **Deferred findings:** `.superpowers\sdd\2026-08-20-whallee-engine\deferred-findings.txt`
- **This folder's `CLAUDE.md`** — constraints and environment facts, auto-loaded. Not repeated here.

## 7. Open with the owner

- **The WALL-E application icon** (Plan 2 Task 13) is **blocked**: the source image has never been
  saved to disk. Ask for the path. The spec records that the still is Disney/Pixar property — fine as
  a personal icon on this machine, but it cannot ship if Whallee is ever distributed.
- **Plan 1 Task 13 Step 6** (monitor unplug/replug) is unverified by choice. Worth offering again
  when a display change is convenient.

---

*When Plan 2 execution is done or superseded, this handoff is spent — say so and offer to remove or
rewrite it rather than leaving it here looking current.*
