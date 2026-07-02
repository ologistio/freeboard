## Context

`Freeboard` (web UI) and `Freeboard.Web` (AspNetStatic public site) are the only
projects that render HTML. Before this change they had no CSS/JS build: styling
was hand-written directly in `wwwroot` and there was no path to a utility CSS
framework or a client-side interactivity library. The Agent and CLI ship no web
assets and are unaffected.

Constraints: the build must run on Windows, Linux, and macOS; it must not slow
routine incremental `dotnet build`; and it must respect the repo conventions
(plain ASCII, code-as-liability, MIT default).

## Goals / Non-Goals

**Goals:**

- Standard front-end stack: Tailwind CSS v4 and Alpine.
- One command to build: assets are produced by `dotnet build`, no separate manual
  step for the common case.
- Reproducible installs via a committed lockfile.
- Incremental: no asset rebuild when nothing front-end changed.
- Per-project isolation: each web project owns its own assets and dependencies.

**Non-Goals:**

- Migrating existing hand-written CSS to Tailwind.
- A shared layout / asset consumption for `Freeboard.Web`.
- PostCSS, extra plugins, or a custom minifier.

## Decisions

**bun as the JS toolchain.** bun runs the Tailwind CLI and bundles Alpine, and
its lockfile (`bun.lock`) gives reproducible installs. Single tool for install +
bundle. Alternative: Node + npm/pnpm. Rejected to avoid a second runtime and
slower installs; bun covers install and bundling in one binary.

**Tailwind CSS v4 via `@tailwindcss/cli`.** v4 is CSS-first: the entry file is
`@import "tailwindcss";` with no `tailwind.config.js`. Template scanning is
automatic; we add an explicit `@source "../../Pages"` so utilities used in
`.cshtml` are always detected regardless of the CLI working directory. Alternative:
the standalone Tailwind binary. Rejected because it is a separate download to
manage per platform; the npm CLI installs through the same bun step as Alpine.

**Alpine bundled with `bun build`.** `assets/js/app.js` imports Alpine, assigns
`window.Alpine`, and calls `Alpine.start()`; `bun build` emits a single
browser bundle. Alternative: a CDN `<script>`. Rejected to keep assets
self-hosted, versioned in the lockfile, and offline-buildable.

**Per-project toolchain, not a repo-root workspace.** Each web project has its
own `package.json`, `assets/`, and `bun.lock`. The two projects build and deploy
independently (`Freeboard.Web` is a standalone SSG), so a shared root workspace
would couple them for no benefit. Trade-off: the Tailwind/Alpine dependency lines
are duplicated across two `package.json` files; acceptable for two projects.

**MSBuild wiring with incrementality.** A `BuildAssets` target runs
`bun run build` `BeforeTargets="BeforeBuild"`, gated by `Inputs`/`Outputs` so it
re-runs only when `assets/`, `package.json`, `bun.lock`, or a `.cshtml` file
changes. A separate `InstallAssets` target runs `bun install` only when
`node_modules` is absent. Alternative: build assets outside dotnet (npm script /
CI step). Rejected so a plain `dotnet build` or `dotnet run` always yields
working assets without an out-of-band step.

**Generated outputs gitignored.** `wwwroot/css/app.css` and `wwwroot/js/app.js`
are build products, so they are gitignored; `bun.lock` is committed. Rationale:
avoid committing generated, minified files that churn on every build. Trade-off:
a build environment must have bun; documented in `CLAUDE.md`.

## Risks / Trade-offs

- bun becomes a build-time dependency for the two web projects. [Risk: CI or a
  contributor without bun cannot build them] -> Documented requirement in
  `CLAUDE.md`; the build fails loudly (no silent skip) if bun is missing.
- Automatic Tailwind source detection could miss `.cshtml` under some working
  directories. [Risk: missing utilities in output] -> Explicit `@source` on
  `Pages` plus `.cshtml` in the MSBuild `Inputs`.
- `Freeboard.Web` builds assets it does not yet consume. [Risk: dead output] ->
  Accepted as a scoped follow-up; the pipeline is proven end-to-end in
  `Freeboard`.
- `@parcel/watcher` native postinstall is blocked by bun's trust policy. [Risk:
  `watch:*` scripts degrade] -> Watch falls back to prebuilt binaries; run
  `bun pm trust @parcel/watcher` only if native watching is needed. Does not
  affect the one-off `build`.
