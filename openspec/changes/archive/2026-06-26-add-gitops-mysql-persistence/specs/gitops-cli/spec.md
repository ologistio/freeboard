## ADDED Requirements

### Requirement: gitops command exit codes

The `gitops` subcommands SHALL use a consistent exit-code matrix:

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 1 | Validation or input error (matches the existing `validate` behavior). |
| 2 | `apply` invoked without `--dry-run` (existing `apply` behavior, unchanged). |
| 3 | Operational failure for `sync`: missing connection string, DB unreachable, schema not current without `--migrate`, a forward-only integrity violation detected by the read-only state report (checksum mismatch of an applied migration, or a recorded applied migration missing from the embedded migrations) - with or without `--migrate` - or a migration failure surfaced while applying with `--migrate` (checksum mismatch, an applied migration missing from the embedded migrations, or migration execution failure). |

The `sync` scenarios below and the test list pin these codes. A test SHALL cover
each distinct code where practical. The `migrate` command and its exit-code-3
behavior now live under the `system` command group (see the system-cli
capability), not under `gitops`.

#### Scenario: Operational failure of sync exits 3

- **WHEN** `sync` fails for an operational reason (missing connection string,
  unreachable DB, schema not current without `--migrate`, or a migration failure
  while applying with `--migrate`)
- **THEN** the command exits `3` with a clear stderr message

### Requirement: gitops sync connection string option and env var

`sync` SHALL accept a per-subcommand `--connection-string` option. The connection
string SHALL be sourced, in precedence order, from an explicit
`--connection-string` (highest) then the `FREEBOARD_DB` environment variable. An
explicit `--connection-string` SHALL override `FREEBOARD_DB`. If neither is
supplied, the command SHALL fail with a clear message and exit `3`. The connection
string SHALL NEVER be read from the YAML config.

#### Scenario: Explicit option overrides the env var

- **WHEN** both `--connection-string` and `FREEBOARD_DB` are set for `sync`
- **THEN** the explicit `--connection-string` value is used

#### Scenario: Missing connection string exits 3

- **WHEN** `sync` runs with neither `--connection-string` nor `FREEBOARD_DB` set
- **THEN** the command prints a clear message and exits `3`

### Requirement: gitops sync command

The CLI SHALL provide `freeboard gitops sync <path>` that loads and validates the
YAML config from `<path>` (a directory) using the `Freeboard.Core` loader and
validator, and on a clean validation writes the resulting config into the store
via `IGitOpsImporter`. GitOps `sync` is one writer into the general compliance
store. The connection string SHALL be supplied out-of-band (a command option or
environment variable), never from the YAML config. On any validation error `sync`
SHALL print the errors to stderr, write nothing to the store, and exit `1`. On
success it SHALL exit `0`.

`sync` SHALL fail with a clear message and write nothing if the schema migrations
are not current, unless `--migrate` is supplied, in which case it SHALL apply
pending migrations (via the system migration runner) before importing. The
migrate-first gate SHALL read migration state through the read-only state report;
when the schema is not current and `--migrate` is absent, "writes nothing" is
literal on ANY schema state, including a fresh or empty database: `sync` SHALL
create no tables (not even `schema_migrations`), write no rows, perform no DDL,
and exit `3`. Only the `--migrate` path applies migrations (which bootstraps
`schema_migrations`).

`sync` SHALL also read the integrity violations the state report surfaces and
SHALL fail with exit `3` and a clear stderr message, writing nothing, BEFORE
importing whenever the report flags a forward-only integrity violation (a checksum
mismatch of an applied migration, or a recorded applied migration missing from the
embedded migrations). This gate applies with OR without `--migrate`: an
integrity-violated schema SHALL never be imported into.

`sync` is the increment-2 mechanism for loading validated config into the store
while `apply` stays dry-run. It is distinct from `apply`: it has no read-only-mode
guard and no authentication, and it is documented as the loading path that real
apply will later subsume.

`sync` replaces the persisted set, which includes HARD removal of resources that
are no longer present in the config (this increment; soft-delete on removal lands
with real apply). Operators SHALL be aware that narrowing the config deletes the
corresponding rows from the store.

#### Scenario: Sync writes a valid config to the store

- **WHEN** the user runs `freeboard gitops sync <dir>` on a valid config with a
  reachable, migrated store
- **THEN** the command writes the standards, controls, and scopes to the store
  keyed on `id` and exits `0`

#### Scenario: Sync on invalid config writes nothing

- **WHEN** the user runs `freeboard gitops sync <dir>` on a config with
  validation errors
- **THEN** the command prints the errors to stderr, does not write to the store,
  and exits `1`

#### Scenario: Sync on an un-migrated schema fails without --migrate

- **WHEN** the user runs `freeboard gitops sync <dir>` against a store whose
  migrations are not current and does not pass `--migrate`
- **THEN** the command prints a clear message, writes nothing, and exits `3`

#### Scenario: Sync without --migrate against an empty database creates no tables

- **WHEN** the user runs `freeboard gitops sync <dir>` with a valid config against
  a completely EMPTY database (no tables) without `--migrate`
- **THEN** the command exits `3` and leaves the database with NO tables: no
  `schema_migrations` table and no domain tables are created, because the
  migrate-first gate reads state read-only and performs no DDL

#### Scenario: Sync --migrate against an empty database bootstraps, migrates, and imports

- **WHEN** the user runs `freeboard gitops sync <dir> --migrate` with a valid
  config against a completely EMPTY database
- **THEN** the command bootstraps `schema_migrations`, applies the pending
  migrations, imports the config, and exits `0`

#### Scenario: Sync removes resources dropped from config

- **WHEN** the user runs `freeboard gitops sync <dir>` on a config that no longer
  contains a resource the store previously held
- **THEN** the command hard-removes that resource's row from the store (set
  replacement), so narrowing the config deletes the corresponding rows

#### Scenario: Sync on an integrity-violated schema exits 3 before importing

- **WHEN** the user runs `freeboard gitops sync <dir>` against a store whose
  `schema_migrations` records an applied migration with a mismatched checksum, or
  a recorded applied migration missing from the embedded migrations (with or
  without `--migrate`)
- **THEN** the command prints a clear message, imports nothing, and exits `3`

#### Scenario: Sync against an unreachable store exits 3

- **WHEN** the user runs `freeboard gitops sync <dir>` and the store is
  unreachable
- **THEN** the command prints a clear message, writes nothing, and exits `3`

### Requirement: apply stays dry-run only

The `freeboard gitops apply` command SHALL remain dry-run only in this increment.
It SHALL NOT write to the store. Real, store-writing apply (with its
authentication, write guard, and soft-delete-on-removal semantics) is a later
change. Loading config into the store in this increment is done by `sync`, not by
`apply`.

#### Scenario: Apply still writes nothing

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run`
- **THEN** the command prints the planned state and exits `0` without writing to
  the store

#### Scenario: Apply without dry-run is still rejected

- **WHEN** the user runs `freeboard gitops apply <dir>` without `--dry-run`
- **THEN** the command exits `2` with a message that only `--dry-run` is
  supported and real apply lands in a later increment

## MODIFIED Requirements

### Requirement: CLI stays community and cross-platform

The `gitops` command group SHALL live in `Freeboard.CLI` and reference only
`Freeboard.Core` and the MIT persistence project (`Freeboard.Persistence`),
and SHALL contain no reference to `Freeboard.Enterprise`. The MySQL client pulled
in by the persistence project SHALL be a fully managed, cross-platform client, so
the CLI continues to run on Windows, Linux, and macOS without platform-specific
code.

#### Scenario: No enterprise reference

- **WHEN** the solution is built
- **THEN** `Freeboard.CLI` resolves without any dependency on
  `Freeboard.Enterprise`

#### Scenario: Cross-platform with the persistence dependency

- **WHEN** the CLI is built and run on Windows, Linux, or macOS
- **THEN** it runs without platform-specific code, the MySQL client being a fully
  managed cross-platform client

## ADDED Requirements

### Requirement: Command-group documentation reflects the write path

The `gitops` command-group documentation SHALL NOT claim that the group makes no
network calls or writes no state, because `sync` now connects to MySQL and writes.
Only `validate` and `apply --dry-run` SHALL be described as non-writing,
non-connecting commands.

#### Scenario: Group doc no longer claims no writes or no network

- **WHEN** the `gitops` command-group documentation is read
- **THEN** it does not claim the group writes no state or makes no network calls,
  and it scopes the non-writing description to `validate` and `apply --dry-run`
