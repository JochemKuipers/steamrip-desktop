# AGENTS.md

Working agreements for anyone (human or AI agent) changing this repository. Read this before touching code. The `docs/` folder holds the research and design; this file holds the rules.

## What this project is

A personal, single-user desktop app that keeps a game library synced with steamrip.com: catalog sync, hoster resolution, download, install, launch (Proton via umu), auto-update with save backup. C# / .NET 10, Avalonia 12, SQLite. Linux first (KDE/Wayland dev box), Windows must keep compiling and is structured to work.

The owner wants **zero manual work** in the happy path. Any change that adds a prompt, a manual step, or a "go configure X in another tool" is a regression unless it is a genuine safety stop (destructive action, interactive CAPTCHA).

## Layout

```
SteamRip.sln
Directory.Build.props              # net10.0, nullable, implicit usings, warnings as errors off
src/SteamRip.Core/                 # everything testable; no Avalonia reference
  Catalog/      REST client, post/title parsing, hoster detection
  Solver/       FlareSolverrClient (sessions, request.get, actions), BrowserSolver (Playwright, headed)
  Hosters/      one IHostResolver per hoster + ResolverChain + HostPages (pure HTML parsing)
  Downloads/    Downloader (Range resume, .part, hash verify)
  Install/      ArchiveTool (unrar/7z), SaveLocator, Installer (staging → backup → swap)
  Launch/       ExeFinder, ProtonLocator, GameLauncher (umu-run / wine / direct)
  Updates/      UpdateService (queue, scheduler, play), UpdateDetector, Job, ManualSolve
  Data/         Database (hand-written migrations), Settings (JSON)
  Platform/     ISecretStore, Notifier, Autostart (Linux + Windows shims)
  Logging/      FileLoggerProvider
src/SteamRip.App/                  # Avalonia: App.axaml(.cs) with TrayIcon + AppServices, ViewModels/, Views/
tests/SteamRip.Core.Tests/         # xunit; Fixtures/ holds saved HTML/JSON from real hosters
docs/                              # research, hosters, architecture, decisions
```

Keep this shape. Do not add a project per hoster, a plugin system, DI container, or EF Core. `Core` must stay free of Avalonia.

## Build, test, run

```sh
dotnet tool restore                            # once per clone: CSharpier + ReSharper CLI (dotnet-tools.json)
git config core.hooksPath .githooks            # once per clone: pre-commit format+build, pre-push full gate
dotnet build                                   # whole solution; analyzers on, warnings are errors
dotnet test                                    # 34 tests, ~1 s, no network
dotnet run --project src/SteamRip.App -- --show --verbose
```

## Code quality gates

Three tools, one source of truth (`.editorconfig`), all of them wired into the git hooks and `.gitlab-ci.yml`:

| Gate | Command | Owns |
| --- | --- | --- |
| CSharpier | `dotnet csharpier format .` / `check .` | All whitespace and line breaking in `.cs` and `.axaml` (`.csharpierrc.yaml`: width 100). Never hand-format; never argue with it. |
| Roslyn analyzers | `dotnet build` | `AnalysisLevel=latest-recommended` + IDE style rules from `.editorconfig`, `TreatWarningsAsErrors`. Severities and the reasons for every disabled rule are in `.editorconfig`. |
| ReSharper | `scripts/inspect.sh` | JetBrains inspections at WARNING+, same engine as the IDE extension. The `resharper_*` block in `.editorconfig` makes the IDE, the script and CI agree and stops ReSharper's formatter fighting CSharpier. |

Rules of engagement:
- Fix the code, not the rule. Downgrade a rule in `.editorconfig` only with an inline reason, in the same commit.
- Naming: PascalCase for constants and `static readonly` fields, `_camelCase` for other private fields, `I` prefix for interfaces. Both Roslyn and ReSharper enforce this (the ReSharper variant needs `resharper_applicable_kinds`, see the comments).
- Braces are not enforced either way; CSharpier wraps long single statements, so "multiline body" rules would fire constantly.
- Suppress a one-off ReSharper finding with `// ReSharper disable once <Id> -- <why>`; Roslyn with `[SuppressMessage(..., Justification = "...")]`. Both need the reason.
- The pre-commit hook formats staged files and builds; the pre-push hook also runs the tests and ReSharper (~1 min). Skip with `--no-verify` only when you are about to fix it in the next commit.

Use a throwaway profile when running the app during development so you never touch the owner's real library and DB:

```sh
XDG_CONFIG_HOME=/tmp/sr-cfg dotnet run --project src/SteamRip.App -- --show --verbose
# settings at /tmp/sr-cfg/steamrip-desktop/settings.json; set LibraryRoot to e.g. /tmp/sr-lib
```

## How to verify your work

**Read the log, not the screen.** The app writes `steamrip-YYYYMMDD.log` to `<config>/logs/`. Every stage (sync, resolve, download, unrar, extract, swap, launch, stop) logs at Information; `--verbose` adds Debug with URLs (signed URLs redacted) and timings. Taking desktop screenshots to check UI state is not acceptable: it captures whatever the user has on screen.

Also usable: `sqlite3 <config>/steamrip.db "select * from installs"` and the `events` table.

For Core changes, prefer a fixture-driven unit test. For live hoster behaviour, write a throwaway console harness **outside the repo** (`/tmp/steamrip-spikes/…`) that references `src/SteamRip.Core/SteamRip.Core.csproj`. Nothing under `/tmp` is committed; nothing experimental lands in the repo.

A change to a resolver is not done until it has resolved and downloaded at least the first megabyte of a real file through `ResolverChain` + `Downloader`, and that run is visible in a log.

## Conventions

- Conventional commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`). One concern per commit.
- `ILogger` everywhere in Core, injected through the constructor, defaulting to `NullLogger`. Never `Console.WriteLine` in Core.
- Exceptions: `SolverException`, `HostResolveException(Host, message)`, `DownloadException(retryResolve)`, `LaunchException`, `NeedsAttentionException`. Throw these, not generic ones, so `UpdateService` can decide retry vs. next mirror vs. "needs you".
- Parsing lives in `HostPages`/`CatalogParser`/`TitleParser` as pure functions over strings, tested against `tests/SteamRip.Core.Tests/Fixtures/`. When a hoster changes its page, save the new HTML as a fixture first, then fix the parser.
- Settings are a flat JSON class (`Data/Settings.cs`). Add a property with a sensible default; no migration needed.
- DB schema changes go in `Database.Migrations[]` as a new entry (append only, never edit a shipped one). Add the column to the matching record and to the SELECT/INSERT in `Database.cs`.
- Mark knowingly-limited code with a `// ponytail: <what the ceiling is>` comment, e.g. the GoFile salt, the FileDitch expiry assumption, the save-dir heuristic.
- `Hoster` enum values are stable identifiers used in `Settings.HosterOrder`; renaming breaks user configs.
- UI: CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), DataTemplates keyed by ViewModel type, styles in `Views/Styles.axaml`, palette in `App.axaml` (ink `#141821`, amber `#F2B544`). No code-behind logic beyond window plumbing.

## Things not to do

- Do not scrape steamrip.com HTML for the catalog. The REST API (`/wp-json/wp/v2/posts`) is the source; HTML parsing is limited to the `shortc-button` anchors and the "Game Size" line inside `content.rendered`. See `docs/research.md`.
- Do not hammer the site. One request in flight, 1.5 s between REST pages, honour `Retry-After`; the site returns 429 after roughly 40 consecutive pages. A full sync is rare; incremental `orderby=modified` checks are the norm.
- Do not route file bytes through FlareSolverr. It clears landing pages only; every download goes through `HttpClient` with `Range`.
- Do not trust hoster-published hashes blindly. MegaDB's page hash does not match the file; the archive CRC test (`unrar t`) is the integrity check. BuzzHeavier's MD5/SHA-256 do match and are verified.
- Do not open a browser window unprompted. Headed-browser solving happens only when the user clicks "Solve in browser".
- Do not delete the old install before the new one passed `unrar t` and extracted fully. Do not delete anything that looks like a Wine prefix (`drive_c`/`system.reg`) — move it into the new tree.
- Do not scan Heroic/Lutris/Steam prefixes for saves. The app owns the prefix at `<game>/prefix`; that decision is recorded in `docs/decisions.md`.
- Do not edit the planning file under `~/.cursor/plans/` if you have access to it; it is a historical record.
- Do not add debrid support, a web UI, torrents, or multi-user features without the owner asking.

## Machine facts (dev box, Nov 2026)

CachyOS, KDE on Wayland, .NET 10.0.114, `unrar`, `7z`, `secret-tool`, `gdbus`, `umu-run` 1.4.3, `wine`. Proton builds at `/usr/share/steam/compatibilitytools.d/proton-cachyos-{slr,native}`, `~/.steam/root/compatibilitytools.d/Proton-CachyOS Latest`, `~/.local/share/Steam/steamapps/common/Proton - Experimental`. FlareSolverr (smeinecke fork, Chrome 154) at `http://192.168.2.251:8191`. Only Firefox is installed; Playwright downloads its own Chromium on first manual solve.

## When you finish

Run `dotnet build && dotnet test && scripts/inspect.sh`, exercise the changed path once with the throwaway profile and confirm it in the log, commit with a conventional message, and update the relevant `docs/` page if behaviour or a hoster protocol changed. Documentation that disagrees with the code is worse than none.
