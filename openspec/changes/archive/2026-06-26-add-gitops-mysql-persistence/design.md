## Context

This design is a synthesis of two independent plans for the same task:

- Plan A (the Planner's original OpenSpec artifacts in this directory) chose
  EF Core 10 + Pomelo.EntityFrameworkCore.MySql, code-first entities, EF
  migrations, a single `IGitOpsStore.SyncAsync` interface, and exposed the write
  path through a new `gitops sync` CLI command.
- Plan B (Codex's independent plan) chose Dapper + MySqlConnector + hand-written
  SQL migrations, a split read/import/migrator interface surface, binary
  collation on ids, a schema-migrations tracking table, and `sync`/`migrate`
  commands with an explicit migrate-first gate.

A later mediator reframe (D9) settled the naming/structure: the persisted data is
the GENERAL compliance domain, the store and its read surface are general-named,
the gitops importer and `sync` stay gitops-named (gitops is one writer), and
migrations are a system concern. This Context predates that reframe; the decisions
below use the reframed names (`Freeboard.Persistence`, `IComplianceStore`,
`IGitOpsImporter`, `IMigrationRunner`, unprefixed tables, `schema_migrations`).

Where they agreed (a new MIT persistence project referencing only Core; web
reads through an injected interface; CLI gains `sync` and `migrate`; apply stays
dry-run; hard-remove-in-transaction now with soft-delete deferred; skip-when-
absent integration tests; docker-compose + docs) this design keeps the shared
shape. Where they diverged, the resolution and its rationale are recorded below.

The archived add-gitops-config-management change shipped the GitOps baseline:
`Freeboard.Core` holds the domain records (`Standard`, `Control`, `Scope`,
aggregated as `GitOpsConfig`), the YAML loader, and the validator (all
never-throw, never-print, returning `Diagnostic` data). The CLI has
`gitops validate` and `gitops apply --dry-run`. The web app has a read-only mode
flag, a `409 + RFC 7807` middleware, and `GET /api/gitops/status`. Persistence
was the explicit "increment 2" non-goal there.

FleetDM's model is the reference: hand-written SQL against MySQL, schema
migrations run by goose, a `*_store.go` datastore interface with a MySQL
implementation, and a `fleetctl gitops` command. We follow the shape (MySQL, a
store interface, a gitops load command) and, after the decision below, also
follow Fleet's hand-written-SQL + explicit-migrations ergonomics.

Repo constraints that bound this design:

- `Freeboard.Core` references nothing and a structural test
  (tests/Freeboard.CLI.Tests/NoNetworkStructuralTests.cs) asserts the Core
  assembly references no `System.Net.Http` or `System.Net.Sockets` types. Any
  MySQL client (MySqlConnector) references sockets, so the DB dependency CANNOT
  live in Core.
- EE one-way rule: Core, CLI, Agent must never reference Enterprise. CLI and
  Agent must stay cross-platform (win/linux/macos). Persistence is MIT, not EE.
- Identity is the immutable `id`. Schema and upsert/match logic key on `id`,
  never `title`. Cross-refs (`Control.maps_to`, `Scope.controls`) are relations.
- Conventional Commits, ASCII punctuation, markdownlint, code-as-liability.

## Goals / Non-Goals

**Goals:**

- A MySQL-backed store for `Standard`, `Control`, `Scope` keyed on `id`, with
  the `maps_to` and `controls` cross-refs persisted as relations.
- Versioned, forward-only schema migrations applied deterministically.
- A general read store and import/migration abstractions with a MySQL
  implementation; the web app reads through `IComplianceStore`; the CLI
  `gitops sync` command writes through `IGitOpsImporter`; the CLI `system migrate`
  command runs migrations through `IMigrationRunner`.
- The DB dependency confined to a new MIT project so Core stays network-free and
  the structural test keeps passing.
- The web read path serving persisted standards/controls/scopes as JSON.
- A defensible answer to "how does data get into the store while apply stays
  dry-run": the `gitops sync` load command.
- A local-MySQL dev/test story where integration tests run against real MySQL
  but skip cleanly when none is reachable, so CI has no hard MySQL dependency.

**Non-Goals:**

- Real reconciling apply, authenticated apply endpoint, write guard on read-only
  mode, soft-delete on removal, drift/reconcile, bidirectional export, new
  domain kinds, UI pages, multi-tenant, history/audit tables, non-MySQL
  providers, and a required-MySQL CI gate. See proposal Non-goals.

## Decisions

### D1. Data access: Dapper + MySqlConnector + hand-written SQL (chosen)

Source: Plan B's architecture. Plan A chose EF Core + Pomelo; this synthesis
overrides Plan A on the merits, not by deferral.

Chosen: Dapper over MySqlConnector, with hand-written SQL for reads and the
import upsert, and hand-written SQL migrations applied by a small ordered runner
(see D4). No ORM, no EF migration tooling.

Deciding facts (verified against NuGet on 2026-06-26):

- This repo targets net10.0 and tracks EF Core 10 (Microsoft.EntityFrameworkCore
  is at 10.0.x stable).
- `Pomelo.EntityFrameworkCore.MySql` has no net10/EF Core 10 release. Its latest
  published version is `9.0.0` (aligned to EF Core 9); there is no `10.x`
  version, not even a preview. Choosing EF Core + Pomelo therefore forces one of:
  (a) pin the whole persistence project to EF Core 9, moving it off the repo's
  net10/EF10 line; or (b) run Pomelo against EF Core 10 unaligned, relying on an
  unreleased or community-patched provider. Both are real maturity/version-
  alignment risks on net10.0, and the task instructed treating unverified Pomelo
  net10 maturity as a risk. Verification confirms the risk is not merely
  unverified: the aligned provider does not exist yet.
- `MySqlConnector` (the managed ADO.NET client both Dapper and Pomelo sit on) is
  at `2.6.x` stable, fully managed (no native dependency, so CLI and Agent stay
  cross-platform), and is unaffected by the EF/Pomelo version gap. Dapper sits
  directly on it with no provider-alignment dependency on EF Core's major
  version.

Other reasons Dapper wins here:

- Fleet fidelity (the user's explicit framing): Fleet uses explicit SQL +
  explicit migrations, not an ORM-managed shape. Dapper keeps SQL reviewable and
  the schema shape pinned by hand, matching Fleet directly.
- code-as-liability: the schema is tiny (3 entities + 2 join tables). The SQL is
  a handful of small statements; the upsert is one `INSERT ... ON DUPLICATE KEY
  UPDATE`. Dapper adds one thin dependency and no generated code, no migration
  scaffolding churn, and no provider/version coupling to maintain. EF would add
  Microsoft.EntityFrameworkCore + Pomelo + EF design tooling and generated
  migration files for a schema this small.
- Testability without live MySQL: the mapping logic (`GitOpsConfig` -> SQL
  parameters and read-model assembly) is plain code testable without a database.
  We avoid the EF-in-memory-for-relational-tests trap (EF's in-memory provider
  does not honour relational constraints, FKs, or the binary collation that is
  central to this schema's correctness, so it would give false confidence).
  Real behaviour is covered by integration tests against MySQL, skipped when
  absent.

Runner-up: EF Core 10 + Pomelo.EntityFrameworkCore.MySql (Plan A's primary).
Recorded and rejected for this increment. Its appeal is least-hand-written-code
for the model and a built-in migration runner. It is rejected primarily because
no net10/EF Core 10 Pomelo provider is published (latest 9.0.0), which is a
concrete version-alignment and maturity risk on this repo's target, and
secondarily because it adds more dependency surface and generated-migration
churn than a 3+2-table schema warrants and because its in-memory provider tempts
weaker relational tests. If the schema later grows query complexity that fights
hand-written SQL, EF (with an aligned Pomelo or the official MySQL provider once
available) can be reconsidered without changing the read/import contracts; Dapper
queries can also be added without an ORM if needed.

Also considered and rejected: SQLite/Postgres providers (out of scope, MySQL is
the directive); a hand-rolled raw ADO.NET layer without Dapper (more boilerplate
mapping for no benefit over Dapper's micro-mapping); DbUp/FluentMigrator as the
migration runner (a second dependency to run a single startup SQL file - see D4).

### D2. Placement: a new MIT project `Freeboard.Persistence`

Source: both plans agreed. The persistence code and the MySqlConnector/Dapper
dependency live in a new project, `src/Freeboard.Persistence` (MIT),
referencing only `Freeboard.Core`. It is ONE project (not split into three); the
three roles are separated by internal namespaces: the general store/reader in
`Freeboard.Persistence`, the gitops importer in `Freeboard.Persistence.GitOps`,
and the system migrations in `Freeboard.Persistence.System`. See D9 for why the
project and store are general-named while the importer and `sync` stay
gitops-named.

Why not Core: the structural no-network test forbids socket types in the Core
assembly; MySqlConnector pulls `System.Net.Sockets`. Putting persistence in Core
would break that test and pollute the pure domain/loader/validator with an I/O
dependency.

Why not the web app: the CLI `sync`/`migrate` commands also need the store. If
the store lived in the web app, the CLI would have to reference an ASP.NET Core
web project (wrong direction, heavy, pulls web/hosting deps into a console tool).
A shared library is the correct seam: both web and CLI reference it.

Why not Enterprise: persistence is community plumbing the MIT edition needs to be
useful, not a paid feature. The EE carve-out is for paid features only. Placing
it in Enterprise would relicense community code and would force CLI to reference
Enterprise (forbidden). So it is MIT.

Resulting reference graph (additions only):

- `Freeboard.Persistence` -> `Freeboard.Core`.
- `Freeboard.CLI` -> `Freeboard.Core`, `Freeboard.Persistence`.
- `Freeboard` (web) -> `Freeboard.Core`, `Freeboard.Enterprise`,
  `Freeboard.Persistence`.
- `Freeboard.Core`, `Freeboard.Agent`, `Freeboard.Enterprise`: unchanged. Core
  stays network-free; Agent stays EE-free and dependency-light; neither
  references persistence.

The `CLI -> Persistence` edge is a new but allowed graph change: Persistence is
MIT and references only Core, so the EE rule (CLI must never reference
Enterprise) still holds. The architecture tests are extended to assert: the new
project does not reference Enterprise; CLI and Agent still do not reference
Enterprise; the Agent gains no persistence or MySQL reference; and the no-network
Core structural test still passes (Core gains no DB dependency). The new project
is added to `Freeboard.slnx`.

Test infrastructure and local-dev tooling live in ONE test-only project,
`tests/Freeboard.TestInfrastructure` (net10.0 classlib): the shared MySQL test
fixture (`MySqlTestDatabase`, which resolves `FREEBOARD_TEST_DB` and provisions a
throwaway DB per test), the `docker-compose.yml`, and the mysql-init grant script.
It references only `Freeboard.Persistence`; no `src/` project references it. The
test projects that touch real MySQL (`Freeboard.Persistence.Tests` and the
`Freeboard.CLI.Tests` real-MySQL `sync` tests) reference it for the shared fixture.
The compose file and init SQL are carried in the project directory and located by
a known relative path (`tests/Freeboard.TestInfrastructure/`), not compiled.

### D3. Schema and table design (keyed on `id`, cross-refs as relations, binary collation)

Source: schema shape from both plans; binary collation and the
api_version/created_at/updated_at columns from Plan B. Plan B's `gitops_` table
prefix is dropped in the D9 reframe: the tables are the general compliance domain.

Six tables total: three entity tables (`standards`, `controls`,
`scopes`), two relation tables (`control_standards`,
`scope_controls`), and one migration-tracking table
(`schema_migrations`). Domain ids are the natural keys. `title` is a plain
mutable column, never part of a key or match. The tables are general
compliance-domain tables with no `gitops_` prefix (see D9): GitOps is one writer
into them, not their owner.

- `standards`
  - `id` VARCHAR(190) PRIMARY KEY, charset utf8mb4, collation utf8mb4_bin
  - `api_version` VARCHAR(64) NOT NULL
  - `title` VARCHAR(512) NOT NULL
  - `created_at` DATETIME(6) NOT NULL, `updated_at` DATETIME(6) NOT NULL
- `controls` - same columns as standards
- `scopes` - same columns as standards
- `control_standards` (Control.maps_to relation)
  - `control_id` VARCHAR(190) collation utf8mb4_bin NOT NULL,
    FK -> `controls(id)` ON DELETE CASCADE
  - `standard_id` VARCHAR(190) collation utf8mb4_bin NOT NULL,
    FK -> `standards(id)` ON DELETE CASCADE
  - PRIMARY KEY (`control_id`, `standard_id`)
- `scope_controls` (Scope.controls relation)
  - `scope_id` VARCHAR(190) collation utf8mb4_bin NOT NULL,
    FK -> `scopes(id)` ON DELETE CASCADE
  - `control_id` VARCHAR(190) collation utf8mb4_bin NOT NULL,
    FK -> `controls(id)` ON DELETE CASCADE
  - PRIMARY KEY (`scope_id`, `control_id`)
- `schema_migrations` (migration tracking - see D4)
  - `version` VARCHAR(190) PRIMARY KEY
  - `checksum` CHAR(64) NOT NULL
  - `applied_at` DATETIME(6) NOT NULL

Notes:

- BINARY collation (utf8mb4_bin) is pinned on every `id` and FK column. This is a
  correctness requirement, not a preference: Core validation treats ids with
  ordinal (case-sensitive, exact-byte) identity semantics. MySQL's default
  case- and accent-insensitive collation (utf8mb4_0900_ai_ci) would collapse
  distinct ids - e.g. `ctrl-a` and `CTRL-A` would collide on the primary key and
  on FK matches - corrupting the identity model. utf8mb4_bin makes the database's
  identity rules match Core's. (Plan A had specified the default ai_ci collation;
  this synthesis adopts Plan B's binary collation as the correct choice.)
- VARCHAR(190) keeps index width safe under utf8mb4 (4 bytes/char) and the
  InnoDB index-prefix limit, and is ample for ids. `Freeboard.Core`'s
  `ConfigValidator` does NOT bound id length (it checks required fields,
  apiVersion, uniqueness per kind, and reference resolution only). Ids longer
  than 190 characters are therefore out of scope this increment: such a config
  would fail to insert at the VARCHAR(190) bound rather than being rejected
  upstream. Adding an upstream id-length check is deferred; if it lands, the
  bound SHOULD be <=190 to match this column.
- Cross-refs are relations, not columns, so referential integrity is enforced by
  the database and `maps_to`/`controls` are loaded as joins. Explicit join tables
  pin the FK/PK and cascade rules.
- Match/identity is `id` everywhere. The import upsert finds existing rows by
  `id` (`INSERT ... ON DUPLICATE KEY UPDATE title=..., api_version=...,
  updated_at=...`) and never matches on `title`. The validator (in Core, upstream)
  already guarantees ids are unique per kind before an import writes.
- `api_version` is persisted per row so the stored shape carries the declared
  schema version of each resource. It is set on insert and updated on every
  upsert (a resource whose declared `api_version` changes between syncs has the
  stored value advanced). `created_at` is set on first insert and is immutable
  thereafter; `updated_at` is set on every upsert.

### D4. Migration strategy: hand-written SQL + a small ordered runner

Source: Plan B (hand-written SQL migrations + `schema_migrations` table).
Plan A used EF migrations; dropped with EF in D1.

Migrations are hand-written SQL files under `Migrations/`, named with a
fixed-width zero-padded ordinal and a slug in the form `NNN_slug.sql`, e.g.
`Migrations/001_initial_schema.sql`. The fixed-width ordinal makes a lexical sort
of the filenames equal to the numeric ordinal sort across `001`, `002`, `010`,
etc. The first migration creates the three entity tables and the two relation
tables (with binary collation, FKs, indexes). The `version` recorded for each
migration is the migration file name WITHOUT its extension - the `NNN_slug` stem
(e.g. file `001_initial_schema.sql` -> version `001_initial_schema`), never the
name with the `.sql` extension. Ordering is by parsed numeric ordinal.

The `schema_migrations` table is NOT created by a versioned migration. It
is a bootstrap object the apply path needs before it can record applied versions.
The bootstrap lives ONLY in the apply path, never in the read/state path:

- Bootstrap: `CREATE TABLE IF NOT EXISTS schema_migrations (...)` runs
  unconditionally as the FIRST step of `ApplyPendingAsync`, before the apply path
  reads which versions are recorded. This is idempotent DDL and separate from the
  versioned domain migrations. Only `system migrate` and `sync --migrate` reach
  this step, so only an explicit apply ever creates the table.
- The state report (`GetState`) is strictly read-only: it performs NO DDL and NO
  writes. It does NOT bootstrap the table. On a fresh database where
  `schema_migrations` does not exist, `GetState` treats every embedded migration
  as pending (none current) WITHOUT creating the table, and reports no
  recorded-but-missing-migration violation because there are zero recorded
  versions. If `schema_migrations` exists, `GetState` reads it and classifies
  current vs pending as normal. This keeps the migrate-first gate side-effect-free:
  `sync` without `--migrate` calls `GetState` and creates nothing.
- The state report ALSO surfaces forward-only integrity violations by pure reads,
  so the migrate-first gate cannot be bypassed by classifying a corrupt schema as
  "current". `GetState` compares the recorded `(version, checksum)` rows against
  the embedded set (the same comparison `ApplyPendingAsync` runs before applying)
  and reports, in the returned `MigrationState`, an integrity error when (a) an
  applied migration's checksum no longer matches its embedded file, or (b) a
  recorded applied `version` has no embedded stem (deleted or renamed). This is
  still strictly read-only: it is a comparison of already-read rows, no DDL and no
  writes. `sync` fails with exit `3` and a clear stderr message on ANY such
  violation BEFORE importing, with or without `--migrate` (an integrity-violated
  schema must never be imported into). `ApplyPendingAsync` keeps its own fail-loud
  check (it still refuses to apply over a corrupt or missing-migration schema).

A small hand-rolled runner (not a third-party migration library) then applies the
versioned migrations in `ApplyPendingAsync`:

- Bootstrap `schema_migrations` (the apply-path step above), then read applied
  state from it.
- Enumerate `Migrations/*.sql` embedded resources, sorted by parsed numeric
  ordinal (equivalently lexical, given fixed-width padding).
- For each file, compute a SHA-256 checksum of its contents.
- Read `schema_migrations`. Before applying any pending migration, check
  for applied versions that no longer exist among the embedded files: if any
  `version` recorded in `schema_migrations` is NOT present among the
  embedded migration stems (an applied migration whose `.sql` file was deleted or
  renamed), fail loudly and run nothing. This preserves forward-only integrity:
  with the stem as the version key, a slug rename would otherwise silently leave
  the old applied row orphaned and re-present the renamed file as a new pending
  migration, bypassing forward-only protection. This is distinct from the
  checksum-mismatch case (a present-but-edited migration); here the file is
  missing entirely.
- For each embedded migration, match its stem against `schema_migrations`.
  If a `version` row exists:
  - if its `checksum` matches, skip (already applied);
  - if its `checksum` differs, fail loudly (a checked-in migration was edited
    after being applied - a forward-only violation), and do not run further.
- If the `version` is absent, execute the file's statements, then insert the
  `(version, checksum, applied_at)` row. Forward-only; no down migrations.

Failure and atomicity semantics (MySQL-specific): DDL statements cause implicit
commits on MySQL, so a multi-statement DDL migration that fails partway CANNOT be
rolled back as one unit. The runner does NOT claim transactional atomicity for
DDL migrations. The real guarantee is: a migration's statements run, and only
after they all succeed is the `(version, checksum, applied_at)` row recorded. On
partial failure the runner fails loudly and does NOT record the version, so the
migration is re-attemptable on the next run. Migrations SHALL be authored to be
safe to re-run where practical (e.g. `CREATE TABLE IF NOT EXISTS`,
`ADD COLUMN IF NOT EXISTS`). Transactions are used only where they actually help
on MySQL: the `(version, checksum, applied_at)` version-record insert and any DML
a migration performs (DML is transactional; DDL is not).

Why a hand-rolled runner over DbUp/FluentMigrator: the runner is roughly one
small file (enumerate, checksum, compare, execute, record) over Dapper, with no
new dependency. DbUp would add a dependency to do the same for a single migration
file. code-as-liability favours the small reviewable runner here. If migrations
grow numerous or need branching/rollback, DbUp can be adopted later behind the
same `IMigrationRunner` contract.

Application is explicit, never implicit at web-app startup:

- The CLI `system migrate` subcommand runs the runner.
- The web app and `sync` assume the schema is current; they do not auto-migrate.
  `sync` checks whether migrations are current and fails with a clear message
  unless `--migrate` is supplied, in which case it runs pending migrations first
  (see D5). The web app never auto-migrates and never auto-syncs.

### D5. How data enters the store while `apply` stays dry-run

Source: both plans converged on `gitops sync` as the in-scope write path; Plan B
added the migrate-first gate and the `--migrate` opt-in. This is the crux of the
in-scope/out-of-scope boundary.

`apply` is and stays dry-run: it validates and prints, writes nothing. The store
still needs a write path, so the import path is built now and exposed through a
separate, explicitly-named command:

- `IGitOpsImporter.ImportAsync(GitOpsConfig)` is the write internal. Its
  precondition is that the config is already validated; the importer does NOT
  re-run Core validation. The caller (the `sync` command) guarantees validation
  before calling it (see F-8 resolution below). It runs inside one DML
  transaction and replaces the whole persisted set in a fixed, FK-safe order:

  1. Upsert all domain rows (standards, then controls, then scopes) by `id`
     (`INSERT ... ON DUPLICATE KEY UPDATE title, api_version, updated_at`; set
     `created_at` on insert only).
  2. Replace all join rows for the imported set: delete the existing cross-ref
     rows and insert the ones derived from the new config (whole-set replacement,
     not a per-parent diff).
  3. Delete domain rows whose `id` is absent from the config, in FK-safe order:
     scopes first, then controls, then standards.

  Whole-set cross-ref replacement (not "changed parents") matches the contract
  "import replaces the persisted set". FK-safe ordering relies on a Core
  invariant: a validated config has no dangling cross-refs (every `maps_to` and
  `controls` target resolves to a resource in the same config), so after the
  upsert+join-replace steps no surviving join row can reference a domain row that
  step 3 deletes. FK constraints therefore hold at commit. Removal is hard for
  now; soft-delete is the later change.

  Validation responsibility (F-8): the importer accepts an already-validated
  config as a documented precondition; the caller validates. We do not add a
  `ValidatedGitOpsConfig` wrapper type - that would balloon Core's surface for a
  guarantee a one-line precondition and a sync-level test already cover. The
  "invalid config writes nothing" guarantee is asserted at the sync/CLI level
  (sync validates, and on any error never calls `ImportAsync`).
- The CLI `gitops sync <path>` command loads + validates via Core, then, only on
  a clean validation, checks migration state (via the read-only
  `IMigrationRunner.GetState`) and calls `ImportAsync`:
  - If migrations are not current and `--migrate` was not supplied, it fails with
    a clear message ("schema out of date; run `system migrate` or pass
    `--migrate`") and writes nothing. "Writes nothing" here is literal on ANY
    schema state, including a fresh/empty DB: because `GetState` is read-only and
    the bootstrap lives in the apply path, this gate creates no tables (not even
    `schema_migrations`), writes no rows, and exits `3`. It does not merely
    "import no domain rows" - it performs no DDL at all.
  - If `--migrate` was supplied, it applies pending migrations first (which
    bootstraps `schema_migrations` and runs pending migrations via
    `ApplyPendingAsync`), then imports.
- `sync` is deliberately distinct from `apply`:
  - `apply` is the future reconciling, guarded, possibly-authenticated path. It
    stays dry-run in this change. We do not overload it.
  - `sync` is an explicit load with no read-only-mode guard and no auth. It is
    documented as the increment-2 loading mechanism, to be subsumed by real
    `apply` later.

Defensibility: the deliverable (store + read path + schema/migrations) is built
and exercised end to end. The write path exists and is tested, but it is gated
behind a clearly separate command, not behind the `apply` contract the task told
us to leave as dry-run. When real apply lands, `apply` gains the
guard/auth/soft-delete and can reuse the importer's internals (or a soft-delete
variant); `sync` can then be removed or kept as an admin escape hatch.

### D6. Interface surface: general reader / gitops importer / system migration runner

Source: divergence resolved toward Plan B's split, then renamed by the D9 reframe.
Plan A had a single `IGitOpsStore.SyncAsync`; Plan B split into reader / importer /
migrator. The reframe makes the reader general, keeps the importer gitops-named,
and makes the migrator a system concern.

Chosen: three small interfaces in the persistence project, each with a MySQL
implementation, in role-separated namespaces:

- `IComplianceStore` (namespace `Freeboard.Persistence`) - the general read
  abstraction: read methods returning persisted standards, controls (with
  resolved `maps_to`), and scopes (with resolved `controls`), plus per-kind
  counts for the status summary.
- `IGitOpsImporter` (namespace `Freeboard.Persistence.GitOps`) -
  `ImportAsync(GitOpsConfig)`, the transactional upsert + remove described in D5.
  Stays gitops-named because it imports git-sourced config; it is one writer into
  the general store.
- `IMigrationRunner` (namespace `Freeboard.Persistence.System`) - both apply
  pending migrations AND report current/pending state, because `sync`'s
  migrate-first gate depends on the state report. Migrations are a system/platform
  concern, not a gitops artifact. Method shape:
  - `MigrationState GetState()` (or `GetStateAsync`) - returns the current and
    pending migration versions, plus an integrity-violation flag/message. It is
    strictly read-only: it performs NO DDL and NO writes (it does NOT bootstrap
    `schema_migrations`). Current = every embedded migration version present in
    `schema_migrations`; pending = the embedded versions not yet recorded. The
    returned `MigrationState` ALSO carries an `IsCorrupt`/`IntegrityError` set when
    a pure comparison of recorded `(version, checksum)` against the embedded set
    finds a checksum mismatch of an applied migration or a recorded applied
    version with no embedded stem - the same rules `ApplyPendingAsync` enforces,
    but reported (not thrown) from the read path. This is what `sync` reads to
    decide whether the schema is current AND whether it is safe to import: `sync`
    exits `3` on any integrity violation before importing, with or without
    `--migrate`.
  - `ApplyPendingAsync()` - applies the pending migrations (the runner in D4) and
    returns the versions applied. It owns the bootstrap: it creates
    `schema_migrations` (idempotent `CREATE TABLE IF NOT EXISTS`) as its first
    step.

  The state report is safe on a fresh DB without mutating it: if
  `schema_migrations` does not exist, `GetState` reports every embedded migration
  as pending (none current) and no integrity violation (zero recorded versions),
  all without creating the table. The bootstrap lives in `ApplyPendingAsync`, so
  reading state never has a DDL side effect; the integrity comparison is a pure
  read of already-loaded rows and adds no side effect either.

Why split rather than one store interface: the web app depends only on the read
surface, matching the dry-run/read-only posture of this increment. The CLI
depends on `IGitOpsImporter` (for `gitops sync`) and `IMigrationRunner` (for
`system migrate`) but not on a combined write+read+migrate god-interface. This
keeps each consumer's dependency honest and is still minimal: three interfaces,
no speculative surface.

DI registration is split so the guarantee is structural, not just by discipline:

- `AddComplianceStore(connectionString)` registers the connection factory and
  `IComplianceStore` only. The web app calls this.
- `AddGitOpsImport(connectionString)` registers `IGitOpsImporter` and
  `AddSystemMigrations(connectionString)` registers `IMigrationRunner` (or one
  combined CLI-side `AddPersistenceCli(connectionString)` extension). The CLI
  calls these.

The web app therefore registers only the reader; its service provider does not
resolve `IGitOpsImporter` or `IMigrationRunner`. An architecture/DI test asserts
this (see D7 and the verification strategy).

### D7. Web read path

Source: both plans agreed on serving the persisted domain and a persisted-count
summary; the D9 reframe makes the routes general (the data is the general
compliance domain, not a gitops artifact) and moves the persisted counts onto a
general status route. The web app registers only `IComplianceStore` via
`AddComplianceStore(ConnectionStrings:Freeboard)` (Dapper/MySqlConnector behind
it). It does not register `IGitOpsImporter` or `IMigrationRunner`, so its service
provider cannot resolve them. New read-only endpoints (general routes, NOT under
`/api/gitops/`):

- `GET /api/standards` -> persisted standards (`id`, `title`).
- `GET /api/controls` -> persisted controls (`id`, `title`, `maps_to`).
- `GET /api/scopes` -> persisted scopes (`id`, `title`, `controls`).
- `GET /api/compliance/status` -> the persisted per-kind count summary.

Where the persisted counts live (decision): the counts move onto a new general
`GET /api/compliance/status`, NOT onto the existing `GET /api/gitops/status`.
Reasoning: the counts describe the general compliance store, so they belong on the
general read surface alongside `/api/standards` etc.; `/api/gitops/status` reports
a gitops concern (read-only mode + repository URL) and is left unchanged by this
change. This keeps each status endpoint cohesive with its domain. The
`/api/compliance/status` shape:

```json
{ "persisted": { "standards": 3, "controls": 12, "scopes": 2 } }
```

The `persisted` object is ALWAYS present. When the store is reachable, each
per-kind value is an integer count. When the store is unreachable, each per-kind
value is `null`, distinguishing "unknown" from "zero rows" (see the DB-unavailable
rule below). `GET /api/gitops/status` keeps its existing `gitOps` bool and
`repositoryUrl` (omitted when unset) and is not touched here.

Read ordering is deterministic: each endpoint returns resources ordered by `id`
(ordinal/binary order, consistent with the binary-collation identity semantics),
and each relation id array (`maps_to`, `controls`) is ordered by id. This makes
responses stable across reads regardless of physical row order.

DB-unavailable behavior: the web app does NOT auto-connect to MySQL at startup,
so an unreachable store does not crash the app. When the store is unreachable at
request time, the read endpoints surface a clear error (HTTP 503, an RFC 7807
problem body) rather than throwing an unhandled exception, and the
`/api/compliance/status` endpoint's `persisted` summary degrades to all-null
per-kind values instead of failing the whole status response. The `persisted`
object is still present with every per-kind key, each set to `null`:

```json
{ "persisted": { "standards": null, "controls": null, "scopes": null } }
```

`null` (not omitted, not `{}`, not `0`) marks the count as unknown rather than
zero. The app stays up; `/api/compliance/status` returns HTTP 200 with the
all-null `persisted` summary.

These are GET-only, so the existing read-only middleware does not touch them.
They serve persisted state, not the YAML on disk: the web app reads the store,
matching Fleet's "UI reads the datastore" model. The web app depends only on
`IComplianceStore`; it has no importer or migration-runner dependency and never
auto-migrates or auto-syncs.

Test path without MySQL: all web tests inject an `IComplianceStore` test double so
`dotnet test` stays green without a database. Coverage boundary (see
Verification): web/double tests assert endpoint serialization shape only (ids,
titles, cross-ref arrays, ordering, status JSON). Cross-ref
(`maps_to`/`controls`) resolution correctness from the SQL joins is asserted only
by the MySQL integration tests (skipped when no MySQL is reachable). The
no-MySQL run does not over-claim join correctness.

### D8. Connection string and config (secrets out of band)

Source: both plans; Plan B stated the secrets rule explicitly. The D9 reframe
renames the env vars: the connection is now a general/system concern (the same DB
backs the general store, the gitops importer, and the system migrations), so the
runtime var is `FREEBOARD_DB`, not a gitops-scoped name. Connection string from
standard config: `ConnectionStrings:Freeboard` for the web app, and a
`--connection-string` option or the `FREEBOARD_DB` env var for the CLI
`gitops sync` and `system migrate`.

`--connection-string` is a per-subcommand option present on both `gitops sync` and
`system migrate`. Precedence: an explicit `--connection-string` overrides the
`FREEBOARD_DB` env var. If neither is supplied, the command fails with a
clear message (exit code 3, per the gitops-cli / system-cli specs).

Two distinct env vars exist and must not be confused:

- `FREEBOARD_DB` - the runtime connection string for `gitops sync` and
  `system migrate`.
- `FREEBOARD_TEST_DB` - the integration-test DB discovery string. Tests
  read it to find a MySQL to run against and skip when it is absent. It is never
  the runtime source.

Connection strings are secrets. They SHALL be supplied via environment variables,
.NET user-secrets, or a config provider only - never in the GitOps YAML config
(its schema has no secret fields) and never in committed config files. This is
consistent with the existing secrets-not-in-git rule. Docs state this explicitly.

### D9. Capability framing: general store, gitops writer, system migrations

Source: a mediator-directed reframe of the original `gitops-persistence` naming.
The original artifacts named the capability, project, interfaces, tables, and
routes after `gitops`. The mediator's point: the persisted data (`Standard`,
`Control`, `Scope`) is the GENERAL compliance domain. GitOps is only the ingestion
mechanism - one writer into the store. Naming the whole persistence layer `gitops`
mis-sells the capability: it ties the durable compliance data layer to one of its
writers. Fleet is the reference here too - in Fleet, MySQL is the platform
datastore that many subsystems read and write; `fleetctl gitops` is one writer
into it, not the owner of the schema. Freeboard follows the same separation.

The reframe (Option C, hybrid) splits naming by concern:

1. The store is the general compliance data layer. The project is
   `Freeboard.Persistence` (one project, role-separated by namespace). The read
   abstraction is `IComplianceStore`. The tables are general domain tables with no
   `gitops_` prefix: `standards`, `controls`, `scopes`, `control_standards`,
   `scope_controls`. The web read routes are general: `/api/standards`,
   `/api/controls`, `/api/scopes`, with persisted counts on
   `/api/compliance/status`. The runtime connection env var is `FREEBOARD_DB`.
2. The importer and `sync` stay gitops. `IGitOpsImporter` (namespace
   `Freeboard.Persistence.GitOps`) imports git-sourced config; the CLI keeps
   `gitops sync <path>` under the existing `gitops` group. GitOps is one writer
   into the general store; its behavior (load+validate via Core, transactional
   upsert-by-id, hard-remove dropped ids, migrate-first gate + `--migrate`) is
   unchanged.
3. Migrations are a system concern. The runner interface is `IMigrationRunner`
   (namespace `Freeboard.Persistence.System`). The tracking table is
   `schema_migrations`. The CLI command moves out of `gitops` into a new `system`
   group: `system migrate`. The schema is owned by the platform, not by gitops;
   it backs the general store, the gitops-fed tables, and future writers alike.

The existing `GET /api/gitops/status` stays a gitops concern (it reports
read-only mode and repository URL) and is unchanged. Persisted counts do NOT go on
it; they go on the general `/api/compliance/status` (see D7), because they describe
the general store, not a gitops artifact.

This framing is durable: a future EE feature or a future non-gitops writer can use
`IComplianceStore` and the general tables without inheriting gitops naming, and the
system migration surface owns the schema independently of any one writer.

## Risks / Trade-offs

- Integration tests need a real MySQL; making them a hard CI gate would couple CI
  to a database. Mitigation: integration tests detect a reachable MySQL
  (env-provided connection string, `FREEBOARD_TEST_DB`) and skip with a
  clear message when absent, so `dotnet test` is green without MySQL. A
  docker-compose file and a documented one-liner stand MySQL up locally and in
  any CI job that opts in. Mapping/SQL-shape unit tests that do not need MySQL run
  unconditionally. We deliberately do not use an in-memory provider as a stand-in
  because it would not honour FKs or binary collation.
- New project + Dapper/MySqlConnector dependency is real added liability.
  Justified: the capability (durable, queryable compliance state read by the UI)
  cannot be delivered without a store; Dapper minimizes mapping code without an
  ORM; the dependency is confined to one MIT project away from Core/Agent.
- Hand-written SQL and a hand-rolled migration runner are code we own and test.
  Justified by the tiny schema, Fleet fidelity, and avoiding the Pomelo net10 gap
  and a second migration-tool dependency. Risk: the runner must be correct;
  mitigated by checksum verification, the bootstrap-then-apply ordering, the
  record-version-only-after-success rule, and integration tests that migrate a
  fresh empty schema. DDL migrations are NOT transactional on MySQL (DDL implicit
  commits), so the runner does not claim atomic DDL rollback; instead it records
  a migration only after all its statements succeed, fails loudly and leaves the
  version unrecorded on partial failure, and migrations are authored to be
  re-runnable (`CREATE TABLE IF NOT EXISTS` etc.). See D4.
- Hard removal in import deletes rows for ids dropped from config. Acceptable now
  because no operational resources or evidence hang off these rows yet and there
  is no apply contract promising soft-delete; FKs cascade cleanly. Risk: an
  accidental config narrowing deletes data. Forward principle: real apply must
  revisit soft-delete on removal. Import runs in a transaction so a failed load
  does not partially write.
- Binary collation and VARCHAR(190) are MySQL-8-specific choices; documented.
  MySQL is the only supported provider this increment.
- `apply` and `sync` both load+validate config, risking two look-alike code
  paths. Mitigation: both call the same Core load+validate; only the terminal
  step differs (print vs write). The existing CLI comments that wrongly imply
  apply could write are corrected so only `validate` and `apply --dry-run` claim
  to write nothing.

## Verification strategy

- `dotnet build` for the new project and the solution.
- Unit tests (no MySQL): `GitOpsConfig` -> SQL parameter mapping; import plan
  keys on `id` and updates `title` without matching on `title`; cross-ref rows
  derive from `maps_to`/`controls`; the migration runner's ordering (across
  `001`/`002`/`010` fixtures, asserting numeric-ordinal order) and checksum
  comparison logic; the migrator's state report (current/pending) given a set of
  recorded vs embedded versions, including "all pending when none recorded" and a
  loud failure when a recorded applied version has no matching embedded migration
  stem (a deleted or renamed applied migration), distinct from a checksum mismatch.
- Integration tests (real MySQL, skipped when none reachable):
  - Migrate a completely empty schema from scratch: against a database with no
    tables at all (no `schema_migrations`), run the migrator and assert it
    bootstraps the migrations table, applies `001`, and ends with all six tables
    present with binary collation, FKs, and indexes. This is the fresh-DB path
    that the bootstrap step (D4) exists to make possible.
  - Migrator state report is side-effect-free: against a completely empty DB (no
    `schema_migrations`), assert `GetState` reports all embedded migrations
    pending AND creates no tables (the database is still empty afterward); after
    applying, assert none pending.
  - Failed migration: a migration whose statements fail partway leaves its
    version unrecorded; a re-run re-attempts it (record-only-after-success).
  - Missing applied migration: a database recording an applied version with no
    matching embedded migration stem (deleted or renamed) makes the migrator fail
    loudly and apply no migrations.
  - `sync` a fixture; read back via `IComplianceStore` and assert counts and
    cross-refs; re-`sync` with a changed `title` and a dropped `id` and assert
    update-by-id and removal-by-id.
  - created_at immutability: re-sync an existing resource and assert `created_at`
    is unchanged while `updated_at` advances.
  - api_version on upsert: re-sync a resource whose declared `api_version`
    changed and assert the stored `api_version` is updated.
  - FK-safe drop: a sync that drops a standard still referenced by a control in
    the OLD db state, where that control is also updated/removed in the same
    config, succeeds without an FK violation (the upsert+join-replace then
    ordered delete sequence holds the Core no-dangling-cross-ref invariant).
  - assert case-distinct ids (`ctrl-a` vs `CTRL-A`) remain distinct rows (binary
    collation); assert an invalid config writes nothing (at the sync level);
    assert `sync` without `--migrate` against a completely EMPTY database exits
    `3` and leaves the database with NO tables (no `schema_migrations`, no domain
    tables) - the migrate-first gate performs no DDL; and the happy path where
    `sync --migrate` on an empty DB bootstraps, migrates, and imports successfully.
- Web tests (no MySQL): all web tests inject an `IComplianceStore` test double.
  `WebApplicationFactory` asserts the three general read endpoints
  (`/api/standards`, `/api/controls`, `/api/scopes`) and the
  `/api/compliance/status` `persisted` summary shape (supplied by the double); GET
  endpoints are not blocked by read-only mode. A DI test asserts the web app's
  service provider does NOT resolve `IGitOpsImporter` or `IMigrationRunner`.
  Coverage boundary: these double tests assert serialization shape and ordering
  only; cross-ref resolution correctness from SQL joins is asserted only by the
  MySQL integration tests above (skipped without MySQL).
- CLI tests (exit-code matrix, per the gitops-cli and system-cli specs):
  `gitops sync` on invalid config exits `1`, writes nothing, does not call the
  importer (importer double); `gitops sync` on valid config calls the importer and
  exits `0`; `apply` without `--dry-run` exits `2`; `apply --dry-run` exits `0`; a
  `gitops sync`/`system migrate` with no connection string exits `3`; a
  checksum-mismatch migration causes `system migrate` to exit `3` with a clear
  message; a recorded applied migration missing from the embedded migrations
  (deleted or renamed) causes `system migrate` to exit `3` with a clear message.
  One test per distinct exit code where practical.
- Architecture tests: new project does not reference Enterprise; CLI and Agent
  still do not; Agent gains no persistence/MySQL reference; Core structural
  no-network test still passes.
