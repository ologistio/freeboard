## 1. Persistence project, abstractions, and SQL store

Commit: `feat(persistence): add mysql-backed compliance store project`

- [x] 1.1 Add a new MIT project `src/Freeboard.Persistence` (classlib,
  net10.0) referencing only `src/Freeboard.Core`. Add it to `Freeboard.slnx`.
- [x] 1.2 Add PackageReferences (pinned): `Dapper` and `MySqlConnector`. Confine
  these to this project; do not add any DB package to `Freeboard.Core`,
  `Freeboard.Agent`, or `Freeboard.CLI` directly. Do not add an ORM or EF
  migration tooling.
- [x] 1.3 Add `PersistenceOptions` (connection string) and a
  connection-factory seam over `MySqlConnector`.
- [x] 1.4 Define the three abstractions in role-separated namespaces:
  `IComplianceStore` (namespace `Freeboard.Persistence`; read standards, controls
  with resolved `maps_to`, scopes with resolved `controls`, and per-kind counts;
  reads ordered by `id`, relation arrays ordered by id), `IGitOpsImporter`
  (namespace `Freeboard.Persistence.GitOps`; `ImportAsync(GitOpsConfig)`;
  documents the already-validated precondition, does not re-validate), and
  `IMigrationRunner` (namespace `Freeboard.Persistence.System`; both
  `ApplyPendingAsync` and a `GetState`/`GetStateAsync` that reports current vs
  pending migration versions; `GetState` is strictly read-only - no DDL, no
  writes, does NOT create `schema_migrations`; on a fresh DB without the table it
  reports every embedded migration pending without creating it; the bootstrap
  lives in `ApplyPendingAsync`).
- [x] 1.5 Implement `MySqlComplianceStore` with hand-written SQL joined reads via
  Dapper, ordered by `id` with relation id arrays ordered by id.
- [x] 1.6 Implement `MySqlGitOpsImporter.ImportAsync` in one DML transaction, in
  FK-safe order: (1) upsert all domain rows by `id`
  (`INSERT ... ON DUPLICATE KEY UPDATE title, api_version, updated_at`), setting
  `created_at` on insert only; (2) replace ALL cross-ref join rows for the
  imported set (delete then insert the whole set, not a per-parent diff);
  (3) delete domain rows whose `id` is absent, in FK-safe order (scopes,
  controls, standards). Match on `id` only, never `title`. Hard-remove only
  (soft-delete deferred). Relies on the Core no-dangling-cross-ref invariant so
  FK constraints hold at commit.
- [x] 1.7 Add split DI extensions: `AddComplianceStore(connectionString)`
  registering the connection factory and `IComplianceStore` only (the web app uses
  this); and `AddGitOpsImport` / `AddSystemMigrations` (or a combined CLI-side
  `AddPersistenceCli`) registering `IGitOpsImporter` and `IMigrationRunner` (the
  CLI uses these). Do NOT register the importer or migration runner in the
  compliance-store extension.

## 2. Schema migrations and runner

Commit: `feat(persistence): add initial schema migration and runner`

- [x] 2.1 Add `Migrations/001_initial_schema.sql` (embedded resource, named
  `NNN_slug.sql` fixed-width) creating the three entity tables
  (`standards`, `controls`, `scopes`) and the two relation
  tables (`control_standards`, `scope_controls`). Use
  `CREATE TABLE IF NOT EXISTS` so the migration is re-runnable. Pin charset
  utf8mb4 and collation utf8mb4_bin on every `id` and FK column. Add FKs ON DELETE
  CASCADE, composite PKs on the join tables, and `api_version`/`title`/
  `created_at`/`updated_at` columns on the domain tables. Do NOT create
  `schema_migrations` here; it is bootstrapped by the runner (2.2).
- [x] 2.2 Implement `MySqlMigrationRunner`. Bootstrap in the apply path only:
  `ApplyPendingAsync` runs `CREATE TABLE IF NOT EXISTS schema_migrations (...)` as
  its first step, before it reads applied state, so a completely empty DB can be
  migrated. `GetState` is strictly read-only: it performs no DDL and no writes and
  does NOT bootstrap the table; on a fresh DB without `schema_migrations` it
  reports every embedded migration pending (none current, no
  recorded-but-missing-migration violation) without creating the table.
  Record each migration under `version` = the file name without extension (the
  `NNN_slug` stem, e.g. `001_initial_schema.sql` -> `001_initial_schema`), never
  with the `.sql` extension. Then enumerate `Migrations/*.sql` sorted by parsed
  numeric ordinal (fixed-width padding makes this equal to lexical), SHA-256 each,
  compare against `schema_migrations`, skip on matching checksum, fail
  loudly on a changed checksum of an applied migration. Before applying any
  pending migration, fail loudly if `schema_migrations` records an applied
  version whose stem is NOT present among the embedded migrations (a deleted or
  renamed applied migration), distinct from the checksum-mismatch case. For a
  pending migration: run its statements,
  and only after they all succeed record `(version, checksum, applied_at)`. Do
  NOT claim DDL transactional atomicity (MySQL DDL implicit-commits); on partial
  failure fail loudly and leave the version unrecorded so it is re-attemptable.
  Use a transaction only for the version-record insert and any DML. Forward-only;
  no down migrations. `GetState` reports current vs pending versions.
- [x] 2.3 Document the apply-migrations-first ordering for `sync` and the web app.
  The web app and store never auto-migrate.

## 3. Persistence tests

Commit: `test(persistence): cover mapping, upsert, runner, and integration`

- [x] 3.1 Add `tests/Freeboard.Persistence.Tests`; add it to
  `Freeboard.slnx`.
- [x] 3.2 Unit tests (no MySQL): `GitOpsConfig` -> SQL parameter mapping; import
  plan keys on `id` and updates `title` without matching on `title`; cross-ref
  rows derive from `maps_to`/`controls`; the migration runner's ordering across
  `001`/`002`/`010` fixtures (asserting numeric-ordinal order); checksum
  comparison logic; the migrator's `GetState` current/pending classification
  including "all pending when none recorded"; and that the migrator fails when a
  recorded applied version has no matching embedded migration stem (deleted or
  renamed applied migration), distinct from a checksum mismatch. Do not use an
  in-memory provider as a relational stand-in.
- [x] 3.3 Integration tests (real MySQL, skipped when none reachable via the
  `FREEBOARD_TEST_DB` env var; distinct from the runtime
  `FREEBOARD_DB`):
  - migrate a completely empty schema from scratch (no tables at all) and assert
    the runner bootstraps `schema_migrations` and ends with all six tables
    present with binary collation, FKs, and indexes;
  - `GetState` reports all pending on an un-migrated DB and none pending after
    applying; against a completely empty DB (no `schema_migrations`) `GetState`
    reports all pending AND creates no tables (the DB is still empty afterward);
  - a migration that fails partway leaves its version unrecorded and is
    re-attempted on a re-run;
  - the migrator fails loudly when `schema_migrations` records an applied
    version that has no matching embedded migration stem (deleted or renamed
    applied migration), and applies no migrations;
  - `sync` a fixture; read back and assert counts and cross-refs;
  - re-`sync` with a changed `title` and a dropped `id` and assert update-by-id
    and removal-by-id;
  - re-`sync` preserves `created_at` and advances `updated_at`;
  - re-`sync` with a changed `api_version` updates the stored `api_version`;
  - FK-safe drop: a sync that drops a standard referenced by a control in the OLD
    db state, where the control is also updated/removed in the same config,
    succeeds without an FK violation;
  - assert case-distinct ids (`ctrl-a` vs `CTRL-A`) remain distinct rows;
  - assert an invalid config writes nothing (asserted at the sync level);
  - `sync` a valid config WITHOUT `--migrate` against a completely EMPTY database
    exits `3` AND leaves the database with NO tables (no `schema_migrations`, no
    domain tables) - the migrate-first gate performs no DDL;
  - `sync --migrate` a valid config against a completely EMPTY database
    bootstraps `schema_migrations`, migrates, imports, and exits `0` (happy path
    pairing the empty-DB no-migrate case above).
- [x] 3.4 Ensure the suite is green without MySQL: integration tests skip with a
  clear message when no test DB is configured.

## 4. CLI gitops sync and system migrate commands

Commit: `feat(cli): add gitops sync and system migrate against the store`

- [x] 4.1 Add a ProjectReference from `Freeboard.CLI` to
  `Freeboard.Persistence`. Confirm no `Freeboard.Enterprise` reference.
- [x] 4.2 Add `gitops sync <path>` (under the existing `gitops` command group):
  load+validate via Core; on clean validation, check migration state (via
  `IMigrationRunner.GetState`) and call `IGitOpsImporter.ImportAsync`. If
  migrations are not current and `--migrate` is absent, fail with a clear message
  and write nothing (exit `3`); if `--migrate` is present, apply pending
  migrations first. The importer is called only after a clean validation (caller
  guarantees the validated-config precondition). Connection string from a
  per-subcommand `--connection-string` option or `FREEBOARD_DB` env var (explicit
  option overrides the env var), never YAML.
- [x] 4.3 Add a new `system` command group and a `system migrate` command (NOT
  under `gitops`): apply pending migrations via `IMigrationRunner`; same
  per-subcommand `--connection-string`/`FREEBOARD_DB` sourcing and precedence.
  Migrations are a system/platform concern, so the command lives under `system`.
- [x] 4.4 Exit codes: `gitops` per the gitops-cli spec matrix, `system migrate`
  per the system-cli spec: `0` success; `1` validation/input error for `gitops`
  commands (writing nothing); `2` `apply` without `--dry-run` (unchanged); `3`
  operational failure for `gitops sync` and `system migrate` - missing connection
  string, DB unreachable, schema not current without `--migrate` (`sync`),
  migration checksum mismatch, an applied migration missing from the embedded
  migrations (deleted or renamed), or migration execution failure - with a clear
  stderr message.
- [x] 4.5 Leave `apply` dry-run only and unchanged. Correct the class-level
  documentation comment on `src/Freeboard.CLI/GitOpsCommands.cs` (currently
  "Makes no network calls and writes no state") so it no longer claims the group
  writes no state or makes no network calls; scope the non-writing,
  non-connecting description to `validate` and `apply --dry-run`. Do not let
  `sync` and `apply` duplicate validation logic (both call the same Core
  load+validate).

## 5. CLI gitops sync and system migrate tests

Commit: `test(cli): cover gitops sync and system migrate exit codes and store calls`

- [x] 5.1 Test: `gitops sync` on an invalid fixture exits `1`, prints errors to
  stderr, and does not call the importer (importer double).
- [x] 5.2 Test: `gitops sync` on a valid fixture calls `ImportAsync` once with the
  loaded config and exits `0` (importer double).
- [x] 5.3 Test: `gitops sync` against an un-migrated store without `--migrate`
  exits `3` and writes nothing (reads state via the read-only `GetState`, calls
  neither `ApplyPendingAsync` nor `ImportAsync`); with `--migrate` it applies then
  imports. The empty-DB end-to-end assertions (exit `3` with no tables created;
  `--migrate` happy path) are real-MySQL tests through the actual
  `GitOpsCommands.Sync` path in `tests/Freeboard.CLI.Tests/SyncMySqlIntegrationTests.cs`
  (skippable when `FREEBOARD_TEST_DB` is absent).
- [x] 5.4 Test: `system migrate` invokes the migration runner and exits `0`.
- [x] 5.5 Test: `apply` behaviour unchanged (still dry-run, still rejects without
  `--dry-run` with exit `2`); `apply --dry-run` exits `0`.
- [x] 5.6 Test exit code `3` paths: `gitops sync`/`system migrate` with no
  connection string (neither option nor env var) exits `3`; a checksum-mismatch
  migration causes `system migrate` to exit `3` with a clear message; a recorded
  applied migration whose embedded SQL file is missing (deleted or renamed) causes
  `system migrate` to exit `3` with a clear message; a migration whose SQL fails
  during execution (a malformed/erroring statement) causes `system migrate` to
  exit `3` with a clear message and leaves that migration's version unrecorded in
  `schema_migrations` (re-attemptable). One test per distinct code where practical.
- [x] 5.7 Test: an explicit `--connection-string` overrides `FREEBOARD_DB`
  for `gitops sync`/`system migrate`.

## 6. Web read path

Commit: `feat(web): serve persisted compliance domain from the store`

- [x] 6.1 Add a ProjectReference from `Freeboard` (web) to
  `Freeboard.Persistence`; register only the reader via
  `AddComplianceStore(ConnectionStrings:Freeboard)` in `Program.cs` (NOT the
  importer or migration runner). The web app does not auto-connect at startup,
  does not auto-migrate, and does not auto-sync.
- [x] 6.2 Add `GET /api/standards`, `/api/controls`, `/api/scopes` (general
  routes, NOT under `/api/gitops/`) reading through `IComplianceStore`, returning
  ids, titles, and resolved cross-refs ordered by `id` (relation arrays ordered by
  id). GET-only. On an unreachable store, return an RFC 7807 problem (HTTP 503)
  rather than an unhandled exception.
- [x] 6.3 Add `GET /api/compliance/status` returning a `persisted` object of
  per-kind counts. The persisted counts live on this general status route, NOT on
  `/api/gitops/status` (which stays a gitops concern and is unchanged). The
  `persisted` object is ALWAYS present: integer counts when the store is
  reachable; `{ "standards": null, "controls": null, "scopes": null }` (each
  per-kind value `null`, not omitted, not `{}`, not `0`) when the store is
  unreachable, returning HTTP 200 without failing the whole response.

## 7. Web read-path tests

Commit: `test(web): cover compliance read endpoints and status counts`

- [x] 7.1 `WebApplicationFactory` tests injecting an `IComplianceStore` double in
  ALL web tests so the suite is green without MySQL: the three read endpoints
  (`/api/standards`, `/api/controls`, `/api/scopes`) return the expected
  ids/titles/cross-refs ordered by `id`; `GET /api/compliance/status` returns the
  `persisted` counts object supplied by the double. These double tests assert
  serialization shape and ordering only; cross-ref resolution correctness from SQL
  joins is covered by the MySQL integration tests (task 3.3), skipped without
  MySQL.
- [x] 7.2 Test: a `GET` to a read endpoint is served normally in read-only mode
  (not blocked by the read-only middleware).
- [x] 7.3 DI test: the web app's service provider resolves `IComplianceStore` and
  does NOT resolve `IGitOpsImporter` or `IMigrationRunner`.
- [x] 7.4 Unreachable-store test: with the `IComplianceStore` double throwing
  (store unreachable), the read endpoints (`/api/standards`, `/api/controls`,
  `/api/scopes`) return HTTP 503 with an RFC 7807 problem body, and
  `GET /api/compliance/status` returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "scopes": null }`.
- [x] 7.5 GitOps-status regression test: a `WebApplicationFactory` test asserting
  `GET /api/gitops/status` returns ONLY its existing fields (`gitOps` boolean, and
  `repositoryUrl` when set) and does NOT include a `persisted` summary, and that
  the endpoint works with no `IComplianceStore` registered / an unreachable store
  (the gitops status handler does not depend on `IComplianceStore`).

## 8. Architecture tests and placement guards

Commit: `test(architecture): pin persistence placement and core purity`

- [x] 8.1 Extend the EE architecture tests to assert
  `Freeboard.Persistence` does not reference `Freeboard.Enterprise`, and
  that CLI/Agent still do not reference Enterprise.
- [x] 8.2 Add an architecture test asserting `Freeboard.Agent` gains no reference
  to `Freeboard.Persistence` or to MySqlConnector/Dapper.
- [x] 8.3 Confirm the existing no-network Core structural test still passes (Core
  gained no DB dependency); assert `Freeboard.Core` does not reference the
  persistence project.

## 9. Docs and local dev

Commit: `docs(persistence): document mysql persistence, sync/migrate, and local dev`

- [x] 9.1 Add a docker-compose MySQL for local dev and integration tests, plus a
  documented one-liner to stand it up. Document both env vars distinctly:
  `FREEBOARD_DB` (runtime connection string for `gitops sync` and `system
  migrate`) and `FREEBOARD_TEST_DB` (integration-test DB discovery; tests skip
  when absent).
- [x] 9.2 Add a `docs/gitops.md` persistence section: schema overview (six
  tables), `system migrate` then `gitops sync` ordering, `gitops sync` vs dry-run
  `apply`, connection-string-as-secret (env/user-secrets/config provider only,
  never YAML), the general read endpoints (`/api/standards`, `/api/controls`,
  `/api/scopes`, `/api/compliance/status`), and a warning that `gitops sync`
  hard-removes resources dropped from config (narrowing config deletes rows). Add
  a README note.
- [x] 9.3 Run markdownlint on changed repo docs (`npx markdownlint-cli2
  "**/*.md"`) and fix issues.

## 10. Verification

Commit: covered by the commits above; this is the gate before finishing.

- [x] 10.1 `dotnet build` on the solution succeeds.
- [x] 10.2 `dotnet test` is green without MySQL (integration tests skip cleanly);
  and green with MySQL when the test connection string is set.
- [x] 10.3 Architecture and no-network structural tests pass.

## 11. Review findings

Commit: `fix(gitops): address mysql-persistence review findings`

- [x] 11.1 (IF-1) Extend the read-only state report so `GetState`/`Classify` ALSO
  surface forward-only integrity violations (checksum mismatch of an applied
  migration; a recorded applied version with no embedded stem) via an
  `IsCorrupt`/`IntegrityError` on `MigrationState`, by a pure comparison of
  already-read rows (no DDL, no writes). `gitops sync` fails with exit `3` and a
  clear stderr message on ANY such violation BEFORE importing, with or without
  `--migrate`. `ApplyPendingAsync` keeps its own fail-loud `Validate` check.
  Tests: real-MySQL `SyncMySqlIntegrationTests` (checksum mismatch and
  recorded-but-missing each exit `3`, import nothing) and CLI-level
  runner-double tests in `SyncAndMigrateCommandTests` (integrity-violated state
  exits `3` before importing, with and without `--migrate`).
- [x] 11.2 (IF-2) Reject duplicate ids within `Control.maps_to` and
  `Scope.controls` in `Freeboard.Core` `ConfigValidator` (ordinal equality, input
  error -> exit `1`), and defensively ordinal-`Distinct` the relation rows in
  `ImportPlan` so the importer never emits duplicate composite-PK join rows. Tests:
  Core `DuplicateMapsToIdFails`/`DuplicateScopeControlIdFails`; persistence
  `DuplicateRelationIdsCollapseToOneJoinRow`.
- [x] 11.3 (IF-3) Add the empty-DB end-to-end `sync` tests through the real
  `GitOpsCommands.Sync` path against real MySQL (skippable): `sync` without
  `--migrate` on an empty DB exits `3` and leaves zero tables; `sync --migrate` on
  an empty DB bootstraps, migrates, imports, exits `0` with six tables and data.
- [x] 11.4 (IF-4) Use `TryAddSingleton` for `PersistenceOptions` so a second
  `Add*` call cannot last-wins a different connection string.
- [x] 11.5 (IF-5) Assert the contract-stable user-facing strings verbatim: the
  `gitops sync` schema-out-of-date message (CLI test) and the RFC 7807 problem
  title/detail on the 503 read-endpoint responses (web test).
- [x] 11.6 (IF-6) Add `tests/Freeboard.TestInfrastructure` (net10.0 classlib,
  test-only, no `src/` reference) as the single home for test infrastructure and
  local-dev tooling. Move into it the shared `MySqlTestDatabase` fixture, the
  `docker-compose.yml`, and the `docker/mysql-init` grant script. Reference it from
  `Freeboard.Persistence.Tests` and `Freeboard.CLI.Tests`. Update `docs/gitops.md`
  and `README.md` to the new compose path. Add it to `Freeboard.slnx`.
