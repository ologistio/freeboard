# Freeboard

.NET 10 solution. Solution file: `Freeboard.slnx`.

## Projects

All projects live under `src/` and target `net10.0`.

| Project | Type | Purpose |
| --- | --- | --- |
| `src/Freeboard.Core` | classlib | Shared library used by all Freeboard components. |
| `src/Freeboard.Enterprise` | classlib | Enterprise-only code (EE license carve-outs). |
| `src/Freeboard.Agent` | console | End-device agent that collects data. Cross-platform. |
| `src/Freeboard` | ASP.NET Core | Web-based UI. |
| `src/Freeboard.CLI` | console | Cross-platform CLI. |
| `src/Freeboard.Web` | ASP.NET Core | Public website. Static site generator (AspNetStatic). |

## Reference graph

- `Freeboard.Core` references nothing (the shared base).
- `Freeboard.Enterprise` -> `Freeboard.Core`.
- `Freeboard.Agent` -> `Freeboard.Core`.
- `Freeboard.CLI` -> `Freeboard.Core`.
- `Freeboard` (web) -> `Freeboard.Core`, `Freeboard.Enterprise`.
- `Freeboard.Web` references nothing (standalone public website).

`Freeboard.Agent` and `Freeboard.CLI` must NOT reference `Freeboard.Enterprise`. They
ship as community components and must build and run on Windows, Linux, and macOS.

## Build and run

```sh
dotnet build                      # build all projects
dotnet run --project src/Freeboard.CLI
dotnet run --project src/Freeboard.Agent
dotnet run --project src/Freeboard      # web UI
```

## Freeboard.Web (public website)

A static site generator built on AspNetStatic. Razor Pages under `Pages/`
render the content; routes to generate are registered as `PageResource` entries
in `Program.cs`.

```sh
dotnet run --project src/Freeboard.Web              # serve live for editing
dotnet run --project src/Freeboard.Web -- ssg-only  # generate static site, then exit
```

`ssg-only` writes the static files to `src/Freeboard.Web/_site/` (gitignored)
and exits.

`Program.cs` disables HTTP/3 (QUIC) via a `DllImportResolver`. The SSG only
serves HTTP/1 to itself, so QUIC is never needed.

## Cross-platform publish

Agent and CLI are portable by default (any OS with the .NET 10 runtime). For
self-contained, OS-specific binaries:

```sh
dotnet publish src/Freeboard.CLI   -c Release -r win-x64    --self-contained
dotnet publish src/Freeboard.CLI   -c Release -r linux-x64  --self-contained
dotnet publish src/Freeboard.CLI   -c Release -r osx-arm64  --self-contained
dotnet publish src/Freeboard.Agent -c Release -r win-x64    --self-contained
dotnet publish src/Freeboard.Agent -c Release -r linux-x64  --self-contained
dotnet publish src/Freeboard.Agent -c Release -r osx-arm64  --self-contained
```
