# Tasks

Retrospective change: the implementation already landed. Tasks are marked done
and record what was built for verification.

## 1. Freeboard web UI toolchain

- [x] 1.1 Add `src/Freeboard/package.json` with `tailwindcss`, `@tailwindcss/cli`,
  `alpinejs`, and `build`/`build:css`/`build:js`/`watch:*` scripts
- [x] 1.2 Add `src/Freeboard/assets/css/app.css` (Tailwind entry with `@source`
  on `Pages`) and `src/Freeboard/assets/js/app.js` (imports and starts Alpine)
- [x] 1.3 Run `bun install` to produce `src/Freeboard/bun.lock`

## 2. Freeboard.Web site toolchain

- [x] 2.1 Add `src/Freeboard.Web/package.json` mirroring the web UI scripts and deps
- [x] 2.2 Add `src/Freeboard.Web/assets/css/app.css` and
  `src/Freeboard.Web/assets/js/app.js`
- [x] 2.3 Run `bun install` to produce `src/Freeboard.Web/bun.lock`

## 3. Build integration

- [x] 3.1 Add `InstallAssets` and `BuildAssets` MSBuild targets to
  `src/Freeboard/Freeboard.csproj` (install when `node_modules` absent; build
  incrementally on `BeforeBuild` via `Inputs`/`Outputs`)
- [x] 3.2 Add the same targets to `src/Freeboard.Web/Freeboard.Web.csproj`
- [x] 3.3 Gitignore `node_modules/` and the generated `wwwroot/css/app.css` and
  `wwwroot/js/app.js` for both projects

## 4. Consume and document

- [x] 4.1 Link `~/css/app.css` and defer `~/js/app.js` from
  `src/Freeboard/Pages/Shared/_Layout.cshtml`
- [x] 4.2 Document the toolchain and the bun build dependency in `CLAUDE.md`

## 5. Verification

- [x] 5.1 `bun run build` in each web project produces `wwwroot/css/app.css` and
  `wwwroot/js/app.js`
- [x] 5.2 `dotnet build` for each web project regenerates deleted assets and
  succeeds
- [x] 5.3 A no-change `dotnet build` skips the `BuildAssets` target (incremental)
