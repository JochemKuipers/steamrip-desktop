# Research notes: steamrip.com and its hosters

Everything here was verified live on Nov 6 2026 from a residential IP (CachyOS box) and through the owner's FlareSolverr instance, then confirmed again by the production code during the build. Dates matter: hosters change; re-verify before trusting a detail older than a few months.

## 1. The site

- **Platform**: WordPress behind Cloudflare (`server: cloudflare`, PHP 8.3, Plesk). About 5,000 posts (`X-WP-Total: 5033` on Nov 6 2026); each post is one game.
- **Anti-bot on the site itself: none from a residential IP.** Every page and REST call returned 200 with a plain Chrome user agent. FlareSolverr reports "Challenge not detected" for steamrip.com. From a datacenter IP the same URLs timed out (504), so IP reputation matters. Published scrapers carry Cloudflare-solving code, so challenges evidently appear for some clients; the app treats a challenge page as a parse failure and retries later.
- **The hard Cloudflare wall is on the hosters**, not the catalog. That inverted the original brief and shaped the design: the solver is a *download* concern.
- **Rate limiting**: a burst of REST pages gets HTTP 429 after roughly 40 consecutive `per_page=100` requests. `Retry-After` is sometimes present. The site sends `cache-control: max-age=3`, i.e. it expects edge caching, not origin hammering.
- **RSS is broken** (`/feed/` → HTTP 500). Do not plan around it.

### Catalog sources, cheapest first

1. **WP REST API** — the one we use.
   `GET /wp-json/wp/v2/posts?per_page=100&page=N&orderby=modified&order=desc&_fields=id,slug,link,title,modified_gmt,date_gmt,acf,content`
   - `title.rendered` — `"Game Name Free Download (v1.2.3)"`; version in the trailing parentheses, free-form (`(v1.3.1)`, `(Build 12903118)`, `(v1.4.0.27939 + Co-op)`, `(v98314+1943)`).
   - `acf` (Advanced Custom Fields) — `steam_app_id`, `tagmetafield` (e.g. `"Build 26315250"`, the Steam build id), `updated`, `release_date`. Structured and far more reliable than the title.
   - `content.rendered` — the download buttons as `<a class="shortc-button" href="//bzzhr.to/…">`, a GAME INFO block (`Game Size: 12.3 GB`, `Version : …`), system requirements. We only parse the `shortc-button` anchors and the Game Size line.
   - `modified_gmt` bumps on any edit including cosmetic ones; pair it with a build/version comparison before calling something an update.
   - **Posts are updated in place**: same id, same URL, new title/`modified`/`acf.tagmetafield`. Match library entries on post id, never on title.
2. **Post sitemap** `/wp-sitemap.xml` → `wp-sitemap-posts-post-{1..3}.xml` with `<lastmod>` per game. ~125 KB for whole-catalog change detection. Not used yet; a cheap fallback if REST is ever disabled.
3. **`/updated-games/`** (25 most recent, human page) and the `updated` tag (id 2892).
4. **`/games-list-page/`** — 707 KB A–Z HTML (`li.az-list-item > a`). What the existing scrapers use; theme-specific and fragile. Fallback only.

The game page also renders a "Posted Build vs Steam Latest" badge client-side (`svchk` script) from `steam_app_id`; we could compute the same from Steam PICS later, as information only.

### Hoster distribution (100 most recently modified posts, Nov 6 2026)

| Hoster | Posts | Access |
| --- | --- | --- |
| `bzzhr.to` (BuzzHeavier) | 81 | Cloudflare managed challenge + referer rule |
| `*.fileditchfiles.st/.me` (FileDitch) | 71 | Own WASM browser check ("Verifying your browser") |
| `gofile.io` | 25 | No challenge; token-based API |
| `megadb.net` | 23 | No challenge; `Referer` enforced; server wait |
| `1fichier.com` | 4 | Free-tier wait timers; the only one Real-Debrid supports |
| `filecrypt.cc` | 3 | CAPTCHA container around other links |

1–4 mirrors per post, median 2. Every sampled post had at least one of FileDitch/GoFile/MegaDB, so BuzzHeavier support was optional; it ended up working anyway.

### Integrity

The site publishes no hashes. Archives are `.rar` (RAR5, sometimes multi-volume). BuzzHeavier publishes MD5/SHA-256 on its page and they match; MegaDB shows a SHA-256 that does **not** match the file (file was a valid RAR). The reliable check is the archive CRC test (`unrar t` / `7z t`), which the installer always runs.

## 2. Cloudflare and FlareSolverr

- FlareSolverr (smeinecke fork 3.9.x, Chrome 154) at `http://192.168.2.251:8191`. `POST /v1` with `cmd: request.get` returns the solved HTML, cookies and the user agent used. `sessions.create` keeps a browser alive; the fork supports an `actions` array (`eval`, `wait_for`, …) which BuzzHeavier needs.
- **Cookie handoff works**: `cf_clearance` + UA returned by FlareSolverr are accepted by `curl` and by .NET `HttpClient` from the same public IP. Cookies are bound to IP and UA; the FlareSolverr box shares the LAN egress, which is why this works.
- FlareSolverr cannot set a `Referer` on `request.get`; hosters that need one (MegaDB) are plain `HttpClient`; BuzzHeavier gets a genuine referer by hopping via `location.href` inside the solver browser.
- It is a landing-page solver only. Bodies come back as JSON; it never streams a file. Every byte of a game goes through `HttpClient` with `Range`.
- Reliability notes gathered: Turnstile loops, 60 s timeouts, dependence on egress IP reputation, need for WebGL fingerprinting. Treat it as an accelerator: health-check (`GET /`) before use, time out at ~90 s, fall through to the next mirror or to the manual browser.
- Manual fallback: headed Chromium via Playwright (Playwright's managed download; only Firefox is on the dev box and Turnstile dislikes patched Firefox). It polls the title for "Just a moment"/"Attention Required"/"Verifying" and hands the page back. Opens only on user click.

## 3. Hoster findings (details in `hosters.md`)

- **FileDitch**: landing page behind its own WASM check; FlareSolverr passes it in ~7 s. The main button (`a.btn-main`) is a signed CDN URL on a rotating domain (e.g. `lumisleepsby10pm.xyz/<base64>`; token decodes to `expiry:signature:path`). `206 Partial Content`, `Accept-Ranges: bytes`, works with **no cookies**. Expiry was ~12 h in the token during recon but a 403 appeared within ~5 minutes in practice; the downloader re-resolves on 403.
- **GoFile**: `POST /accounts` guest token; `X-Website-Token = sha256("<UA>::en-US::<token>::<unix/14400>::12af056dacea0b")` (salt is a site constant, changes occasionally); `GET /contents/{id}`; file GET needs `Cookie: accountToken=<token>`. Supports `Range`.
- **MegaDB**: `Referer: https://steamrip.com/` on the landing page or you get "Referrer not allowed"; page has `var finalDownloadUrl` and `var seconds` (server-enforced wait); after the wait a 302 to `fs*.megadb.xyz`; the file GET needs `Referer: https://megadb.net/` plus the cookie jar.
- **BuzzHeavier**: plain GET with a Referer → 403 `cf-mitigated: challenge`; FlareSolverr `request.get` on the share link bounced the browser back to steamrip.com with no cookies (referer rule). Working flow: FlareSolverr **session**, load steamrip.com, `eval location.href = <share>`, `wait_for //a[contains(@class,'download-btn')]`, then an in-page `fetch(hx-get, {HX-Request, HX-Current-URL, redirect:'manual'})` whose `hx-redirect` header is the file URL. MD5 and SHA-256 are on the page and match.
- **1fichier / FileCrypt**: not implemented (4 % and 3 % of posts, every such post also had another mirror).

## 4. Debrid

Parked. Real-Debrid covers 1fichier only (~4 % here) and none of bzzhr/fileditch/gofile/megadb. TorBox lists BuzzHeavier. Cost ~3–4 EUR/month, adds an account and token. Revisit only if the site shifts hosters.

## 5. Launching (added after the scope change)

- `umu-run` 1.4.3 on the box; it fetches a Steam Linux Runtime (`steamrt4`) and, when `PROTONPATH` is unset, the latest GE-Proton into `~/.local/share/umu/`. First launch of a new prefix takes 30–60 s (runtime check, prefix creation, protonfixes downloading DLSS/XeSS/FSR DLLs); subsequent launches are instant.
- Proton builds found on the box: `/usr/share/steam/compatibilitytools.d/proton-cachyos-{slr,native}`, `~/.steam/root/compatibilitytools.d/Proton-CachyOS Latest` (also reachable via `~/.steam/steam` symlink — dedupe by name+version, not path), `~/.local/share/Steam/steamapps/common/Proton - Experimental`. Display names come from `compatibilitytool.vdf`.
- A Lutris-made umu prefix (LEGO Batman) confirmed the layout we now create ourselves: `prefix/{drive_c,dosdevices,system.reg,user.reg,pfx -> .}`, saves under `drive_c/users/steamuser/…`.
- Goldberg/GSE emu saves land in `drive_c/users/steamuser/AppData/Roaming/{GSE Saves,Goldberg SteamEmu Saves}/<steam_app_id>/`; some games write saves inside the game folder (`Save*`, `Saves`, `SaveGames`, `Profiles`, `Saved`, `UserData`…). "Released By" varies (Goldberg, RUNE, online-fix), so save backup is best-effort.
- Verified launch: 5D Chess created `prefix/`, ran under Proton-CachyOS, wrote `AppData/Roaming/Thunkspace` (its save dir) inside the prefix; `Stop` (kill tree + `wineboot -k`) left zero wine processes.

## 6. Prior art read

- `KeksPirates/SoftwareManager` (`src/data/sources/steamrip.py`) — HTML A–Z list scraping, hoster list.
- `fat-addons/steamrip-addon` (GitLab) and the `shockstruck/steamrip-addon` fork — TypeScript; Cloudflare handling with `puppeteer-real-browser` (60 s stability check), per-hoster modules (`Bzzhr.ts`, `MegaDB.ts`, `Gofile.ts`), and in the fork an executable-detection heuristic that informed `ExeFinder`.
- umu-launcher docs and Lutris's umu integration (`lutris.json` in the prefix) for the env-var contract: `WINEPREFIX`, `GAMEID`, `STORE`, `PROTONPATH`.

## 7. Dev-box facts that influenced choices

KDE on Wayland (native StatusNotifierItem → Avalonia tray works out of the box; XWayland by default), .NET 10.0.114, `unrar`/`7z`/`secret-tool`/`gdbus` present, no Chromium, FlareSolverr already running on the LAN (sunk cost), desktop machine (battery not a concern).
