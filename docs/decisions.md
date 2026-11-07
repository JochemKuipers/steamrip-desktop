# Decision log

Short ADR-style entries. Newest at the bottom. Each records the context, the choice, what it locks in, and what would make us revisit it.

## D1 — Scope: Linux first, Windows must work, macOS later (Nov 6 2026)

Owner's dev box is CachyOS/KDE/Wayland; a Windows machine exists. Platform shims (autostart, notifications, archive tool, secrets, launcher) are implemented for both behind the smallest interfaces; nothing macOS-specific exists. Revisit when a Mac appears.

## D2 — Single process

Avalonia app + `TrayIcon` + background tasks in one process. A worker/UI split would have doubled autostart, IPC and lifetime handling for no stated need (downloads do not have to outlive the UI; no headless target). Revisit if a NAS/headless worker is wanted.

## D3 — Catalog via WP REST API, not HTML scraping

The site exposes `/wp-json/wp/v2/posts` with `acf.tagmetafield` (Steam build) and `steam_app_id`; posts update in place. HTML parsing is limited to the `shortc-button` anchors and the Game Size line inside `content.rendered`. Sitemap `lastmod` and the A–Z list are documented fallbacks. Politeness: one request in flight, 1.5 s between pages, honour `Retry-After` (429 appears after ~40 pages), incremental checks on the first pages only.

## D4 — Hosters and order: FileDitch, GoFile, MegaDB, BuzzHeavier; no 1fichier/FileCrypt; debrid parked

Coverage from the 100-post sample made FileDitch+GoFile+MegaDB sufficient; BuzzHeavier was conditional on a spike and the spike passed (session + referer hop + HTMX fetch), so it is included last because it is slowest. 1fichier (4 %) and FileCrypt (3 %) always co-occur with a supported mirror. Real-Debrid covers only 1fichier; TorBox covers BuzzHeavier. Revisit debrid only if the site moves hosts.

## D5 — Cloudflare: FlareSolverr first, headed browser only on click

The owner already runs FlareSolverr (smeinecke fork) on the LAN, so it is a sunk dependency and solved FileDitch in ~7 s. It clears landing pages only; all bytes go through `HttpClient` with `Range`. If it is unreachable or fails, the queue item waits in "needs you" with one notification; the Playwright headed browser opens only when the user clicks. The app never pops a browser unprompted. `cf_clearance` is not persisted; re-solving is cheaper than managing IP/UA-bound cookies.

## D6 — Integrity: archive CRC test is the truth

The site publishes no hashes. BuzzHeavier's page hashes match; MegaDB's displayed SHA-256 did **not** match a valid file. So resolvers pass hashes only when known-good, and `Installer` always runs `unrar t`/`7z t` before touching the old install.

## D7 — Updates: fully automatic, full re-download, no previous versions kept

Pre-installed RARs mean an update is a 10–70 GB re-download, not a patch. Flow: detect (`modified` AND build/version differs) → download to `.downloads` → CRC test → extract to `.staging` → back up saves → swap → prune. The old install is intact until the swap. Only the last `KeepBackups` save backups are kept. "New game posted" notifications default off.

## D8 — Data: SQLite + JSON settings + keychain shim

`Microsoft.Data.Sqlite`, hand-written schema with a migrations table (no EF Core). Settings as one flat JSON file. Secrets through `secret-tool`/DPAPI/0600-file; currently unused, kept because adding a token later should not need a redesign.

## D9 — Save backup heuristic is best-effort

Releases use different emulators (Goldberg/GSE, RUNE, online-fix). We back up `GSE Saves`/`Goldberg SteamEmu Saves/<app_id>` from the game's prefix and save-like directories inside the game folder (`Save*`, `Saves`, `SaveGames`, `Profiles`, `Saved`, `UserData`…), skipping engine folders (`Engine`, `Content`, `Binaries`, `drive_c`…). Marked `ponytail:` in code. Revisit with a per-game override if a game's saves are missed.

## D10 — Verification via logs, not screenshots (Nov 6 2026)

Early UI checks used `spectacle` screenshots of the active window; that captured whatever the owner had on screen and proved nothing about app state. Owner: "why tf are we relying on screenshots instead of having proper logging?" Resolution: `Microsoft.Extensions.Logging` throughout Core with an in-house `FileLoggerProvider` (daily files, 7-day prune, live tail in the Activity view, runtime level switch, `--verbose`), signed URLs redacted. Rule in `AGENTS.md`: verify by reading the log and the DB.

## D11 — The app owns and launches games; no Heroic/Lutris prefix scanning (Nov 6 2026)

The plan originally said "launching is Heroic/Lutris; the app installs and stops" and backed up Goldberg saves by scanning configured Heroic/Lutris prefix roots. Owner: "we also install/own and manage the games. I don't want to do any manual work if possible."

Choice: each game gets a Wine prefix at `<library>/<slug>/prefix`, created on first Play by `umu-run` + Proton (`GAMEID=umu-<steam_app_id>`, `STORE=none`, `PROTONPATH` optional). `ExeFinder` picks the executable; the user can override exe, Proton and arguments per game; running state and playtime are tracked; updates wait for a running game and preserve the prefix by moving it into the new tree. `PrefixRoots` was removed from settings and UI; `SaveLocator.FindEmulatorSaves` only looks inside the game's own prefix. Windows runs the exe directly.

Locks in: the prefix is a per-game artifact that lives and dies with the game folder; deleting a game deletes its prefix and saves (after backup). Revisit if the owner wants a shared prefix, Steam shortcuts, or a Heroic sideload export.

## D12 — No spikes or scratch files in the repo

Throwaway harnesses live under `/tmp/steamrip-spikes`, test profiles under `/tmp/sr-cfg`, test libraries under `/tmp/sr-lib`. Findings that matter are written into `docs/` and, where parseable, saved as fixtures under `tests/…/Fixtures/`.

## D13 — Native packages per platform, no tarballs, no bundled runtime (Nov 6 2026)

Owner: "I want proper packages: deb, rpm, AppImage, AUR for Linux, exe for Windows. I don't like tarball releases."

Choice: one self-contained single-file `dotnet publish` per OS (compressed, ~90 MB installed), wrapped as `.deb`/`.rpm`/Arch `.pkg.tar.zst` by nfpm from a single `packaging/nfpm.yaml`, as an AppImage by appimagetool, and as a per-user Inno Setup installer on Windows (`%LOCALAPPDATA%\Programs`, no UAC, closes and restarts the tray app on upgrade, removes the HKCU Run value on uninstall). The AUR package is `steamrip-desktop-bin` and repacks the released `.deb`, so there is exactly one Linux binary to debug. Builds are tag-driven and calendar-versioned, `YYYY.M.D` without zero padding (owner: "use a date version system so 2026.11.6 for today"); a further release the same day appends `.1`, `.2`; a `-rc1` suffix marks a pre-release, which nfpm renders as `~rc1` so distro package managers sort it below the final. Published by GitHub Actions with generated notes and checksums.

Rejected: Flatpak (the sandbox breaks shelling out to `unrar`, `umu-run`, `wine`, `secret-tool` and writing into an arbitrary library folder); Snap (same, plus confinement of `~/Games`); MSIX/Store (signing and identity requirements for a single-user app); framework-dependent builds (would make "install .NET 10" the first manual step); code signing (cost; SmartScreen warning accepted and documented).

Locks in: the binary is `/usr/bin/steamrip-desktop` with the icon name `steamrip-desktop` in hicolor, which the app's own autostart `.desktop` relies on; `Autostart.ExecutablePath` prefers `$APPIMAGE`. Revisit if the app grows a self-updater (Velopack would replace the installers) or if a maintainer wants Flatpak badly enough to add portals.

## D14 — Launch tweaks are env vars and wrappers around umu, defaults with per-game overrides (Nov 7 2026)

Owner: "I need more default settings for things like mangohud, gamescope, hdr, other proton variables … default settings in the settings page but also settings per game", and "can't we do any checks about what hardware and software we're running?"

Choice: one `LaunchOptions` record (MangoHud, gamemode, gamescope + args, HDR, Wine Wayland, FSR, fsync, Proton log, DLL overrides, free-form environment, wrapper). `Settings.DefaultLaunch` holds the defaults; `installs.launch_json` holds only the fields a game overrides, resolved as game → defaults → `AppDefaults`. `GameLauncher.Compose` is a pure function from options to command line + environment, so every combination is unit-tested. HDR follows the ArchWiki HDR page exactly: gamescope `--hdr-enabled` or Wine's Wayland driver, both with `DXVK_HDR=1`, never `ENABLE_HDR_WSI` under gamescope. Missing tools are skipped with a log warning, not an error. `HostCapabilities` probes session, per-display HDR state (compositor CLI, EDID fallback), GPU driver, the tools and each Proton build's known env vars; it only produces indicator lines and an advisory sentence next to the HDR box, never a block.

Rejected: esync and NTSync toggles (esync is gone in Proton 10 and NTSync is chosen by wine from `/dev/ntsync`; both are indicator lines); gating env vars on what a build "knows" (unknown vars are inert, so always setting them is simpler and works for the umu default build we cannot inspect); Avalonia `Screens` for the gamescope size (Core stays Avalonia-free; `/sys/class/drm/*/modes` gives the same answer); a separate per-option settings column (JSON blob keeps migrations at one line).

Locks in: `LaunchOptions` property names are the JSON keys in both `settings.json` and `launch_json`; renaming one silently resets that option for existing users, so add, do not rename.

## D15 — GE-Proton is the default, DW-Proton is offered, UMU-Proton never (Nov 7 2026)

Owner: "never enable umu proton by default pls proton 10 is way too old. use ge-proton as default", after a day of HDR debugging in Cyberpunk.

Context: with `PROTONPATH` unset, umu 1.4.3 picks `UMU-Latest` = UMU-Proton-10.0-4, a Proton 10 build without the Wayland driver and without HDR auto-detection. The app used to force `DXVK_HDR=1` + `PROTON_ENABLE_HDR=1`, which on that build (and on Proton-CachyOS) produced PQ output onto what Wine considered an SDR display: grey blacks and dull colours.

Choice: `Settings.DefaultProtonPath` defaults to the keyword `GE-Proton`, which umu downloads and keeps current itself (`PROTONPATH=GE-Proton`); `""` is read as the same. `DW-Proton` (Dawn Winery's Proton-CachyOS + GE patch set) is a second keyword, downloaded by `ProtonDownloader` from the GitHub mirror with SHA-512 verification because umu has no keyword for it. Both appear in the Settings list and the per-game list next to the locally found builds. HDR composition no longer sets any HDR env var; it only chooses the path (gamescope `--hdr-enabled` or Wine Wayland) and leaves detection to Proton, per the GE-Proton 11 documentation.

Rejected: shipping our own umu `PROTONPATH` default of `UMU-Proton` (too old); checking GitHub on every launch (one check per 6 h, offline uses what is installed); a generic "Proton from any GitHub repo" feature (one hard-coded mirror, one archive layout, `// ponytail`).

Locks in: `GE-Proton` and `DW-Proton` are stored verbatim in `settings.json` and `installs.proton`; they are identifiers, do not rename them.
