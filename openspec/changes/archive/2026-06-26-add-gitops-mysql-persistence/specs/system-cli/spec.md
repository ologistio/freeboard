## ADDED Requirements

### Requirement: system command group

The CLI SHALL provide a `system` command group for platform/operational
subcommands that are not gitops-specific. Schema migrations are a system concern
(they own the database schema, including the gitops-fed tables and the migration
tracking table), so the migrate command lives here, not under `gitops`. The
`system` group SHALL live in `Freeboard.CLI`, reference only `Freeboard.Core` and
the MIT persistence project (`Freeboard.Persistence`), and contain no reference to
`Freeboard.Enterprise`.

#### Scenario: system group has no enterprise reference

- **WHEN** the solution is built
- **THEN** `Freeboard.CLI` resolves the `system` command group without any
  dependency on `Freeboard.Enterprise`

### Requirement: system migrate connection string option and env var

`system migrate` SHALL accept a per-subcommand `--connection-string` option. The
connection string SHALL be sourced, in precedence order, from an explicit
`--connection-string` (highest) then the `FREEBOARD_DB` environment variable. An
explicit `--connection-string` SHALL override `FREEBOARD_DB`. If neither is
supplied, the command SHALL fail with a clear message and exit `3`. The connection
string SHALL NEVER be read from the YAML config, and SHALL NOT be committed to the
repository (it is a secret supplied via environment, user-secrets, or a config
provider).

#### Scenario: Explicit option overrides the env var

- **WHEN** both `--connection-string` and `FREEBOARD_DB` are set for
  `system migrate`
- **THEN** the explicit `--connection-string` value is used

#### Scenario: Missing connection string exits 3

- **WHEN** `system migrate` runs with neither `--connection-string` nor
  `FREEBOARD_DB` set
- **THEN** the command prints a clear message and exits `3`

### Requirement: system migrate command

The CLI SHALL provide `freeboard system migrate` that applies pending schema
migrations through `IMigrationRunner`. The connection string SHALL be supplied
out-of-band (a command option or environment variable), never from the YAML
config. On success it SHALL exit `0`. On an operational failure (missing
connection string, unreachable DB, migration checksum mismatch, an applied
migration missing from the embedded migrations, or migration execution failure) it
SHALL print a clear stderr message and exit `3`.

#### Scenario: Migrate applies pending migrations

- **WHEN** the user runs `freeboard system migrate` against a reachable store with
  pending migrations
- **THEN** the command applies the pending migrations and exits `0`

#### Scenario: Migrate with no connection string exits 3

- **WHEN** the user runs `freeboard system migrate` with neither
  `--connection-string` nor `FREEBOARD_DB` set
- **THEN** the command prints a clear message and exits `3`

#### Scenario: Migrate against an unreachable store exits 3

- **WHEN** the user runs `freeboard system migrate` and the store is unreachable
- **THEN** the command prints a clear message and exits `3`

#### Scenario: Migrate fails on a checksum mismatch

- **WHEN** the user runs `freeboard system migrate` and a recorded migration's
  checksum no longer matches its checked-in file
- **THEN** the command prints a clear message, applies no further migrations, and
  exits `3`

#### Scenario: Migrate fails on an applied migration missing from embedded migrations

- **WHEN** the user runs `freeboard system migrate` and `schema_migrations`
  records an applied version whose SQL file was deleted or renamed
- **THEN** the command prints a clear message, applies no migrations, and exits `3`

#### Scenario: Migrate fails when a migration's SQL errors during execution

- **WHEN** the user runs `freeboard system migrate` and a pending migration's SQL
  fails during execution (for example a malformed or erroring statement)
- **THEN** the command prints a clear stderr message and exits `3`, and the failed
  migration's version is NOT recorded in `schema_migrations` (record-only-after-
  success), so the migration is re-attemptable on a later run
