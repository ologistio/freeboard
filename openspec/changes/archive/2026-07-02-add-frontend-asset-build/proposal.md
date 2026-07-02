## Why

The two ASP.NET Core web projects (`Freeboard` web UI and `Freeboard.Web` public
site) had no CSS/JS toolchain: styles were hand-written in `wwwroot` and there was
no way to use a utility CSS framework or a client-side interactivity library. We
want Tailwind CSS and Alpine as the standard front-end stack, built reproducibly
as part of the normal `dotnet build` so contributors do not run a separate manual
step. This change canonicalises the bun-based asset build that was implemented.

Licensing: MIT. The toolchain and generated assets sit in `Freeboard` and
`Freeboard.Web`, both MIT. `Freeboard` references `Freeboard.Enterprise`, but the
asset build is generic front-end tooling with no EE carve-out. No MIT/EE boundary
is crossed.

## What Changes

- Add a per-project bun front-end toolchain to `Freeboard` and `Freeboard.Web`:
  `package.json`, a source `assets/` dir (`assets/css/app.css` Tailwind entry,
  `assets/js/app.js` Alpine entry), and a committed `bun.lock`.
- Dependencies: Tailwind CSS v4 (`tailwindcss` + `@tailwindcss/cli`) and
  `alpinejs`.
- Build output goes to `wwwroot/css/app.css` and `wwwroot/js/app.js` in each
  project; both are gitignored generated artifacts.
- Wire the asset build into MSBuild so `dotnet build` produces the assets
  incrementally, installing bun dependencies on first use.
- Consume the built assets from the `Freeboard` shared layout.
- Ignore `node_modules/` and the generated output files in `.gitignore`.
- Document the toolchain and the new bun build dependency in `CLAUDE.md`.

## Capabilities

### New Capabilities

- `frontend-asset-build`: how the web projects compile front-end CSS and JS
  (Tailwind CSS + Alpine) from source into `wwwroot` via bun, wired into
  `dotnet build`, including output locations, incrementality, and the build-time
  bun dependency.

### Modified Capabilities

<!-- None. No existing spec's requirements change. -->

## Non-goals

- No visual redesign or migration of existing hand-written CSS (`auth.css`) to
  Tailwind utilities.
- No shared layout for `Freeboard.Web`; its assets are built but not yet consumed
  by a page (follow-up).
- No additional Tailwind plugins, Alpine plugins, PostCSS pipeline, or CSS/JS
  minification beyond what the bun and Tailwind CLIs provide by default.
- No change to the `Freeboard.Web` AspNetStatic generation flow beyond serving the
  new static files.

## Impact

- Projects: `src/Freeboard`, `src/Freeboard.Web` (new `package.json`,
  `assets/`, `bun.lock`; modified `.csproj`).
- Build: `dotnet build` for these two projects now requires `bun` on PATH.
- Files: `.gitignore`, `CLAUDE.md`, `src/Freeboard/Pages/Shared/_Layout.cshtml`.
- Dependencies (dev/build-time, not NuGet): `tailwindcss`, `@tailwindcss/cli`,
  `alpinejs` via bun.
