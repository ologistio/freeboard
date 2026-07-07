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
validator's referential-integrity checks to every config kind, including Vendor,
VendorScope, EvidenceCollector, and AttestationTemplate. A config in which any
kind references an id that no document defines SHALL be rejected: `gitops
validate` SHALL print a diagnostic that names the offending kind, resource, and
missing id, and SHALL exit non-zero, and `gitops sync` SHALL NOT import such a
config.

At the command surface the coverage is a representative dangling reference for
EACH new kind: a VendorScope naming an unknown vendor, an EvidenceCollector
naming an unknown control, and an AttestationTemplate naming an unknown control.
The exhaustive per-edge matrix (for example a VendorScope naming an unknown
requirement or control, or an EvidenceCollector naming an unknown vendor) is
owned by the `Freeboard.Core` ConfigValidator unit tests, so the CLI layer proves
command wiring only rather than re-running every edge.

#### Scenario: Validate rejects a dangling vendor-scope vendor reference

- **WHEN** the user runs `gitops validate` on a directory whose VendorScope names
  a `vendor` id that no Vendor document defines
- **THEN** the command prints a diagnostic naming the VendorScope and the unknown
  vendor id and exits non-zero

#### Scenario: Validate rejects a dangling evidence-collector control reference

- **WHEN** the user runs `gitops validate` on a directory whose EvidenceCollector
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the EvidenceCollector and the
  unknown control id and exits non-zero

#### Scenario: Validate rejects a dangling attestation-template control reference

- **WHEN** the user runs `gitops validate` on a directory whose AttestationTemplate
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the AttestationTemplate and the
  unknown control id and exits non-zero

#### Scenario: Sync does not import a config with a dangling reference

- **WHEN** the user runs `gitops sync` on a directory that fails referential
  integrity for any of the new kinds
- **THEN** the command reports the validation error, exits non-zero, and writes
  no rows for that config

### Requirement: gitops sync round-trips and hard-removes the new config kinds

The `gitops sync` command SHALL persist Vendor, VendorScope, EvidenceCollector,
and AttestationTemplate resources from a valid config and, on a later sync that
drops a resource, SHALL hard-remove the persisted row whose id is absent from the
new config, in foreign-key-safe order, preserving the existing hard-remove
semantics. A resource kept across syncs SHALL remain persisted.

#### Scenario: New kinds round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing vendors,
  vendor-scopes, evidence-collectors, and attestation-templates against a
  migrated store
- **THEN** each resource is persisted and readable with its fields and references
  intact

#### Scenario: Dropping a resource hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists the new kinds and a second `gitops sync`
  runs on a config that omits one vendor-scope (keeping its vendor and its
  requirement or control target), one evidence-collector (keeping its control and
  vendor), and one attestation-template (keeping its control)
- **THEN** the omitted rows are removed from the store, the retained rows remain,
  and the import succeeds without a foreign-key violation

The dropped VendorScope exercises the whole-set-replace removal path
(`MySqlGitOpsImporter` ReplaceVendorScopes), which is distinct from the
DeleteAbsent path used for evidence-collectors and attestation-templates, so it is
worth covering at the command surface. A bare Vendor's hard-remove uses the same
DeleteAbsent path and is covered by the Persistence
`MySqlIntegrationTests.ResyncRemovesVendorThatHadVendorScope` resync-removal test,
so the CLI case delegates it rather than adding an unreferenced vendor to the
fixture.

