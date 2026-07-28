# gitops-cli Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
### Requirement: gitops validate command

The CLI SHALL provide `freeboard gitops validate <path>` that loads the YAML
config from `<path>` (a directory) using the `Freeboard.Core` loader and
validator, prints any errors, and exits `0` when validation passes or `1` when
validation fails or the input is invalid (including a missing or nonexistent
path). Errors SHALL be written to stderr and the success summary to stdout. The
command MUST NOT modify any file or external state and MUST NOT make any network
call.

#### Scenario: Valid config passes

- **WHEN** the user runs `freeboard gitops validate <dir>` on a valid config
- **THEN** the command prints a success summary (counts of standards, controls,
  scopes) to stdout and exits with code `0`

#### Scenario: Invalid config fails

- **WHEN** the user runs `freeboard gitops validate <dir>` on a config with
  validation errors
- **THEN** the command prints each error on its own line to stderr and exits with
  code `1`

#### Scenario: Missing path

- **WHEN** the user runs `freeboard gitops validate <dir>` and `<dir>` does not
  exist
- **THEN** the command prints an error naming the path to stderr and exits `1`

#### Scenario: Validate makes no network call

- **WHEN** the gitops load/validate code path is inspected by a structural test
- **THEN** it references no HTTP or socket APIs (no `System.Net.Http` or
  `System.Net.Sockets` usage), so validate cannot make an outbound network
  connection

### Requirement: gitops apply dry-run command

The CLI SHALL provide `freeboard gitops apply <path> --dry-run` that performs the
same load and validation as `validate` and additionally prints the resulting
desired config state that would be applied to stdout. In this increment
`--dry-run` is required; invoking `apply` without `--dry-run` SHALL exit `2` with
a message to stderr that only dry-run is supported and that real apply lands in a
later increment, because there is no backing store yet. `apply --dry-run` exits
`0` on a valid config and `1` on a validation or input error, and SHALL make no
network call.

#### Scenario: Dry-run prints planned state

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on a valid
  config
- **THEN** the command prints the standards, controls, and scopes that would be
  applied to stdout and exits `0` without writing any state

#### Scenario: Apply without dry-run is rejected

- **WHEN** the user runs `freeboard gitops apply <dir>` without `--dry-run`
- **THEN** the command prints to stderr that only `--dry-run` is supported in
  this version and that real apply lands in a later increment, and exits `2`

#### Scenario: Dry-run on invalid config fails

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on an invalid
  config
- **THEN** the command prints the validation errors to stderr and exits `1`
  without printing planned state

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

### Requirement: EE one-way rule pinned by architecture test

An architecture test SHALL assert that `Freeboard.Core`, `Freeboard.CLI`, and
`Freeboard.Agent` carry no project or assembly reference to
`Freeboard.Enterprise`, so an accidental reference fails the build.

#### Scenario: Community components free of enterprise

- **WHEN** the architecture test runs
- **THEN** it passes only if none of `Freeboard.Core`, `Freeboard.CLI`, or
  `Freeboard.Agent` references `Freeboard.Enterprise`

### Requirement: Command-group documentation reflects the write path

The `gitops` command-group documentation SHALL NOT claim that the group makes no
network calls or writes no state, because `sync` now connects to MySQL and writes.
Only `validate` and `apply --dry-run` SHALL be described as non-writing,
non-connecting commands.

#### Scenario: Group doc no longer claims no writes or no network

- **WHEN** the `gitops` command-group documentation is read
- **THEN** it does not claim the group writes no state or makes no network calls,
  and it scopes the non-writing description to `validate` and `apply --dry-run`

### Requirement: gitops commands enforce referential integrity for every config kind

The `gitops validate` and `gitops sync` commands SHALL apply the loader and
validator's referential-integrity checks to every config kind, including the unified
Scope, Integration, and Collector. A config in which a
kind's target reference names an id that no document defines SHALL be rejected: `gitops
validate` SHALL print a diagnostic that names the offending kind, resource, and missing
id, and SHALL exit non-zero, and `gitops sync` SHALL NOT import such a config. A `Scope`
target (`standard`, `requirement`, or `control`) is a hard reference and a dangling target
fails the command; a `Scope.subject` is a scalar asset reference and a dangling subject is
a NON-BLOCKING warning that does NOT fail the command (see the warnings-on-success
behaviour), because a subject may be a retired or not-yet-discovered asset.

The commands SHALL likewise reject a Collector whose `provider` disagrees with the
`provider` of the Integration its `connection` names, and a Collector whose `config`
carries a key that the schema registered for its `(type, provider)` pair does not name or
omits a key that schema requires.

At the command surface the coverage is a representative dangling reference for
EACH kind: a Scope naming an unknown target (a standard, requirement, or control that no
document defines), a Collector naming an unknown control, a Collector of
`type: integration` naming an unknown connection, a Collector of `type: integration` whose
`provider` disagrees with its connection's, and a Collector carrying an unknown `config`
key. The exhaustive per-edge matrix (for example a Scope naming an unknown
requirement or control, a dangling Scope subject warning, an Integration naming an
unknown vendor, or the full per-`(type, provider)` config-schema matrix) is owned by the
`Freeboard.Core` ConfigValidator unit tests, so the CLI layer proves command wiring only
rather than re-running every edge.

#### Scenario: Validate rejects a dangling scope target reference

- **WHEN** the user runs `gitops validate` on a directory whose Scope names a `standard`,
  `requirement`, or `control` id that no document defines
- **THEN** the command prints a diagnostic naming the Scope and the unknown target id and
  exits non-zero

#### Scenario: Validate warns but does not fail on a dangling scope subject

- **WHEN** the user runs `gitops validate` on a directory whose Scope names a `subject` id
  that no asset defines, with every other reference resolving
- **THEN** the command prints a non-blocking warning naming the Scope and the unresolved
  subject and exits `0`

#### Scenario: Validate rejects a dangling collector control reference

- **WHEN** the user runs `gitops validate` on a directory whose Collector
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the Collector and the
  unknown control id and exits non-zero

#### Scenario: Validate rejects a dangling collector connection reference

- **WHEN** the user runs `gitops validate` on a directory whose Collector
  of `type: integration` names a `connection` id that no Integration
  document defines
- **THEN** the command prints a diagnostic naming the Collector and the
  unknown connection id and exits non-zero

#### Scenario: Validate rejects a collector provider that disagrees with its connection

- **WHEN** the user runs `gitops validate` on a directory whose Collector of
  `type: integration` names a `provider` other than the `provider` of the Integration its
  `connection` names
- **THEN** the command prints a diagnostic naming the Collector and both providers and
  exits non-zero

#### Scenario: Validate rejects an unknown collector config key

- **WHEN** the user runs `gitops validate` on a directory whose Collector authors a
  `config` key that its `(type, provider)` schema does not name
- **THEN** the command prints a diagnostic naming the Collector and the unknown config key
  and exits non-zero

#### Scenario: Validate rejects a retired collector kind

- **WHEN** the user runs `gitops validate` on a directory containing a
  `kind: EvidenceCollector` or `kind: AttestationTemplate` document
- **THEN** the command prints an unknown-kind diagnostic naming the document and exits
  non-zero, rather than silently ignoring the document

#### Scenario: Sync does not import a config with a dangling target reference

- **WHEN** the user runs `gitops sync` on a directory that fails referential
  integrity for any target reference
- **THEN** the command reports the validation error, exits non-zero, and writes
  no rows for that config

### Requirement: gitops sync round-trips and hard-removes the new config kinds

The `gitops sync` command SHALL persist the unified Scope, Vendor (as an `Asset` of
`type: Vendor`), Integration, and Collector resources from a
valid config and, on a later sync that drops a resource, SHALL hard-remove the persisted
row whose id is absent from the new config, in foreign-key-safe order, preserving the
existing hard-remove semantics. A resource kept across syncs SHALL remain persisted. The
unified scope set SHALL be replaced as a whole set (delete-all then insert) each sync, and
that replace SHALL precede the absent standard, requirement, and control deletes, so no
target `RESTRICT` foreign key blocks a removal; a scope's `subject` has no foreign key, so
removing a subject asset never blocks the sync (the scope persists with a dangling
subject). The foreign-key-safe order SHALL prune an absent Collector before an
absent Integration it referenced, and an absent Integration before an absent Vendor asset
it referenced. Because collectors and attestations are now one kind, the sync SHALL prune
one collector set rather than two, and the `validate` summary and the
`apply --dry-run` planned-state output SHALL report one collector count and one collector
section in place of the separate evidence-collector and attestation-template ones.

On the `sync` path the dangling-subject warning SHALL be DB-accurate, not the DB-less
authored-set check. The importer SHALL evaluate each scope `subject` against the PERSISTED asset
set with the subject-resolution predicate (a subject is unresolved when no `assets` row has its
id, or the row is a discovered asset in the `Retired` state) within the import transaction before
it commits, and return the unresolved subjects in the import result; `sync` SHALL print one
non-blocking warning per unresolved subject on the success path (exit `0`).
This DB-accurate check covers subjects that the DB-less validator cannot evaluate - a
discovered or retired `Machine` subject in particular. On the `sync` path the DB result SHALL be
authoritative for the scope subject: `sync` SHALL NOT also emit the DB-less authored-set
scope-subject warning for a subject that DOES resolve in the persisted asset set (which would be
a false positive for a healthy discovered `Machine`). The DB-less authored-set scope-subject
warning remains the surface for `validate` and `apply --dry-run`, which have no database.

#### Scenario: Retired or absent machine subject warns at sync against the persisted assets

- **WHEN** a `gitops sync` imports a config whose scope names a `Machine` subject that the
  persisted asset set either does not contain or holds only as a discovered asset in the
  `Retired` state
- **THEN** the sync succeeds (exit `0`) and prints a non-blocking DB-accurate warning for that
  unresolved subject, whereas a scope whose `Machine` subject resolves to a live discovered
  asset draws NO sync warning (the DB result supersedes the DB-less authored-set false positive)

#### Scenario: Unified scopes round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing scopes with standard,
  requirement, and control targets and org and vendor subjects, plus integrations and
  collectors, against a migrated store
- **THEN** each scope is persisted and readable with its `subject`, its one target,
  `disposition`, and `justification` intact

#### Scenario: Collectors of every type round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing an `integration`, a
  `script`, a `manual`, and a `training` collector against a migrated store
- **THEN** each collector is persisted in the one collector set and readable with its
  `type`, `provider` (when set), `frequency`, `threshold`, and typed `config` intact

#### Scenario: Dropping a collector hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists a set of collectors and a second `gitops sync`
  runs on a config that omits one collector (keeping its control, vendor asset, and
  connection)
- **THEN** the omitted collector is removed from the store, the retained collectors remain,
  and the import succeeds without a foreign-key violation

#### Scenario: Dropping a scope hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists the unified scopes and a second `gitops sync`
  runs on a config that omits one scope (keeping its subject asset and its target)
- **THEN** the omitted scope is removed from the store by the whole-set replace, the
  retained scopes remain, and the import succeeds without a foreign-key violation

#### Scenario: Removing a scope subject asset does not fail the sync

- **WHEN** a `gitops sync` removes an asset that is the `subject` of a scope the config
  keeps
- **THEN** the sync succeeds (the subject has no foreign key), and the dangling subject is
  reported as a non-blocking warning, not a failure

#### Scenario: Summary and dry-run report one collector set

- **WHEN** the user runs `gitops validate` or `gitops apply --dry-run` on a config
  containing collectors of several types
- **THEN** the summary line reports one collector count and the planned-state output lists
  one `Collectors` section, with no separate evidence-collector or attestation-template
  count or section

