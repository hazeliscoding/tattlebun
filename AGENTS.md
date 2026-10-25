# AGENTS.md

These are the working rules for agents in this repo. Tattlebun is a Windows tray app (.NET 10, Avalonia 12, NativeAOT, Apache-2.0) that keeps a receipt of what coding agents changed on the PC outside the repo, and undoes those changes once you confirm.

## Sources of truth

- `README.md`: the pitch and the trust contract.
- `ROADMAP.md`: decisions already made, the milestones, and what is out of scope. Check it before proposing features. Respect those decisions unless the owner reopens them.
- Work from the next unchecked item in `ROADMAP.md`. Don't build past the current milestone without asking.

## The trust contract (hard rules)

Tattlebun reads people's environment, configs and what their agents ran. They run it only if they can trust it, so never break these, not even in dev tooling that ships.

- **Read-only until an undo is confirmed.** A snapshot writes nothing outside Tattlebun's own folder in `%LOCALAPPDATA%`. A tool that reports what changed on your PC can't make changes of its own.
- **No networking code.** No `HttpClient`, sockets, updater or telemetry, and no package that brings them in. A test will fail on any `System.Net` use in Tattlebun's assemblies. Receipts describe someone's machine, and they never leave it.
- **Never store a file's contents or a variable's value in plain text.** Old values are kept only so undo can put them back, encrypted with DPAPI for the current user, and deleted after 30 days or when the receipt is dismissed. Never write a value, a diff line or a command's output to a log, a test fixture or a debug dump. `.npmrc` and user variables often hold tokens.
- **Mask secrets on screen.** Values of keys that look like secrets (token, password, auth, key) are masked in diffs and commands. The owner may screen-share a receipt.
- **Never block the agent.** `tattlebun-hook` catches every failure, exits 0 and prints no decision. Exit code 2 from a `PreToolUse` hook blocks the tool call, so never return it. A snapshot has a budget of 100 ms: never shell out to `npm`, `pip`, `winget` or `reg.exe` from the hook.
- **Never edit Claude Code's settings** or anything else under `~/.claude`. The hooks reach Claude Code only through the plugin the user adds. Tattlebun mustn't make the kind of change it reports.
- **Fair blame.** Tie a change to a command only when that command's interval covers it. When several commands were running, say "one of these"; when none was, say "not tied to a session". A wrong blame is worse than none: it sends people after the wrong agent.
- **Undo only what the person ticked and confirmed,** and check the current state first. If it changed after the receipt, refuse instead of overwriting the newer change.
- **Run unelevated.** Don't set `requireAdministrator` in the manifest, and never start an elevated process or a UAC prompt. A change that needs admin gets a command the person runs themselves.
- **No code in other processes.** No DLL injection, `CreateRemoteThread` or hook DLLs. Work laptops ban it, and nothing here needs it.

## Code

- .NET 10 with nullable on and warnings as errors. Package versions live in `Directory.Packages.props`.
- `Tattlebun.Core` holds snapshots as data, the diff, blame by interval and receipts. It never references Avalonia or any Win32 interop; a test will enforce it. Blame takes snapshots, intervals and times as inputs, so its tests run on made-up data.
- `Tattlebun.Windows` reads and undoes each kind of change. Read the registry and disk directly, and skip a part whose last-write time hasn't changed.
- Win32 calls go through CsWin32 (`NativeMethods.txt`), not hand-written `DllImport`. The generated code is typed and NativeAOT-safe.
- NativeAOT must stay free of warnings: no reflection-based serialization. Use source generators, including for the hook's JSON.

## Brand

- The logo is a placeholder bunny-and-receipt mark. The real logo and the mascot come from a claude.ai design project, an owner item. Don't redraw the mark here.
- The wordmark is outlined Fredoka SemiBold. Use the lockup SVGs; don't re-typeset it with a web font. `scripts/make_lockup.py` rebuilds them from the mark, so rerun it when the mark changes.

## Dependencies

- **A special-purpose library** (a parser, an installer framework): add it yourself, with its reason in the commit message.
- **A general-purpose helper:** only after checking that nothing in the project already does the job.
- **A framework:** never without asking the owner.
- **Anything that brings in network code:** never. See the trust contract.

## Glossary

One meaning per term. Use these words in code, docs and commits.

- **Session:** one Claude Code session, known by its session ID.
- **Command:** one tool call in a session that Tattlebun watches: Bash, PowerShell, Write or Edit.
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

## Working style

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `chore:`, `test:`, `ci:`, `build:`, `refactor:`). Keep each commit atomic, and use a scope when it adds clarity (`feat(hook): …`).
- **No AI attribution** in commits or PRs. That means no `Co-Authored-By` trailers, no "Generated with" lines and no session links.
- **Checks:** automate acceptance checks instead of handing manual steps to the owner. Give every check that tests for an absence a positive control. For example, the network guard must fail on a deliberate `HttpClient`.
- **Structural rules get tests.** When a decision is a rule about the code's shape, add a test that fails when it's broken.
- **Tests that change the real PC** (a variable, a package, a startup entry) are explicit tests, use names that start with `TattlebunTest`, clean up after themselves, and never touch the owner's real dotfiles. A test that leaves junk behind is the very thing Tattlebun exists to catch.
- **Validation:** evidence comes from dogfooding and public signals (issues, downloads). Don't plan interviews, recruiting or outreach.
- **Docs:** short and concise. Prefer editing `ROADMAP.md` over creating new planning documents.
- **Code comments:** explain why, not what. Comment only what the code can't say for itself: a non-obvious constraint, a Win32 or Claude Code quirk and its cause, or a line that keeps the trust contract. Don't restate names or types, and don't leave commented-out code.
- Local Playwright output goes to `.playwright-mcp/`, which git ignores.
