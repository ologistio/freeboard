# frontend-asset-build Specification

## Purpose

Define how the web projects (`Freeboard` web UI and `Freeboard.Web` public site)
compile their front-end CSS and JavaScript. Each project builds Tailwind CSS and
Alpine from a project-local `assets/` source into `wwwroot` using bun, wired into
`dotnet build` so a normal build produces working assets with no separate manual
step.

## Requirements

### Requirement: Web projects build front-end assets from source

Each web project (`Freeboard` and `Freeboard.Web`) SHALL compile its front-end
CSS and JavaScript from a project-local `assets/` source directory into its
`wwwroot`. The CSS SHALL be produced from a Tailwind CSS entry
(`assets/css/app.css`, which imports Tailwind) to `wwwroot/css/app.css`. The
JavaScript SHALL be produced from an entry (`assets/js/app.js`, which imports and
starts Alpine) bundled to `wwwroot/js/app.js`. The Tailwind build SHALL scan the
project's Razor (`.cshtml`) templates so utilities used in markup appear in the
output.

#### Scenario: CSS is generated from the Tailwind entry

- **WHEN** the asset build runs for a web project
- **THEN** `wwwroot/css/app.css` is produced from `assets/css/app.css`
- **AND** it contains the Tailwind utilities referenced by the project's `.cshtml`
  templates

#### Scenario: Alpine is bundled to a single script

- **WHEN** the asset build runs for a web project
- **THEN** `wwwroot/js/app.js` is produced from `assets/js/app.js`
- **AND** it is a self-contained browser bundle that starts Alpine on load

### Requirement: Asset build runs as part of dotnet build

Building a web project with `dotnet build` SHALL produce its front-end assets
without a separate manual step. The build SHALL install the front-end
dependencies on first use (when `node_modules` is absent) and SHALL be
incremental: it SHALL NOT rebuild the assets when no front-end input
(`assets/`, `package.json`, the lockfile, or a `.cshtml` file) has changed.

#### Scenario: Fresh build produces assets

- **WHEN** `dotnet build` runs for a web project whose `wwwroot/css/app.css` and
  `wwwroot/js/app.js` do not yet exist
- **THEN** the build produces both files

#### Scenario: No-change rebuild skips asset work

- **WHEN** `dotnet build` runs again for a web project with no change to any
  front-end input
- **THEN** the asset build step does not re-run

### Requirement: Reproducible, pinned front-end dependencies

Each web project SHALL declare its front-end dependencies (Tailwind CSS and
Alpine) in a project-local `package.json` and SHALL commit a lockfile
(`bun.lock`) that pins resolved versions. The generated output files
(`wwwroot/css/app.css`, `wwwroot/js/app.js`) and the installed `node_modules`
directory SHALL NOT be committed.

#### Scenario: Lockfile is committed, generated output is not

- **WHEN** the repository is inspected after an asset build
- **THEN** each web project's `package.json` and `bun.lock` are tracked in git
- **AND** its `wwwroot/css/app.css`, `wwwroot/js/app.js`, and `node_modules` are
  gitignored

### Requirement: Missing build toolchain fails loudly

The asset build SHALL fail with an error when the front-end toolchain (bun) is
not available. `dotnet build` for a web project MUST NOT silently skip asset
generation when bun is missing.

#### Scenario: bun not on PATH

- **WHEN** `dotnet build` runs for a web project and bun is not on PATH
- **THEN** the build fails with an error identifying the missing command
