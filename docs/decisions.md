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
