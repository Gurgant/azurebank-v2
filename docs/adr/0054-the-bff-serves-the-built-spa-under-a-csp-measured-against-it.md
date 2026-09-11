# ADR-0054: The BFF serves the built SPA under a CSP measured against it

**Status:** Accepted · **Date:** 2026-09-11 · Makes true what the BFF's `Program.cs` already said
("prod = the BFF serves the SPA itself") and what `SecurityHeadersMiddleware` said it would need.
Changes [ADR-0031](0031-e2e-playwright.md) D1 for CI; the local default is untouched.

## Context

The Content-Security-Policy was written for a bundle that nothing served under it.

- **The BFF served no pages.** Measured on main `7ceb16f`: `GET /`, `/index.html` and `/settings`
  all answered 404. Every page a browser had ever loaded came from vite.
- **Vite's dev server sends no CSP.** The e2e suite (ADR-0031) drives vite, so `script-src 'self'`
  had never met the real build. The frontend's own comments already warned that an inline script
  "would work under Vite and be refused in production" — and nothing could have shown it.
- **The policy sat on API responses only**, with a comment saying a frontend "would need a more
  permissive policy". Nobody had checked whether it would.
- **`X-XSS-Protection: 1; mode=block`.** The OWASP HTTP Headers cheat sheet recommends `0`, and
  warns that the filter the old value turns on "can create XSS vulnerabilities in otherwise safe
  websites".

## Decision

**D1. The BFF serves the build when `Spa:RootPath` is set.** Static files from that directory, and
the page shell for any navigation no endpoint claimed. Unset, it serves no pages, exactly as before:
the development loop keeps vite. Set but holding no `index.html`, the host refuses to start
(`SpaOptionsValidator`), because a BFF told to serve the app would otherwise report itself healthy
and answer every page with 404.

**D2. The shell fallback is a middleware, and it never answers for the server's own paths.** It acts
only when routing selected no endpoint, only for GET and HEAD, never under `/api`, `/bff` or
`/health`, and never for a file name. Measured with `MapFallbackToFile` in its place:
`GET /bff/auth/login` — a POST-only route, 405 on main — answered 200 with the page, and so did
`GET /bff/nope` and `GET /health/nope`. With the middleware they answer 405, 404 and 404.

**D3. One policy for every response, with no `'unsafe-inline'` and no `'unsafe-eval'`:**

    default-src 'self'; script-src 'self';
    style-src 'self' 'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=';
    img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none';
    base-uri 'none'; form-action 'self'; frame-ancestors 'none';

The hash is SHA-256 of the **empty string**. Griffel, Fluent's styling engine, creates empty
`<style>` elements and fills them through CSSOM `insertRule`, which CSP does not inspect; it is the
empty element that `style-src 'self'` refuses. So the hash admits exactly that and nothing more: a
`<style>` with any content still does not match, which is what `'unsafe-inline'` would have allowed.
`object-src`, `base-uri` and `form-action` are new; the rest is main's policy.

Measured in headless Chromium against the build served by the BFF, signing in and walking the
dashboard, accounts, history, transfer and settings, opening a dialog and reloading in dark mode:

    main's policy (style-src 'self' 'unsafe-inline')    6 violations, all script-src eval
    style-src 'self' only                               105: 99 style-src-elem + 6 eval;
                                                        a button's border-radius 0px, not 10px
    style-src 'self' + the empty-string hash            6, all script-src eval; 10px
    this policy, with D4                                0

**D4. Zod runs `jitless`.** The six `eval` violations were Zod probing whether it may compile
parsers with `Function(...)`: under this policy the probe throws, Zod falls back, and the browser
files a violation on every page load anyway. `src/zodConfig.ts` sets `jitless` before any schema
runs, so there is no probe to explain away. The test setup imports it too, so the unit suite parses
the way the browser does.

**D5. Caching by name.** Everything under `/assets` is fingerprinted by vite, so it is
`public, max-age=31536000, immutable`; the shell and every file that keeps its name across builds
(`theme-init.js`, the icons) are `no-cache`, because the shell names this build's bundle and must
not outlive a deploy.

**D6. `X-XSS-Protection: 0`**, as OWASP recommends. The protection is the policy in D3.

**D7. CI runs the whole e2e suite against the served build.** The real-stack job builds the SPA,
starts the BFF with `Spa:RootPath` pointing at it and sets `E2E_BASE_URL`, so Playwright starts no
vite. `e2e/csp.spec.ts` repeats D3's walk and fails on any violation, and on a page that arrives
without the policy. The frontend job also refuses a built `index.html` with an inline `<script>` or
`on*=` handler, in seconds, before anything starts. Locally the suite still runs against vite unless
`E2E_BASE_URL` is set, and the CSP spec then skips with the reason.

## Rejected

- **Keeping `'unsafe-inline'` in `style-src`.** It was the expected answer, and it is not needed:
  the empty-string hash does the one thing the build requires and admits strictly less.
- **Nonces.** A fresh nonce per response means templating `index.html` on every request and handing
  the value to Griffel's renderer. It buys nothing here that a fixed hash does not: the only inline
  thing the build produces is an empty element, and an empty element has one hash.
- **An inline theme script with a hash.** Already rejected by
  [ADR-0027](0027-dark-mode-through-css-custom-properties.md): the pre-paint script is a file, and
  stays `'self'`.
- **`MapFallbackToFile`.** D2's measurement.
- **A separate policy for pages.** API responses are never rendered, so the page policy costs them
  nothing, and one policy is one set of values for the tests to pin.

## Consequences

- **The policy now has a guard that exercises it.** A Griffel release that writes text into its
  `<style>` elements, or a dependency that reaches for `eval`, fails the served e2e run instead of
  production. Both failure modes were put back to prove it: without D4 the spec fails on six `eval`
  violations, and without the hash on 97 `style-src-elem` violations.
- **What it does not cover.** The walk and the suite visit what they visit; a page neither reaches
  is unchecked. Only Chromium runs. `img-src 'self' data:` was kept as it was and not re-measured.
  Nothing reports violations from real browsers — there is no `report-to` — and HSTS is not set
  by the BFF, which leaves it to whatever terminates TLS in front of it.
- **`dist/` ships `mockServiceWorker.js`** from `public/`, and the BFF now serves it. It is inert:
  the app registers it only in vite's `mock` mode (`main.tsx`). Noticed here, not changed.
- **The development loop is unchanged**: no `Spa:RootPath`, no pages, vite on 5173.

## What would change this

- **The served e2e run going red on `style-src-elem`.** Griffel would then be writing content into
  its `<style>` elements, and the empty-string hash stops being enough: nonces, or a renderer option
  that keeps the elements empty, reopen.
- **The SPA moving to another origin** (a CDN, static hosting). The policy and D5 move with the
  pages, D1 and D2 go, and `connect-src` has to name the BFF.
- **Wanting to know what real browsers refuse.** That is a `report-to` endpoint, which this does
  not add.
