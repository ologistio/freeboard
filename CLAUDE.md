# Freeboard

.NET 10 solution. Solution file: `Freeboard.slnx`.

## Projects

All projects live under `src/` and target `net10.0`.

| Project | Type | Purpose |
| --- | --- | --- |
| `src/Freeboard.Core` | classlib | Shared library used by all Freeboard components. |
| `src/Freeboard.Persistence` | classlib | MIT. All DB code (MySQL store, migrations, auth). Referenced by web and CLI. |
| `src/Freeboard.Enterprise` | classlib | Enterprise-only code (EE license carve-outs). |
| `src/Freeboard.Agent` | console | End-device agent that collects data. Cross-platform. |
| `src/Freeboard` | ASP.NET Core | Web-based UI. |
| `src/Freeboard.CLI` | console | Cross-platform CLI. |
| `src/Freeboard.Web` | ASP.NET Core | Public website. Static site generator (AspNetStatic). |

## Reference graph

- `Freeboard.Core` references nothing (the shared base).
- `Freeboard.Persistence` -> `Freeboard.Core` (MIT; holds all DB code).
- `Freeboard.Enterprise` -> `Freeboard.Core`.
- `Freeboard.Agent` -> `Freeboard.Core`.
- `Freeboard.CLI` -> `Freeboard.Core`, `Freeboard.Persistence`.
- `Freeboard` (web) -> `Freeboard.Core`, `Freeboard.Enterprise`, `Freeboard.Persistence`.
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

## Testing

```sh
dotnet test                       # run all test projects
```

Tests split into two tiers:

- Unit/web tests run with no external dependencies. They use in-memory fakes and
  `WebApplicationFactory`, so `dotnet test` passes out of the box.
- MySQL integration tests are gated on the `FREEBOARD_TEST_DB` connection string.
  When it is unset they **skip cleanly** (via `Xunit.SkippableFact`); when it is
  set they run against a real database. Each test provisions a throwaway
  `fb_test_<guid>` database and drops it on dispose (see
  `tests/Freeboard.TestInfrastructure/MySqlTestDatabase.cs`).
- The SMTP auth-email integration test is gated on `FREEBOARD_TEST_SMTP`. When it
  is unset it **skips cleanly** (via `Xunit.SkippableFact`); when it is set it
  sends through a local Mailpit and asserts delivery via the Mailpit HTTP API.
- The browser end-to-end tests (`tests/Freeboard.WebE2E`, Playwright) are gated on
  `FREEBOARD_TEST_E2E` **and** a launchable Chromium. When the var is unset or no
  browser is installed they **skip cleanly** (via `Xunit.SkippableFact`); set both
  to run them. They boot the real app over an HTTPS Kestrel socket (so the Secure
  `__Host-` session cookie sticks) with the same in-memory fakes, so E2E needs no
  MySQL. WebAuthn ceremonies run through the CDP virtual authenticator (no hardware
  key). To run them:

  ```sh
  dotnet build tests/Freeboard.WebE2E
  pwsh tests/Freeboard.WebE2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
  export FREEBOARD_TEST_E2E=1
  dotnet test tests/Freeboard.WebE2E
  ```

### Start the test services

The test compose file `tests/Freeboard.TestInfrastructure/docker-compose.yml`
defines a local MySQL (and `FREEBOARD_DB` at runtime) plus a Mailpit SMTP sink;
one command brings up both. From the repo root:

```sh
docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d
```

It exposes MySQL 8.4 on `127.0.0.1:3306` with database/user/password
`freeboard`, and an init grant script so the `freeboard` user can create the
`fb_test_*` throwaway databases.

Then run the integration tests by pointing `FREEBOARD_TEST_DB` at it:

```sh
export FREEBOARD_TEST_DB="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"
dotnet test
```

Tear down with `docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml down`
(add `-v` to also drop the data volume). The connection string is a secret in
real deployments - supply it via env var, user-secrets, or a config provider;
never commit it.

### SMTP integration test (Mailpit)

The Mailpit sink (started above) exposes SMTP on `127.0.0.1:1025` (unencrypted)
and the web UI / messages API on `127.0.0.1:8025`. Run the SMTP integration test
by pointing `FREEBOARD_TEST_SMTP` at it:

```sh
export FREEBOARD_TEST_SMTP="Smtp=127.0.0.1:1025;Http=http://127.0.0.1:8025"
dotnet test
```

`FREEBOARD_TEST_SMTP` is one connection-string-shaped value: `Smtp=host:port` is
the SMTP send target, `Http=base-url` is the Mailpit HTTP API base. Unset, the
test skips.

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
