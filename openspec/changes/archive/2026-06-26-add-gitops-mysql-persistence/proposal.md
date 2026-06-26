## Why

The GitOps baseline (add-gitops-config-management, archived) loads and validates
declarative compliance config from git but persists nothing: `apply` is dry-run
only and there is no backing store. Following FleetDM's lead, Freeboard needs a
durable store so validated config can be read back by the web UI and, in a later
increment, reconciled by a real apply.

The persisted data (`Standard`, `Control`, `Scope`) is the GENERAL compliance
domain, not a gitops artifact. GitOps is one ingestion mechanism that writes into
it. This change therefore adds a general compliance persistence layer - a
MySQL-backed store, schema and hand-written migrations, a read abstraction, and the
web read path - plus a GitOps importer that writes into it, without yet enabling
real apply writes. Naming reflects this: the store and its read surface are
general/compliance-named; the importer and the `sync` command stay gitops-named;
migrations are a system concern. See design D9 for the framing rationale.

## What Changes

- Add a MySQL-backed store for the compliance domain (`Standard`, `Control`,
  `Scope`), keyed on the immutable `id`, preserving the cross-ref relations
  (`Control.maps_to` -> Standard ids, `Scope.controls` -> Control ids).
- Add a schema with versioned, forward-only, hand-written SQL migrations applied
  by a small ordered runner that records version, checksum, and applied-at in a
  `schema_migrations` table. Domain tables are `standards`, `controls`, `scopes`
  and join tables `control_standards`, `scope_controls` (no `gitops_` prefix).
  Identity is `id`; upsert/match logic keys on `id`, never `title`.
- Use Dapper over MySqlConnector with hand-written SQL (not an ORM). The
  data-access decision and the rejected EF Core + Pomelo runner-up are justified
  in design.md; the deciding factor is that Pomelo has no net10/EF Core 10
  provider released (latest is 9.0.0).
- Add abstractions with MySQL implementations: `IComplianceStore` (general read),
  `IGitOpsImporter` (the git-sourced writer), `IMigrationRunner` (system
  migrations). The web app depends only on `IComplianceStore`; the CLI depends on
  `IGitOpsImporter` (for `gitops sync`) and `IMigrationRunner` (for
  `system migrate`).
- Add a new MIT project, `Freeboard.Persistence`, to hold the store, SQL,
  migrations, and the MySqlConnector/Dapper dependency. It is one project with
  internal namespaces separating the three roles: the general store/reader
  (`Freeboard.Persistence`), the gitops importer (`Freeboard.Persistence.GitOps`),
  and the system migrations (`Freeboard.Persistence.System`). The MySQL client
  lives here, NOT in `Freeboard.Core` (which must stay network-free; see Impact).
- Wire the web read path: the web app reads persisted standards/controls/scopes
  through `IComplianceStore` and exposes them at the general routes
  `GET /api/standards`, `/api/controls`, `/api/scopes`. A new
  `GET /api/compliance/status` reports persisted per-kind counts. The existing
  `GET /api/gitops/status` is unchanged and stays a gitops concern.
- Add a CLI `gitops sync <path>` command (under the existing `gitops` group) that
  loads, validates, and imports the config into the store via `IGitOpsImporter`.
  This is the in-scope mechanism that populates the store while public `apply`
  stays dry-run. `sync` is an explicit, operator-run load, not the reconciling
  apply path; it has no read-only-mode or auth guard yet. `sync` fails clearly if
  migrations are not current unless `--migrate` is supplied.
- Add a CLI `system migrate` command (under a new `system` command group) that
  applies pending SQL migrations via `IMigrationRunner`.
- Keep `apply` dry-run only. No authenticated apply endpoint, no soft-delete on
  removal, no read-only write guards on the store. Those are the next change.
  Correct the existing CLI comments so only `validate` and `apply --dry-run`
  claim to write nothing.
- Add a docker-compose MySQL for local dev and integration tests, a docs
  section, and a connection-string-as-secret note. Two env vars are documented
  and distinct: `FREEBOARD_DB` (the runtime connection string for `gitops sync`
  and `system migrate`) and `FREEBOARD_TEST_DB` (the integration-test DB
  discovery string; tests skip when it is absent).

No breaking changes. `apply` behaviour is unchanged. All else is new.

## Capabilities

### New Capabilities

- `compliance-persistence`: the MySQL-backed store for the compliance domain -
  schema, hand-written forward-only migrations with a checksum-tracking runner,
  the `IComplianceStore`/`IGitOpsImporter`/`IMigrationRunner` abstractions and
  their MySQL implementations, identity/upsert keyed on `id`, cross-ref relations,
  and the import write path that populates the store from a validated config.
- `compliance-web-read`: the web read path that serves persisted standards,
  controls, and scopes from the store via read-only HTTP endpoints
  (`/api/standards`, `/api/controls`, `/api/scopes`), and the persisted-count
  summary at `GET /api/compliance/status`.
- `system-cli`: the `system` CLI command group and its `system migrate` command,
  with the `--connection-string`/`FREEBOARD_DB` sourcing and exit-code-3
  operational-failure behavior.

### Modified Capabilities

- `gitops-cli`: add the `gitops sync <path>` subcommand. `validate` and
  `apply --dry-run` are unchanged; `apply` stays dry-run only. The `migrate`
  command now lives under `system`, not `gitops`.

## Impact

- Code:
  - New MIT project `src/Freeboard.Persistence`: SQL access (Dapper over
    MySqlConnector), hand-written migrations and runner, and the
    store/importer/migration-runner implementations across internal namespaces
    (general store, gitops importer, system migrations). Holds the MySQL client
    dependency.
  - `Freeboard.CLI`: add `gitops sync` and `system migrate`; add a
    ProjectReference to the new persistence project. Still no Enterprise
    reference; still cross-platform.
  - `Freeboard` (web app): general read endpoints + DI registration of
    `IComplianceStore`; add a ProjectReference to the persistence project.
  - `Freeboard.Core`: unchanged. The store maps the Core domain records but Core
    gains no DB dependency, preserving the no-network-Core structural test.
  - Test projects: new persistence integration tests (skipped without MySQL),
    new web read-path tests, CLI sync/migrate tests, and an extension of the
    architecture tests to cover the new project's placement.
- Dependencies: `Dapper` + `MySqlConnector`, confined to
  `src/Freeboard.Persistence`. No DB dependency reaches `Freeboard.Core`,
  `Freeboard.Agent`, or the no-network code path. No ORM and no EF migration
  tooling are added. Data-access decision and runner-up are in design.md.
- Reference graph: one new MIT project. CLI and web reference it; Core, Agent,
  and Enterprise do not. CLI and Agent still never reference Enterprise.
- Licensing: MIT. Persistence is community plumbing, not a paid feature, so
  nothing goes in `Freeboard.Enterprise`. A future EE feature may consume the
  store one-way (`Enterprise -> Persistence -> Core`).
- Docs: docker-compose MySQL, a `docs/gitops.md` persistence section, and a
  README note. Connection strings are secrets: env vars / user-secrets / config
  provider only, never in YAML or committed config.

## Non-goals

In scope is the store, schema/migrations, read abstraction, web read path, the
GitOps importer, and the `gitops sync` / `system migrate` commands. NOT in this
change (next increment):

- No real, reconciling `apply`. `apply` stays dry-run only and writes nothing.
- No authenticated (bearer-token) apply endpoint and no write guard tying store
  writes to read-only mode. `sync` is an unguarded operator command for now.
- No soft-delete/disable on removal. `sync` replaces the persisted set; the
  soft-delete-on-removal semantics for GitOps-owned resources land with real
  apply. Recorded as a forward principle, not built.
- No drift detection, reconciliation loop, or bidirectional export to YAML.
- No new domain kinds. The schema stays `Standard`, `Control`, `Scope`.
- No web UI page rendering. Only JSON read endpoints.
- No multi-tenant or history/audit tables. Single logical config set.
- No DB provider other than MySQL. No SQLite/Postgres support promised.
- No CI job that stands up MySQL as a required gate. Integration tests skip when
  no MySQL is reachable; design.md covers the opt-in story.
- No agent involvement. The Agent does not change.
