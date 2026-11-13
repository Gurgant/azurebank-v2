# ADR-0065: Install the frontend's packages with pnpm

**Status:** Accepted · **Date:** 2026-11-13

## Context

`npm ci` took 140.5 s in a fresh folder on 26 October 2026 (Node 24, the lock of that day), and it
ran the install script of every package that has one. I want an install that is quick in a new
working folder and that runs no dependency code at install time. pnpm shares the package files on
the machine and can refuse an install script, a version under a week old, a weaker release than an
earlier one, and a dependency that is not from the registry. The move must change no package.

## Decision

1. **`frontend/` installs with pnpm 12.15.0, named by `packageManager` in `frontend/package.json`**,
   so that Corepack (Node 24.12 or later), CI and the Dockerfile all take the same version.
2. **No dependency runs an install script.** `allowBuilds` lists esbuild, msw and fsevents as
   `false` and `strictDepBuilds` fails any other one, because esbuild's script downloads a binary
   only when its platform package is missing, and otherwise checks the binary and swaps the
   JavaScript stub for it, which the platform package already allows; fsevents is for macOS alone and
   msw's only copies a file that CI now checks.
3. **A version under 7 days old is not installed** (`minimumReleaseAge`), because most malicious
   releases are pulled within hours and nothing here needs a package on its first day.
4. **A version with weaker provenance than an earlier one is refused** (`trustPolicy`), with one
   exception, `semver@6.3.1`: a backport published by hand in 2023, which reaches us only as a
   development dependency, through `@babel/core` under `eslint-plugin-react-hooks` and under
   `pastable`, which `typed-openapi` brings in. Any other refusal is a reason to stop and look.
5. **A dependency of a dependency comes from the registry only** (`blockExoticSubdeps`), because a
   git URL or a tarball is not covered by the checks above.
6. **The tinypool override moves from `package.json` to `pnpm-workspace.yaml`**, because oxfmt
   pins tinypool 2.1.0, a version with an advisory, and pnpm reads overrides from there.
7. **The lock holds the same packages as the npm lock it replaces**: 573 names and versions, the
   nested semver 7.7.3, tinypool 2.1.2 under oxfmt and one keyborg 2.6.0. I compared the two locks
   by name and version before I deleted the old one.
8. **CI checks that `public/mockServiceWorker.js` is the worker of the installed msw**, because
   msw's install script no longer refreshes it.
9. **`docs-site/`, its lock, its workflow and the Bruno CLI stay on npm**: they are separate
   trees, and moving them is a change of its own.
10. **`pmOnFail: ignore` in `pnpm-workspace.yaml`, and `packageManager` stays.** With
    `packageManager` set to pnpm 12, pnpm writes `pnpm-lock.yaml` as two YAML documents, a first
    one that lists pnpm itself and then the packages. GitHub's documentation says that its
    dependency graph does not detect the dependencies of a lock written that way, so Dependabot
    would stop seeing the frontend and `SECURITY.md` would be wrong when it says that Dependabot
    alerts reach the frontend's npm packages. With `pmOnFail: ignore` pnpm does not enforce the
    pinned version, so it records nothing about it, and the lock is one document. The field in
    `package.json` still picks 12.15.0 for Corepack, for the Dockerfile and for `pnpm/action-setup`.
    What I give up: a pnpm that is not 12.15.0, run by hand, is no longer stopped or switched by
    pnpm itself, and the lock no longer records which pnpm wrote it. GitHub reads the dependency
    graph from the default branch, so I can see the frontend there only after the merge.

## Rejected

- Rejected: lowering a setting or excluding another version to pass, because a refusal is the check
  doing its work.
- Rejected: pins in `overrides` for react-redux, reselect or ts-api-utils, because the lock holds
  the versions already and removing such a pin later moved versions up.

## Consequences

- A new folder installed in 35 s with the packages in the store, and in 56 s the first time (the
  store held earlier packages), on 13 November 2026. Both are wall times. A second install with
  nothing changed printed "Done in 90ms", which is pnpm's own figure; the wall time I measured
  was 5 s. Node 26, the machine under load.
- Going back is `git revert`, which brings `package-lock.json` back; every folder then needs its
  `node_modules` deleted first, because pnpm's layout is not npm's.
- The icon rasteriser is installed into a folder of its own, not with `npm install --no-save`,
  which would rebuild `node_modules` npm's way in a pnpm folder, with no age limit, no trust
  policy and install scripts running. `pnpm add --dir .icon-tools @resvg/resvg-js@2.6.2`, run once
  from the repository root, writes `.icon-tools/` (git-ignored, outside `frontend/` because pnpm
  would take a folder under it for part of the workspace), and `generate-icons.js` and
  `favicon-contrast.js` read it from there; `ICON_TOOLS_DIR` points them elsewhere.
  `@resvg/resvg-js` stays out of `package.json` on purpose: it is a native module and the icons
  change roughly never.
  The folder has no `pnpm-workspace.yaml` of its own, so the age limit and the trust policy do not
  apply to it; the exact pin and the lock it writes are what hold the version.
- Not covered: Node 26 has no Corepack, so the move to it must change `corepack enable` in the
  Dockerfile and the sentence about it in three documents: `README.md`, `frontend/README.md`
  and `docs/engineering-practices.md`. Those sentences now say that Node 24.12 or later carries
  Corepack and that Node 25 and later need pnpm installed another way.
- A cached `node:24-bookworm-slim` older than 24.12.0 fails at the install, at the Dockerfile's
  `corepack`: its Corepack cannot start pnpm 12, and `compose up --build` does not pull a newer
  image.
- A developer whose pnpm is not 12.15.0 and who does not use Corepack runs that pnpm as it is:
  `pmOnFail: ignore` turns off the check, and no run here exercised that.
- Not covered: that the dependency graph of GitHub lists the frontend again. It reads the default
  branch, so I can look only after the merge.
- Not covered: the Dockerfile, `pnpm/action-setup`, the cache of setup-node, and the demo
  suite's reporter arguments are proved by CI alone; fsevents is not installed on any machine I
  have used. `pnpm run dev` as the Playwright web server is used only locally: CI's real-stack
  job sets `E2E_BASE_URL`, which turns that web server off, so no run here or in CI has
  exercised it.

## Verified by

- `pnpm install --frozen-lockfile` in `frontend/`: it fails on a lock that does not match.
- The `frontend` job of `ci.yml`, step "The mock's worker matches the installed msw".

## Related

ADR-0054.
