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

The success summary SHALL carry a count for EVERY declared kind the loader supports -
standards, requirements, controls, assets, scopes, collectors, and integrations - so no
kind a config authors is silently absent from the operator's confirmation. The asset
count SHALL keep its per-type breakdown. Integrations SHALL be counted as their own kind
rather than folded into the collector count, because an integration is pruned as its own
set and a collector that names it must be removed first: `collectors.connection_id` is
`ON DELETE RESTRICT`, so a config that omits a connection while keeping a collector that
references it fails validation rather than removing either.

#### Scenario: Valid config passes

- **WHEN** the user runs `freeboard gitops validate <dir>` on a valid config
- **THEN** the command prints a success summary to stdout carrying a count for each of
  standards, requirements, controls, assets, scopes, collectors, and integrations, and
  exits with code `0`

#### Scenario: Summary counts an authored integration

- **WHEN** the user runs `freeboard gitops validate <dir>` on a valid config containing
  at least one `Integration` document
- **THEN** the summary reports that integration in an integration count of its own,
  distinct from the collector count

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

The planned-state output SHALL carry one section per declared kind - standards,
requirements, controls, assets, scopes, collectors, and integrations - so every
resource a `sync` would write or hard-remove is visible before the write. The
integrations section SHALL list, per connection, its id, title, provider, base URL,
discovery cadence, and optional vendor reference. It SHALL NOT report the connection's
API-token health, because `apply --dry-run` makes no network call and reads no
out-of-band token configuration.

#### Scenario: Dry-run prints planned state

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on a valid
  config
- **THEN** the command prints one section per declared kind - standards, requirements,
  controls, assets, scopes, collectors, and integrations - to stdout and exits `0`
  without writing any state

#### Scenario: Dry-run lists an authored integration

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on a valid config
  containing an `Integration` document
- **THEN** the planned state lists that connection with its provider, base URL, and
  discovery cadence, and carries no token value or token-health field

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

The command-group documentation SHALL also state the shape of what the commands print:
that the `validate` success summary carries one count per declared kind, that the
`apply --dry-run` planned state carries one section per declared kind, and that the `sync`
success line carries one count per declared kind. Stating the shape rather than only the
exit codes is what makes a kind missing from the output a documented defect instead of an
unwritten expectation.

The hard-removal documentation SHALL carry the discovered-asset carve-out: a `sync`
hard-removes only resources it declares, and a discovered asset is never removed by a sync
because ingest is its only writer. Without the carve-out the documentation reads as though
a config with no `Machine` documents deletes the operator's discovered inventory, which is
not what the importer does.

#### Scenario: Group doc no longer claims no writes or no network

- **WHEN** the `gitops` command-group documentation is read
- **THEN** it does not claim the group writes no state or makes no network calls,
  and it scopes the non-writing description to `validate` and `apply --dry-run`

#### Scenario: Command doc states the printed output shape

- **WHEN** a reader consults the `gitops` command documentation
- **THEN** it states that `validate` prints one count per declared kind, that
  `apply --dry-run` prints one planned-state section per declared kind, and that the `sync`
  success line prints one count per declared kind

#### Scenario: Hard-removal doc carves out discovered assets

- **WHEN** a reader consults the hard-removal warning in the `gitops` documentation
- **THEN** it states that the hard removal applies to declared resources and that a
  discovered asset is not removed by a sync, so a config declaring no `Machine` assets
  does not delete discovered inventory

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
subject). The vendor assurance set SHALL likewise be replaced as a whole set each sync. Its
`vendor_id` and `standard_id` foreign keys are both `ON DELETE RESTRICT`, so the replace
SHALL precede the declared-asset prune, which itself already precedes the absent-standard
delete: one placement before the prune block therefore covers both keys. The
foreign-key-safe order SHALL prune an absent Collector
before an absent Integration it referenced, and an absent Integration before an absent
Vendor asset it referenced. Because collectors and attestations are now one kind, the sync
SHALL prune one collector set rather than two, and the `validate` summary and the
`apply --dry-run` planned-state output SHALL report one collector count and one collector
section in place of the separate evidence-collector and attestation-template ones.

The `sync` success line SHALL carry a count for EVERY declared kind, including
integrations, matching the kind set the `validate` summary reports, so an operator sees
the integration set a narrowing config would have removed.

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

#### Scenario: Dropping a vendor assurance hard-removes its row on the next sync

- **WHEN** a first `gitops sync` persists a `Vendor` asset carrying two assurances and a
  second `gitops sync` runs on a config that keeps the vendor and drops one entry
- **THEN** only the kept entry's row remains, the dropped entry's row is gone, and the
  sync succeeds

#### Scenario: Dropping a vendor that holds assurances does not violate a foreign key

- **WHEN** a first `gitops sync` persists a `Vendor` asset carrying assurances and a second
  `gitops sync` runs on a config that omits the vendor and its entries
- **THEN** the assurance set is replaced before the declared-asset prune, the vendor row is
  removed, and the import succeeds without a foreign-key violation

#### Scenario: Dropping a standard an assurance named does not violate a foreign key

- **WHEN** a second `gitops sync` runs on a config that drops both a `Standard` and the
  assurance entry that named it
- **THEN** the assurance set is replaced before the absent-standard delete, and the import
  succeeds without a foreign-key violation

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

#### Scenario: Integrations round-trip through sync

- **WHEN** the user runs `gitops sync` on a valid config containing an `Integration`
  against a migrated store
- **THEN** the connection is persisted and readable with its `provider`, `base_url`,
  `discovery_cadence`, and optional vendor reference intact, and no token value is stored

#### Scenario: Dropping an integration hard-removes it on the next sync

- **WHEN** a first `gitops sync` persists an `Integration` and the collector that names
  it, and a second `gitops sync` runs on a config that omits both
- **THEN** the collector is removed before the connection, the connection is removed, the
  import succeeds without a foreign-key violation, and the retained resources remain

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

#### Scenario: Sync success line counts integrations

- **WHEN** the user runs `gitops sync` on a valid config containing integrations against a
  migrated store
- **THEN** the success line reports an integration count alongside the standard,
  requirement, control, asset, scope, and collector counts

### Requirement: gitops sync maps the declared-only asset guarantee onto its exit codes

`gitops sync` SHALL exit `0` on a config that declares no `Machine` assets, and on a config
that declares zero assets of any kind, rather than reporting the absent declarations as an
error. When the import rejects a declared asset whose id equals an existing discovered
asset's id, `gitops sync` SHALL exit `3` - an operational failure, not a config-validation
failure, because the collision is detected against database state that validation cannot
see - and SHALL print a message to stderr naming the colliding id and stating that nothing
was written.

The store-level guarantee behind those exit codes is owned by the `asset-model` capability
("Declared assets are synced by config; discovered assets are owned by ingest"): a sync
reconciles declared assets only, never creates, updates, or deletes a discovered asset, and
fails mutating nothing when a declared id collides with a discovered one. This requirement
adds only what the `gitops sync` command surface reports for it, which that capability does
not govern.

#### Scenario: A config declaring no machines succeeds

- **WHEN** a `gitops sync` runs against a migrated store holding discovered machines, on a
  config that declares no `Machine` assets
- **THEN** the command exits `0`, treating the absent declarations as neither an error nor
  a removal

#### Scenario: An emptied config succeeds

- **WHEN** a `gitops sync` runs on a config with zero assets against a store holding
  declared assets and discovered machines
- **THEN** the command exits `0`

#### Scenario: Declared id colliding with a discovered id exits 3 naming the id

- **WHEN** a `gitops sync` runs on a config declaring an asset whose id equals an existing
  discovered asset's id
- **THEN** the command prints a message to stderr naming the colliding id and stating that
  nothing was written, and exits `3`

### Requirement: Dangling and missing asset edges surface as non-blocking command warnings

The `gitops validate`, `gitops apply --dry-run`, and `gitops sync` commands SHALL print a
non-blocking warning to stderr, and SHALL still exit `0`, when an asset's `parent` or
`owner` names an id no document defines and when a required read anchor is missing (a
`Vendor` with no `owner`, or a `Machine` with no `parent`). A `Company` or `Department`
with no `parent` is a legitimate root and SHALL draw no warning. These edges are scalar
references with no foreign key, so a dangling one never blocks a command; suppressing the
warning would instead hide an asset that is visible to no caller under the fail-closed
read model.

#### Scenario: Dangling parent warns without failing

- **WHEN** the user runs `gitops validate` on a config whose asset names a `parent` id no
  document defines, with every other reference resolving
- **THEN** the command prints a warning to stderr naming the asset and the unknown parent,
  prints the success summary, and exits `0`

#### Scenario: Dangling owner warns without failing

- **WHEN** the user runs `gitops validate` on a config whose `Vendor` asset names an
  `owner` id no document defines
- **THEN** the command prints a warning to stderr naming the vendor and the unknown owner
  and exits `0`

#### Scenario: Missing required edge warns without failing

- **WHEN** the user runs `gitops apply --dry-run` on a config carrying a `Vendor` with no
  `owner` and a `Machine` with no `parent`, alongside a root `Company` with no `parent`
- **THEN** the command warns for the vendor and the machine, does NOT warn for the root
  company, prints the planned state, and exits `0`

#### Scenario: Dangling parent warns on the sync path without failing

- **WHEN** the user runs `gitops sync` on a config whose asset names a `parent` id no
  document defines
- **THEN** the command prints a warning to stderr naming the asset and the unknown parent,
  imports the config, and exits `0`

