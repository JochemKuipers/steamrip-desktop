# Hoster resolution protocols

Each hoster has one `IHostResolver` in `src/SteamRip.Core/Hosters/` that turns a share URL from a post into a `ResolvedDownload(Host, Url, Headers, FileName, Size, Md5, Sha256, ExpiresUtc)`. `ResolverChain` tries a post's mirrors in `Settings.HosterOrder` (default `FileDitch, GoFile, MegaDb, BuzzHeavier`) and returns the first success plus the list of errors. `Downloader` then streams the file with `Range` resume.

All four flows below were verified end to end (resolve → ranged download → `unrar t`) on Nov 6 2026 through the production code. HTML parsing is isolated in `HostPages.cs` and tested against fixtures in `tests/SteamRip.Core.Tests/Fixtures/`.

Common downloader behaviour: user agent is a Chrome/154 Linux string (`Downloader.DefaultUserAgent`), `.part` file with `Range: bytes=<have>-`, a 416 means the part is already complete, a 302/401/403 or a `text/html` body means the direct URL died → `DownloadException(retryResolve: true)` and `UpdateService` re-resolves (once per attempt, up to the retry budget). SHA-256/MD5 are verified when the resolver supplied them; mismatch deletes the file.

## FileDitch (`*.fileditchfiles.st`, `*.fileditchfiles.me`)

Share of posts: ~71 %. Needs FlareSolverr.

1. `FlareSolverrClient.GetAsync(shareUrl, disableMedia: true)` — FileDitch runs its own WASM browser check ("Verifying your browser…"); FlareSolverr passes it in ~5–8 s and returns the real landing page.
2. `HostPages.ParseFileDitch(html)`:
   - if the HTML still contains `Verifying your browser` → `FormatException("browser-verification interstitial")` (solver did not clear it; chain moves on).
   - first `<a … class="… btn-main …" href="…">` is the file URL. Ignore the `abuse.fileditch.com` link.
   - the URL is `https://<rotating-domain>/<base64>`; the base64 token decodes to `<unix-expiry>:<signature>:<path>`. Expiry is parsed when the layout matches, otherwise left null.
   - `<title>` gives the file name.
3. Download: plain GET, **no cookies, no referer** needed. `206 Partial Content`, `Accept-Ranges: bytes`, `Content-Type: application/vnd.rar`.

Gotchas: the token's embedded expiry said ~12 h, but in practice a 403 arrived within ~5 minutes of resolving; always treat a 403 as "re-resolve", never as "file gone". Signed URLs are redacted in logs (`FileDitchResolver.Redact`). No hashes published.

## GoFile (`gofile.io/d/<id>`)

Share of posts: ~25 %. No Cloudflare; pure API. No solver needed.

1. `POST https://api.gofile.io/accounts` → `data.token` (guest account).
2. Website token: `X-Website-Token = sha256hex("<UserAgent>::en-US::<token>::<floor(unix/14400)>::12af056dacea0b")`. The salt `12af056dacea0b` is a site constant lifted from gofile's JS; when the API starts answering `error-wt`, re-derive it (`GoFileResolver.Salt`, marked `ponytail:`).
3. `GET https://api.gofile.io/contents/<id>?cache=true&sortField=createTime&sortDirection=1` with `Authorization: Bearer <token>` and the website token → `data.children[*]` with `link`, `name`, `size`, `md5`.
4. Download `link` (`https://storeN.gofile.io/download/web/<id>/<name>`) with `Cookie: accountToken=<token>`. Supports `Range`. MD5 verified.

Gotchas: guest accounts get lower throughput than premium. When a folder holds several files the resolver picks the **largest**; multi-volume RAR sets are not yet downloaded as a set (`ponytail:` ceiling). A folder with no `children` usually means password-protected or removed. One transient "SSL connection could not be established" was seen; retry fixed it.

## MegaDB (`megadb.net/<id>`)

Share of posts: ~23 %. No Cloudflare; referer-gated with a server-side wait. No solver needed; FlareSolverr cannot even do this one because it cannot set a `Referer`.

1. GET the share URL with `Referer: https://steamrip.com/` and a cookie jar (`AllowAutoRedirect = false`). Without the referer the site answers "Referrer not allowed".
2. `HostPages.ParseMegaDb(html)` → `var finalDownloadUrl = "…"` (token URL), `var seconds = N` (wait), a displayed SHA-256 and the file name.
3. Wait `seconds + 2` (the server enforces it; asking earlier gives an error page).
4. GET the token URL with the same jar → `302 Location: https://fs<N>.megadb.xyz/…`.
5. Download that URL with `Referer: https://megadb.net/` **and the cookies**; without either it 403s. Supports `Range`.

Gotchas: **the SHA-256 on the page does not match the file** (observed `9ee9…` vs `9db6…`; the file was a valid RAR). The resolver deliberately does not pass the hash; `unrar t` is the integrity check.

## BuzzHeavier (`bzzhr.to/<id>`, also `buzzheavier.com`)

Share of posts: ~81 %. Cloudflare managed challenge **plus** a referer rule: loading the share link directly (even through FlareSolverr `request.get`) bounces the browser back to steamrip.com with no cookies. Needs a FlareSolverr **session** with the fork's `actions` support.

1. `FlareSolverrClient.CreateSessionAsync()`; `GetAsync("https://steamrip.com/", session)` so the browser has a genuine origin.
2. `Session.ActionAsync([...])`:
   - `eval`: `window.location.href = "<shareUrl>"` (navigation with a real referer),
   - `wait_for`: XPath `//a[contains(@class,'download-btn')]`, 30 s,
   - `eval`: return `{url: location.href, html: document.documentElement.outerHTML}`.
3. `HostPages.ParseBzzhr(html)` → the `hx-get` path on the download button, MD5, SHA-256, file name.
4. `eval` in-page: `fetch(hxPath, {headers: {'HX-Request': 'true', 'HX-Current-URL': location.href}, redirect: 'manual'})` and stash `response.headers.get('hx-redirect')` on `window.__sr`; a second `eval` reads it back. That header is the direct file URL.
5. Download the URL with plain `HttpClient` (no cookies needed in testing). MD5 and SHA-256 both verified and both matched.

Gotchas: slowest resolver (session + two page loads, ~15–25 s), so it is last in the default order even though it is the most common mirror. Session is disposed after each resolve. If the fork is replaced by upstream FlareSolverr without `actions`, this resolver stops working.

## Not implemented

- **1fichier** (~4 %): free-tier wait timers and occasional CAPTCHA; the only host Real-Debrid supports. Every post seen also had another mirror.
- **FileCrypt** (~3 %): CAPTCHA/ad container wrapping other hosts' links; interactive by design. `CatalogParser` drops those URLs; every such post also had another mirror.
- `Hoster.Fichier` and `Hoster.FileCrypt` exist in the enum so `HosterDetector` can label them; `ResolverChain.Supported` excludes them.

## Manual fallback

`ManualSolve.ViaBrowserAsync(post)` opens Playwright's headed Chromium on the FileDitch mirror, waits until the title is no longer a challenge, and feeds the HTML to `HostPages.ParseFileDitch`. It exists for the day the FlareSolverr box is down or Cloudflare escalates to an interactive CAPTCHA. It only runs when the user clicks "Solve in browser" on a queue item in the "needs you" state.

## Adding or fixing a hoster

1. Save the real landing HTML (after any solve) into `tests/SteamRip.Core.Tests/Fixtures/<host>_*.html` and write the parser in `HostPages` against it.
2. Implement `IHostResolver` with constructor-injected `ILogger<T>?`; throw `HostResolveException(Host, message)` for anything the chain should skip past.
3. Register it in `AppServices.Build` and add the enum value to `Settings.HosterOrder` defaults.
4. Prove it with a throwaway harness under `/tmp` that runs `ResolverChain` + `Downloader` on a real post and check the log; then run `unrar t` on the result.
5. Update this file.
