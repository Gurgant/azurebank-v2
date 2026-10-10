# ADR-0054: The BFF serves the built SPA under a CSP measured against it

**Status:** Accepted · **Date:** 2026-09-11 · **Amended:** 2026-09-25 (HSTS), 2026-10-05 (D2, with
ADR-0063). Changes ADR-0031, decision 1, for CI; the local default is untouched.

## Context

A Content-Security-Policy is worth what the real bundle does under it. Without this decision the BFF
serves no pages: every page comes from Vite's dev server, which sends no CSP, and the e2e suite
(ADR-0031) drives that server. So `script-src 'self'` never meets the build, and an inline script
works under Vite and is refused in production. `X-XSS-Protection: 1; mode=block` turns on a filter
that "can create XSS vulnerabilities in otherwise safe websites" (OWASP HTTP Headers cheat sheet).

## Decision

- **D1. The BFF serves the build when `Spa:RootPath` is set**: static files from that directory, and
  the page shell for any navigation no endpoint claimed. Unset, it serves no pages and development
  keeps Vite. Set but holding no `index.html`, the host refuses to start (`SpaOptionsValidator`),
  because it would otherwise report healthy and answer every page 404.
- **D2. The shell fallback is a middleware, and it never answers for the server's own paths.** It
  acts only when routing selected no endpoint, only for GET and HEAD, never for a file name, and
  never under `/api`, `/bff` or `/health`: a path whose first non-empty segment is one of those, in
  any letter case, behind any run of leading slashes and backslashes (an encoded slash is not a
  separator: `/%2Fapi/accounts` gets the page). `MapFallbackToFile` in its place answers
  `GET /bff/auth/login` (a POST-only route), `/bff/nope` and `/health/nope` with 200 and the page;
  the middleware answers 405, 404 and 404.
- **D3. One policy for every response, with no `'unsafe-inline'` and no `'unsafe-eval'`:**
  ```
  default-src 'self'; script-src 'self';
  style-src 'self' 'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=';
  img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none';
  base-uri 'none'; form-action 'self'; frame-ancestors 'none';
  ```
  The hash is SHA-256 of the **empty string**. Griffel, Fluent's styling engine, creates empty
  `<style>` elements and fills them through CSSOM `insertRule`, which CSP does not inspect; the
  empty element is what `style-src 'self'` alone refuses, leaving a button's border radius at 0px
  instead of 10px. A `<style>` with content does not match, which `'unsafe-inline'` would allow.
- **D4. Zod runs `jitless`**, because Zod probes whether it may compile parsers with
  `Function(...)`, and under this policy the browser files an `eval` violation for the probe on
  every page load. `src/zodConfig.ts` sets it before any schema runs; the test setup imports it.
- **D5. Caching by name.** Everything under `/assets` is fingerprinted by Vite, so it is
  `public, max-age=31536000, immutable`. The shell and the files that keep their name across builds
  (`theme-init.js`, the icons) are `no-cache`, because the shell must not outlive a deploy.
- **D6. `X-XSS-Protection: 0`**, as OWASP recommends. The protection is the policy in D3.
- **D7. CI runs the whole e2e suite against the served build.** The real-stack job builds the SPA,
  starts the BFF with `Spa:RootPath` and sets `E2E_BASE_URL`, so Playwright starts no Vite.
  `e2e/csp.spec.ts` walks the app and fails on any violation, and on a page without the policy. The
  frontend job refuses a built `index.html` with an inline `<script>` or `on*=` handler. Locally the
  suite runs against Vite unless `E2E_BASE_URL` is set, and the CSP spec skips.

## Rejected

- Rejected: `'unsafe-inline'` in `style-src`, because the empty-string hash admits strictly less.
- Rejected: nonces, because a nonce per response means templating `index.html` on every request and
  handing the value to Griffel, while the only inline thing the build produces has one fixed hash.
- Rejected: an inline theme script with a hash, because the pre-paint script is a file (ADR-0027).
- Rejected: `MapFallbackToFile`, because it answers the server's own paths with the page (D2).
- Rejected: a separate policy for pages, because API responses are never rendered, so the page
  policy costs them nothing, and one policy is one set of values for the tests to pin.

## Consequences

- The policy has a guard that exercises it: a Griffel release that writes text into its `<style>`
  elements, or a dependency that reaches for `eval`, fails the served e2e run, not production.
- The BFF sends `Strict-Transport-Security: max-age=31536000` in every environment but Development,
  from the same middleware and not through `UseHsts`, which skips any request that is not https:
  behind an edge that terminates TLS every request reaches the BFF over http.
- `dist/` ships `mockServiceWorker.js` from `public/` and the BFF serves it. It is inert: the app
  registers it only in Vite's `mock` mode (`main.tsx`).
- Not covered: a page neither the walk nor the suite reaches, and any browser but Chromium.
- Not covered: `img-src 'self' data:` is kept as it was and not re-measured.
- Not covered: nothing reports violations from real browsers; there is no `report-to`.

## Revisit when

- The served e2e run goes red on `style-src-elem`: Griffel then writes content into its `<style>`
  elements, and nonces, or a renderer option that keeps the elements empty, reopen.
- The SPA moves to another origin (a CDN, static hosting): the policy and D5 move with the pages, D1
  and D2 go, and `connect-src` has to name the BFF.

## Verified by

- `SecurityHeadersTests`: the whole header set, value by value. `SpaHostingTests`: D1, D2 and D5.
- `frontend/e2e/csp.spec.ts` in CI's real-stack job; the frontend job's inline-script check.

## Related

ADR-0027, ADR-0031, ADR-0032, ADR-0063.
