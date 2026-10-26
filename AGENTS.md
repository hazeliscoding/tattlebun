# AGENTS.md

These are the working rules for agents in this repo. Tattlehop is a Windows tray app (.NET 10, Avalonia 12, NativeAOT, Apache-2.0) that keeps a receipt of what coding agents changed on the PC outside the repo, and undoes those changes once you confirm.

## Sources of truth

- `README.md`: the pitch and the trust contract.
- `ROADMAP.md`: decisions already made, the milestones, and what is out of scope. Check it before proposing features. Respect those decisions unless the owner reopens them.
- Work from the next unchecked item in `ROADMAP.md`. Don't build past the current milestone without asking.

## The trust contract (hard rules)

Tattlehop reads people's environment, configs and what their agents ran. They run it only if they can trust it, so never break these, not even in dev tooling that ships.

- **Read-only until an undo is confirmed.** A snapshot writes nothing outside Tattlehop's own folder in `%LOCALAPPDATA%`. A tool that reports what changed on your PC can't make changes of its own.
- **No network call besides the update check.** Velopack in the tray app checks GitHub Releases, and that's all: no telemetry, and nothing about the PC is sent. Tattlehop's own code never references a networking assembly (`HttpClient`, sockets), and the hook never references Velopack; a test enforces both. Receipts describe someone's machine, and they never leave it.
- **Never store a file's contents or a variable's value in plain text.** Old values are kept only so undo can put them back, encrypted with DPAPI for the current user, and deleted after 30 days or when the receipt is dismissed. Never write a value, a diff line or a command's output to a log, a test fixture or a debug dump. `.npmrc` and user variables often hold tokens.
- **Mask secrets on screen.** Values of keys that look like secrets (token, password, auth, key) are masked in diffs and commands. The owner may screen-share a receipt.
- **Never block the agent.** `tattlehop-hook` catches every failure, exits 0 and prints no decision. Exit code 2 from a `PreToolUse` hook blocks the tool call, so never return it. A snapshot has a budget of 100 ms: never shell out to `npm`, `pip`, `winget` or `reg.exe` from the hook, and keep the plugin's hooks in exec form (`command` plus `args`), because Claude Code otherwise starts them through Git Bash, which added about 120 ms a call.
- **Never edit Claude Code's settings** or anything else under `~/.claude`. The hooks reach Claude Code only through the plugin the user adds. Tattlehop mustn't make the kind of change it reports.
- **Fair blame.** Tie a change to a command only when that command's interval covers it. When several commands were running, say "one of these"; when none was, say "not tied to a session". A wrong blame is worse than none: it sends people after the wrong agent.
- **Undo only what the person ticked and confirmed,** and check the current state first. If it changed after the receipt, refuse instead of overwriting the newer change.
- **Run unelevated.** Don't set `requireAdministrator` in the manifest, and never start an elevated process or a UAC prompt. A change that needs admin gets a command the person runs themselves.
- **No code in other processes.** No DLL injection, `CreateRemoteThread` or hook DLLs. Work laptops ban it, and nothing here needs it.

## Code

- .NET 10 with nullable on and warnings as errors. Package versions live in `Directory.Packages.props`.
- `Tattlehop.Core` holds snapshots as data, the diff, blame by interval and receipts. It never references Avalonia or any Win32 interop; a test will enforce it. Blame takes snapshots, intervals and times as inputs, so its tests run on made-up data.
- `Tattlehop.Windows` reads and undoes each kind of change. Read the registry and disk directly, and skip a part whose last-write time hasn't changed.
- Win32 calls go through CsWin32 (`NativeMethods.txt`), not hand-written `DllImport`. The generated code is typed and NativeAOT-safe.
- NativeAOT must stay free of warnings: no reflection-based serialization. Use source generators, including for the hook's JSON.

## Brand

- The logo is a placeholder bunny-and-receipt mark. The real logo comes from a claude.ai design project, an owner item. Don't redraw the mark here.
- Tilly, in `docs/brand/tilly/`, is the mascot. Never edit, recolour or redraw her here: her images come from one written spec and one reference image in the owner's anime-pipeline, and an image changed by hand drifts from the rest of the set. New stickers are drawn there.
- The wordmark is outlined Fredoka SemiBold. Use the lockup SVGs; don't re-typeset it with a web font. `scripts/make_lockup.py` rebuilds them from the mark, so rerun it when the mark changes.

## Dependencies

- **A special-purpose library** (a parser, an installer framework): add it yourself, with its reason in the commit message.
- **A general-purpose helper:** only after checking that nothing in the project already does the job.
- **A framework:** never without asking the owner.
- **Anything that brings in network code:** never, besides Velopack in the tray app. See the trust contract.

## Glossary

One meaning per term. Use these words in code, docs and commits.

- **Session:** one Claude Code session, known by its session ID.
- **Command:** one tool call in a session that Tattlehop watches: Bash, PowerShell, Write or Edit.
- **Snapshot:** the state of everything on the watch list at one moment.
- **Watch list:** the places a snapshot covers. See "What it watches" in the README.
- **Interval:** the time between two snapshots, from any session.
- **Change:** one difference between two snapshots, such as a PATH entry added or a package removed.
- **Receipt:** the changes tied to one command.
- **One of these:** a change found while several commands were running.
- **Not tied to a session:** a change found while no command was running.
- **Old value:** what a changed dotfile or variable held before, kept encrypted for undo.
- **Undo:** putting one change back, after the person confirms.

## Commands

- `uv run --no-project --with fonttools --with uharfbuzz python -I scripts/make_lockup.py <Fredoka-SemiBold.ttf> docs/brand` rebuilds the lockups. Its header says where to get the font.
- `dotnet run scripts/make-icons.cs` rebuilds `docs/brand/tattlehop.ico` from the mark.
- `dotnet publish src/Tattlehop.Desktop -c Release -r win-x64 -o publish/Tattlehop.Desktop` builds the native tray exe, and the same with `src/Tattlehop.Hook` builds `tattlehop-hook.exe`. It needs Visual Studio's C++ workload. If linking fails with "'vswhere.exe' is not recognized", put `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` on `PATH`: VS 18's `vcvarsall.bat` calls `vswhere.exe` by bare name, and the error text lands in the linker path.
- `dotnet test --solution Tattlehop.slnx` runs the tests.
- `powershell -NoProfile -File scripts/tray.ps1 [<screenshot.png>]` checks a running Tattlehop through UI Automation: it finds the tray icon, opens its menu, optionally saves a screenshot, and chooses Quit Tattlehop. Avalonia draws the menu as a `TrayPopupRoot` window at the cursor, so the script restores the cursor only once the menu is open.

## Working style

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `chore:`, `test:`, `ci:`, `build:`, `refactor:`). Keep each commit atomic, and use a scope when it adds clarity (`feat(hook): …`).
- **No AI attribution** in commits or PRs. That means no `Co-Authored-By` trailers, no "Generated with" lines and no session links.
- **Checks:** automate acceptance checks instead of handing manual steps to the owner. Give every check that tests for an absence a positive control. For example, the network guard must fail on a deliberate `HttpClient`.
- **Structural rules get tests.** When a decision is a rule about the code's shape, add a test that fails when it's broken.
- **Tests that change the real PC** (a variable, a package, a startup entry) are explicit tests, use names that start with `TattlehopTest`, clean up after themselves, and never touch the owner's real dotfiles. Delete a variable through the registry API or `Remove-ItemProperty`: from PowerShell, `[Environment]::SetEnvironmentVariable(name, $null, 'User')` leaves an empty value behind. A test that leaves junk behind is the very thing Tattlehop exists to catch.
- **Validation:** evidence comes from dogfooding and public signals (issues, downloads). Don't plan interviews, recruiting or outreach.
- **Docs:** short and concise. Prefer editing `ROADMAP.md` over creating new planning documents.
- **Code comments:** explain why, not what. Comment only what the code can't say for itself: a non-obvious constraint, a Win32 or Claude Code quirk and its cause, or a line that keeps the trust contract. Don't restate names or types, and don't leave commented-out code.
- Local Playwright output goes to `.playwright-mcp/`, which git ignores.
