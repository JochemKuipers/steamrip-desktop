# SteamRIP Desktop

A tray-resident desktop app (C# / Avalonia, Linux first, Windows supported) that tracks [steamrip.com](https://steamrip.com), downloads games from its file hosters, installs them into a library folder, launches them through Proton in a per-game Wine prefix, and keeps them updated automatically while backing up saves.

Personal-use project. The code is what one person needs to run a game library hands-off; it is not a general scraping framework.

## What it does

- **Catalog**: syncs the site through the WordPress REST API (no HTML scraping of the site itself), detects updated posts by `modified` + build/version change.
- **Download**: resolves a post's mirrors (FileDitch, GoFile, MegaDB, BuzzHeavier) to a direct URL, clears Cloudflare via a [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr) instance, downloads with resume (`Range` + `.part`), verifies hashes where the hoster publishes them.
- **Install**: `unrar t`/`7z t` integrity check → extract to staging → back up saves → swap into `<library>/<slug>/` → prune old backups. The old install is untouched until the new one is verified.
- **Play**: `umu-run` + Proton with `WINEPREFIX=<game>/prefix`, exe auto-detected, per-game Proton/exe/argument overrides, playtime tracking. On Windows the exe runs directly.
- **Update**: periodic check; auto-downloads and reinstalls library games; waits for a running game to exit first; prefix and saves survive the swap.
- **Tray**: open library / check now / pause auto-updates / quit; OS notifications; autostart; hidden start.
- **Logging**: daily rotating log files under the config dir; live tail in the Activity view.

## Requirements

| Linux | Windows |
| --- | --- |
| .NET 10 SDK (to build) | .NET 10 SDK (to build) |
| `unrar` or `7z` on PATH | 7-Zip or WinRAR installed, or `7za.exe` next to the binary |
| `umu-run` (umu-launcher) and/or `wine`; a Proton build (Steam, Proton-GE, Proton-CachyOS) | — |
| `secret-tool` (optional, keychain) | — |
| `notify-send` or `gdbus` (notifications) | — |
| A reachable FlareSolverr (`http://host:8191`) for FileDitch and BuzzHeavier mirrors | same |

GoFile and MegaDB mirrors work without FlareSolverr. If no solver is reachable, the queue item waits in "needs you" and offers a one-click "Solve in browser" (headed Chromium through Playwright).

## Build and run

```sh
dotnet build
dotnet test
dotnet run --project src/SteamRip.App -- --show --verbose
```

Flags: `--show` (open the window even if "start hidden" is on), `--hidden`, `--verbose` (debug-level log).

Publish self-contained single-file binaries:

```sh
dotnet publish src/SteamRip.App -c Release -r linux-x64
dotnet publish src/SteamRip.App -c Release -r win-x64
```

## Where things live

| | Linux | Windows |
| --- | --- | --- |
| Config, DB, logs | `~/.config/steamrip-desktop/` (`settings.json`, `steamrip.db`, `logs/`) | `%APPDATA%\steamrip-desktop\` |
| Library (default) | `~/Games/SteamRIP/<slug>/` | `%USERPROFILE%\Games\SteamRIP\<slug>\` |
| Game prefix | `<library>/<slug>/prefix/` | n/a |
| Downloads (default) | `~/Games/SteamRIP/.downloads/<slug>/` | `%USERPROFILE%\Games\SteamRIP\.downloads\<slug>\` |
| Staging during install | `<library>/.staging/<slug>/` | same |
| Save backups | `<library>/.backups/<slug>/<old-version>/` | same |

Override the config directory with `XDG_CONFIG_HOME` (handy for a throwaway test profile).

## Documentation

- [`docs/research.md`](docs/research.md) — what we learned about the site, its API, hosters, Cloudflare, rate limits; the prior art we read.
- [`docs/hosters.md`](docs/hosters.md) — the exact resolution protocol per hoster, verified end to end, with the gotchas.
- [`docs/architecture.md`](docs/architecture.md) — pipeline, components, DB schema, settings, install/update/launch flows, logging, secrets.
- [`docs/decisions.md`](docs/decisions.md) — decision log, including the two course corrections (logging over screenshots; the app launches games itself).
- [`AGENTS.md`](AGENTS.md) — working agreements for contributors and AI agents: layout, conventions, how to verify, what not to do.

## Status

Verified on CachyOS (KDE/Wayland, .NET 10.0.114, umu-launcher 1.4.3). The Windows code paths (DPAPI secrets, PowerShell toast, HKCU autostart, direct exe launch) compile and are structured for it but have not been run on a Windows machine yet.
