# Roadmap

Tattlebun is a Windows tray app that keeps a receipt of what your coding agents changed on your PC outside the repo, and undoes those changes once you confirm. This file tracks the decisions already made, what gets built in what order, and what's out of scope.

## Architecture characteristics

What the design must protect. Everything else gets ordinary good design. The owner picked these on 2026-10-24.

1. **Trust.** It only reads, nothing leaves the PC, and it changes something only after you confirm an undo.
2. **Fair blame.** It never pins a change on a command or session that didn't make it. When it can't tell, it says "one of these" or "not tied to a session".
3. **Out of the agent's way.** A snapshot adds under 100 ms to a command, and a Tattlebun failure never blocks one.

Given up for them: exact per-process blame (ETW needs admin), a view of the whole disk, and in-app undo of changes that need admin.

## Decisions (2026-10-24)

The owner's:

- **Build Tattlebun.** It's the owner's own problem: running several agents at once, the hardest part of checking their work is what they ran on the PC, not what they changed in the repo. A search of the web and GitHub on 2026-10-24 found no tool that keeps a receipt of each session's system changes. The nearest are Process Monitor (everything, raw), transcript scanners such as whatileaked, and aGiTrack, which commits changes inside the repo. This gives up that time for other ideas.
- **The name is Tattlebun:** a bunny that tattles on what your agents did. On 2026-10-24 nothing called Tattlebun was on GitHub, and neither `tattlebun.com` nor `tattlebun.app` had DNS. Trademarks aren't searched yet. This gives up the runner-up, Sniffpup, whose `.com` is registered.
- **A snapshot around each command, not each session.** Claude Code's `PreToolUse` and `PostToolUse` hooks run before and after every tool call, with the session ID, the tool call's ID and the exact command. The owner runs several agents at once, so snapshots at session start and end would mix their changes. A killed session also never sends `SessionEnd`: the [hooks docs](https://code.claude.com/docs/en/hooks) list its exit reasons, and none is a kill or a crash. ETW would say exactly which process wrote each file or key, but it needs admin. This gives up a command that never waits: each one waits for a snapshot, hence the 100 ms budget.
- **Blame by interval.** Each snapshot, from any session, closes an interval. A change found in it goes to the commands that were running during it: one command gets it, several get "one of these", and none means "not tied to a session". A session that died mid-command has its interval closed by the next snapshot from any session. Receipts say "while", not "by". This gives up certainty: a change you make yourself during an agent's command lands on that command's receipt.
- **A fixed list of files, not the whole home folder.** Dotfiles and tool configs, global package folders, new items at the top of the home folder, and every path an agent's Write or Edit call touched outside the repo, which the hook names exactly. Each file is recorded by size, time and hash. A watcher on the whole home folder would blame agents for what Chrome, OneDrive and app caches write. This gives up a stray file a command writes deep in some other folder.
- **Undo what doesn't need admin, and show a command for the rest.** Each undo runs only after you tick it and confirm. For a machine-wide PATH entry, a service, an `HKLM` startup entry or a machine-wide install, the receipt shows the exact command to run in an admin terminal. Tattlebun never asks for elevation. This gives up one-click undo of machine-wide changes.
- **Old values are kept, encrypted.** A dotfile or variable can only go back if its old value was kept. Tattlebun keeps the version from before each change, encrypted with DPAPI for the current user, and deletes it after 30 days or when you dismiss the receipt. Diffs mask values of keys that look like secrets (token, password, auth). This gives up the strictest promise, never storing contents, because `.npmrc` and user variables can hold tokens.
- **Claude Code CLI only in version one,** as Sweepling started. Its hooks give the design everything it needs, and it's what the owner runs most. The snapshot engine doesn't care which agent runs the command, so others come as more hooks. This gives up Codex and other agents at launch.
- **Its own app, not part of Sweepling.** Sweepling closes processes your sessions left running, and Tattlebun undoes what they changed, so each tells a smaller trust story. Per-command hooks don't need Sweepling's process trees, which was the reason to merge them. This gives up a shared tray icon and install.
- **The brand is a placeholder:** a wide-eyed bunny peeking over a receipt, with one ear perked, in `docs/brand/`. Its eyes are open where Shybun's are closed, and its accent is cornflower, since Shybun has pink, Staypaw orange and Sweepling gold. The wordmark is Fredoka SemiBold, as in the other apps. The real logo and the mascot are M0 owner items. *(The mascot half is superseded: Tilly is the mascot, under Decisions (2026-10-25).)*

Claude's calls, approved by the owner with the design:

- **Public, under Apache-2.0,** like Staypaw and Shybun. A tool that reads your configs and what your agents ran is easier to trust when people can read it.
- **Two programs.** `tattlebun-hook`, a small console exe that Claude Code runs around each command: it takes a snapshot, records what changed and exits. And the tray app, which shows the receipts and runs undo. This gives up a single exe, because the hook must start fast and stay small.
- **.NET 10, Avalonia 12 and NativeAOT,** laid out like Shybun: `Tattlebun.Core` (snapshots as data, the diff, blame and receipts, with no Win32), `Tattlebun.Windows` (reading and undoing each kind of change), `Tattlebun.Hook`, `Tattlebun.Desktop` and `Tattlebun.Tests`. Win32 calls go through CsWin32. Because Core calls no Win32, blame tests run on made-up snapshots. This gives up a smaller exe on a runtime Windows already ships.
- **Read state straight from the registry and disk,** never by running `npm ls -g`, `pip list` or `winget list`, and skip a part whose last-write time hasn't changed. That's how a snapshot stays under 100 ms. This gives up those tools' own view, such as packages a tool installed somewhere unusual.
- **One SQLite file in `%LOCALAPPDATA%\Tattlebun`** for the receipts, the last snapshot and the encrypted old values, in WAL mode, because parallel tool calls run several hooks at once. This gives up plain files a person could read.
- **The hooks ship as a Claude Code plugin you add yourself.** Tattlebun never edits `~/.claude/settings.json`, because it shouldn't make the kind of change it reports, and you see the hooks before they run. A hook that fails or times out doesn't block the command, which is Claude Code's default. This gives up a one-step setup.
- **No network code, so no in-app updater.** Updates come through winget and GitHub Releases, as in Staypaw. *(Superseded: updates apply on their own, under Decisions (2026-10-25).)*
- **Velopack Setup and a portable zip, signed through the Artifact Signing setup Staypaw uses.** *(The portable zip is superseded on 2026-10-25: no portable copy, under Decisions (2026-10-25).)* Setup installs per user and never asks for admin.
- **The spike comes first in M1,** not before Day 1. Day 1 is only docs, so they're cheap to change if the spike shows snapshots can't stay under 100 ms.

## Decisions (2026-10-25)

- **Tilly Stubbs is the mascot, beside the placeholder mark.** The owner's call. She is a front-desk clerk who notices every little change and tells you exactly what happened, fairly and kindly: the mark's bunny-over-a-receipt as a person, drawn in the owner's anime-pipeline. Receipts say "while", not "by", so she tells and never accuses. She appears in the README, in `docs/brand/tilly/` and in launch posts; whether her psst sticker goes on M2's notification is decided when M2 starts. The bunny mark stays the logo and the tray icon, and the real logo is still an owner item. Her art was drawn with an AI image model, and it is Apache-2.0 like the code; the README says both. Her one-of-these sticker stays out, because its see-through gaps show a dark page through it. Her cardigan (`#507DBF`) stays as drawn, near the mark's cornflower, rather than retinting her. This gives up a single mark, a sticker for "one of these" for now, and exclusive rights to her art: anyone may reuse or change it.
- **Updates apply on their own, as in Sweepling.** The owner's call. Velopack in the tray app checks GitHub Releases a minute after it starts and every 4 hours after, downloads in the background, and restarts into the update only while the review window is closed, so it never cuts off an undo. A tick in the tray menu turns it off, and a build run from source never checks. The update check is the only network call: Tattlebun's own code never references a networking assembly, and the hook doesn't reference Velopack, so a snapshot never touches the network. No telemetry. This gives up the strictest promise, no network code at all, and some size: Velopack and its HTTP stack grew Sweepling's exe from 19.9 to 25.6 MB.
- **No portable copy, as in Sweepling.** The owner's call. Velopack's per-user Setup is the only download. A copy that can't update itself would miss fixes. This gives up a zip for people who won't run an installer.
- **The snapshot around each command stands.** The M1 spike read the watch list, scheduled tasks aside, in 8-20 ms. See the spike's results under M1.
- **The hooks run in exec form.** Claude's call from the spike. Claude Code runs a command hook through Git Bash unless the hook lists `args`, and Git Bash added about 120 ms to each call, more than the whole budget. With `command` set to `tattlebun-hook.exe` and `args` listed, Claude Code starts the exe directly. This gives up shell syntax in the hook command, which the hook doesn't need.
- **The tray app lists scheduled tasks, not the hook.** The owner's call. Unelevated, the Task Scheduler's COM API is the only way to read them (its registry cache and `System32\Tasks` deny access), and a listing took 115-150 ms. The tray app lists them every minute, and a new task goes to the command that ran since the last listing, to "one of these" when several did, or to "not tied to a session". This gives up blame as sharp as the other changes get.
- **Claude Code's own files are ignored.** Claude's call from the spike. Claude Code writes `.claude.json.lock` and `.claude.json.tmp.*` at the top of the home folder while commands run, and receipts would blame agents for them. This gives up seeing those files change, which only Claude Code does.

## M0: Placeholder (as soon as possible)

- [x] Add `LICENSE` (Apache-2.0), `README.md`, `ROADMAP.md` and `AGENTS.md`.
- [x] Add the placeholder mark and its lockups to `docs/brand/`.
- [ ] Search trademarks for Tattlebun: USPTO. *(owner)*
- [x] The bunny mascot: Tilly Stubbs, from anime-pipeline, in `docs/brand/tilly/` and the README.
- [ ] Pick the real logo in a claude.ai design project, perhaps drawn from Tilly's bunny-head pin, and export `mark.svg` with a `-dark` variant. `scripts/make_lockup.py` rebuilds the lockups from it, and `scripts/make-icons.cs` the icon. *(owner)*
- [x] Scaffold `Tattlebun.Core`, `Tattlebun.Windows`, `Tattlebun.Hook`, `Tattlebun.Desktop` and `Tattlebun.Tests` on .NET 10 and Avalonia 12, with central package versions and warnings as errors.
- [x] A tray icon made from the mark, with Quit. No window yet. Load Avalonia's Fluent theme, or the tray menu opens empty (Staypaw's fix).
- [x] `tattlebun-hook` reads the hook's JSON from stdin, appends one line to a log and exits 0. Nothing else yet. The line holds the event, session, tool and tool call ID, never the command.
- [x] Design tokens: a spacing scale, a type scale, a palette with shades and a few shadow levels.
- [x] NativeAOT publish for `win-x64` of both programs, with no trim or AOT warnings. On 2026-10-25: `tattlebun-hook.exe` is 2.4 MB and ran a sample `PreToolUse` in 23-33 ms, with PowerShell's pipe included; `Tattlebun.exe` is 17.6 MB and idles in the tray at 39.5 MB working set and 13.8 MB private bytes.
- [x] Guardrail tests, each with a positive control: `Tattlebun.Core` references neither Avalonia nor any Win32 interop, Tattlebun's own code references no networking assembly, and only the tray app references Velopack.
- [x] CI on Windows: build, test, and secret scanning with the gitleaks release binary, checked against its published checksum and run with `--log-opts="--full-history HEAD"`.

**Done when:** CI is green on `main`, and `dotnet publish -r win-x64` gives a native tray exe that starts in the tray, and a native hook exe that exits 0 on a sample `PreToolUse` payload.

**Build items done 2026-10-25; the two owner items are still open.** [CI run 40190051808](https://github.com/hazeliscoding/tattlebun/actions/runs/40190051808) passed on `main`: build, tests, format, the NativeAOT publish of both programs with warnings as errors, the native hook on a sample `PreToolUse` (exit 0, nothing on stdout), and the secret scan. A deliberate `HttpClient` in Core failed `No_Tattlebun_assembly_references_a_networking_assembly` before the guard was committed. `scripts/tray.ps1` found the native exe's icon in the tray, opened its menu and quit it.

## M1: Receipts

- [x] **Spike:** a throwaway PowerShell hook in a scratch project that snapshots around each command and logs the diff and the time, run through one real agent session. Record what changed and how long snapshots took on the owner's PC. If they can't stay under 100 ms, reopen the per-command decision before building on it.
- [x] Snapshot readers for each item on the watch list, reading the registry and disk directly and skipping parts whose last-write time hasn't changed. Under nvm-windows, npm's global packages are in `%NVM_SYMLINK%\node_modules`, not `%APPDATA%\npm`. Values are kept only as keyed fingerprints. Installed apps are read every time, because an upgrade rewrites only the app's own key. On the owner's PC a full read took a median of 14.3 ms and a read reusing unchanged parts 8.5 ms (JIT, warmed up).
- [ ] The tray app lists scheduled tasks through the Task Scheduler's COM API every minute, and blames a new or removed task by the interval since its last listing.
- [x] Blame by interval in `Tattlebun.Core`, with tests for one command, two overlapping commands, no command, and a session that died mid-command. Intervals are counted in snapshot numbers, not clock time, so a clock that steps back can't move a change onto the wrong command.
- [x] Close a command that never gets its `PostToolUse`, or it stays a suspect for every later change. A denied call fires `PreToolUse` and nothing else; close it at its batch's `PostToolBatch`, which lists every call. An interrupted turn may skip that too; close the session's open commands at its next `UserPromptSubmit`. A session that died: close its commands when the Claude Code process its hook saw (`CLAUDE_PID`) is gone.
- [x] Settle background commands. A `run_in_background` command keeps running after its `PostToolUse`, so its later changes could land on another session's command. Try keeping its interval open while its processes live, found by `CLAUDE_CODE_SESSION_ID` in their environment, as Sweepling does. *Done another way:* reading every process's environment at each snapshot would cost too much. A background call's `PostToolUse` carries `backgroundTaskId`, and its shell is a child of Claude Code (`CLAUDE_PID`) that started after the command's snapshot. The command stays open until those children have all exited, checked by ID and start time at each snapshot. Another call's short-lived process can slip in and hold it open a few seconds longer.
- [x] Record the path of each Write and Edit call outside the session's working folder. It joins the watch list before that call's snapshot and stays on it.
- [x] Receipts and the last snapshot in SQLite (WAL), safe with parallel hooks. Each hook takes the write lock (`BEGIN IMMEDIATE`) before it reads its snapshot. `PostToolUseFailure` closes a command too. First numbers on the owner's PC, native, direct spawn: a median of 66.5 ms over 60 calls, but only 50 under 100 ms (p95 173 ms, cold first call 190 ms). Profile it before M1's Done when.
- [x] A Claude Code plugin in this repo whose hooks run `tattlebun-hook` in exec form for Bash, PowerShell, Write and Edit. It also hooks `PostToolUseFailure`, `PostToolBatch` and `UserPromptSubmit`, which close unfinished commands. Installing it asks once for the path of `tattlebun-hook.exe`, since exec form can't expand `%LOCALAPPDATA%`.
- [x] `tattlebun-hook --list` prints recent receipts, for checking before the window exists.

**Done when:** in a real session, `npm i -g` of a small package shows up on its command's receipt; a command that changes nothing gets an empty receipt; overlapping commands from two sessions both get "one of these"; and over 200 commands on the owner's PC, 95% of snapshots take under 100 ms.

**Progress (2026-10-25).** Headless Claude Code sessions ran through the plugin (loaded with `--plugin-dir`, so no settings changed) and the native hook. `npm i -g is-number` landed on its command's receipt, and its uninstall on the uninstall's. Each `git --version` got an empty receipt. With one session's command still running, another session's install landed as "one of these" with both. A call the session denied started and ended at the same snapshot, so it was never a suspect. A background `sleep` stayed open past its `PostToolUse` until the first snapshot after its shell exited. Timing isn't met yet: `--list` reported 93% of 29 hooks under 100 ms (95th percentile 158 ms), with three sessions running at once. The owner's 200 commands decide it.

**Spike results (2026-10-25).** The hook was a throwaway C# exe published with NativeAOT, not PowerShell: in PowerShell the same snapshot took 560-745 ms, mostly PowerShell itself, which says nothing about the native hook. A headless Claude Code session (2.1.310) ran a no-op, set a user variable, added an `HKCU` Run entry, ran `npm i -g`, wrote a file at the top of the home folder, and undid each. Every change landed on the receipt of the command that made it, and the no-op's receipt was empty.
- Reading the watch list without scheduled tasks (1,360 items: variables, uninstall keys, packages, startup entries, 801 services, dotfiles and the top of the home folder) took 8-20 ms inside the process. Diffing against a text file took another 13-54 ms; SQLite and skipping unchanged parts replace that.
- From process start to the last log line, calls took 64-88 ms in exec form after a cold first call of 322 ms. Timed from outside under the same load, the near-empty `tattlebun-hook` took a median of 28 ms, so starting the process is most of the cost.
- Through Git Bash the same call took a median of 194 ms against 75 ms direct, with none of 40 under 100 ms. Hence exec form, under Decisions (2026-10-25).
- Clearing a variable with `[Environment]::SetEnvironmentVariable(name, $null, 'User')` in PowerShell left it set to an empty string, because PowerShell passes `$null` to a string parameter as `""`. The receipt said "changed", not "removed", which was right. Tests delete a value with `Remove-ItemProperty` or the registry API instead.
- Two overlapping commands weren't tested: the agent ran the parallel step one call at a time. Blame tests on made-up intervals cover it.

## M2: Review window

- [ ] The tray icon counts changes since you last looked, and notifies when the count rises.
- [ ] A window that lists sessions by project folder and start time, their commands, and each change in words.
- [ ] Sections for "one of these" and "not tied to a session".
- [ ] Dotfile and variable diffs, with secret-looking values masked.
- [ ] Dismiss a receipt.

**Done when:** tests cover each section and the masking, and the window shows a real receipt from an M1 session.

## M3: Undo

- [ ] Undo each kind of change that doesn't need admin: a user variable or PATH entry, an `npm -g`, `pip --user` or `dotnet tool` package, an `HKCU` startup entry, a scheduled task you created, a user-scope install (through its uninstaller), and a dotfile.
- [ ] Before undoing, check the current state again, and refuse if it changed after the receipt, so a newer change isn't overwritten.
- [ ] Changes that need admin show the exact command, ready to copy.
- [ ] Keep old values encrypted with DPAPI for the current user, and delete them after 30 days or when the receipt is dismissed.
- [ ] Log every undo with what it changed back.

**Done when:** an explicit test makes each kind of change that doesn't need admin, undoes it through Tattlebun and finds the snapshot from before; an undo refuses when the state changed after the receipt; and no old value is readable from the database without DPAPI.

## M4: 1.0

- [ ] A Velopack per-user Setup, built by a release workflow. Velopack installs into `%LOCALAPPDATA%\Tattlebun` and deletes it on uninstall, and the receipt store lives there too: decide whether receipts and encrypted old values go with an uninstall, and keep Setup from installing into a folder that already holds the store.
- [ ] Automatic updates through Velopack, as in Sweepling: a check a minute after start and every 4 hours, held while the review window is open, with an "Update automatically" tick in the tray menu.
- [ ] Sign releases with Artifact Signing through OIDC, from a `release` environment that only `v*` tags can deploy to, as in Staypaw.
- [ ] Scan the published exes with Microsoft Defender.
- [ ] Publish the plugin from this repo, with install steps in the README.
- [ ] Submit a winget manifest.
- [ ] A screenshot and a short demo in the README.

**Done when:** `winget install hazeliscoding.Tattlebun` installs a signed build that starts in the tray, and after adding the plugin, an agent's `npm i -g` shows up in the tray with Undo; and an installed release updates itself to a later test release.

## Later

- Codex and other agents, as more hooks on the same engine.
- A filtered watcher on the home folder, if receipts show the fixed list misses things.
- A link from Sweepling's leftovers to the receipt of the session that started them.
- Undo of machine-wide changes through a separate elevated step, if copying commands proves annoying.
- Copy a receipt as Markdown, to paste into a pull request.
- Claude Code's session scratchpads in `%TEMP%\claude`, one folder per session, offered for deletion once their session has ended. On 2026-10-25 the owner's PC had 21.4 GB in 464 of them, 351 untouched for a week. The largest, 13.4 GB, belonged to a session still writing to it, so neither size nor age can decide which ones are stale.
- Tilly's one-of-these sticker, re-rolled in anime-pipeline to close its see-through gaps and bring back its sweat drop, and her all-clear sticker for an empty receipt. Both need a new image budget there; all-clear's preset is ready in her spec.

## Not planned

- **Any network call besides the update check:** telemetry, crash reporting or accounts.
- **Running elevated, or ETW tracing,** which needs admin. See Decisions.
- **Watching the whole disk,** or reading files outside the watch list.
- **macOS and Linux.** Claude Code's sandbox can already confine agents there.
- **Blocking commands before they run.** Tattlebun reports and undoes. Claude Code's permission rules already decide what may run.

## How we'll know it works

- The owner's dogfooding: wrong blames, where a receipt pins a change on a command that didn't make it, must stay at zero.
- The hook's own timings: 95% of snapshots under 100 ms.
- Public signals after release: issues, downloads and winget installs. No interviews or outreach.
