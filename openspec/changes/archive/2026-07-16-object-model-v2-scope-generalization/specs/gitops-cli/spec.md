## MODIFIED Requirements

### Requirement: gitops commands enforce referential integrity for every config kind

The `gitops validate` and `gitops sync` commands SHALL apply the loader and
validator's referential-integrity checks to every config kind, including the unified
Scope, Integration, EvidenceCollector, and AttestationTemplate. A config in which a
kind's target reference names an id that no document defines SHALL be rejected: `gitops
validate` SHALL print a diagnostic that names the offending kind, resource, and missing
id, and SHALL exit non-zero, and `gitops sync` SHALL NOT import such a config. A `Scope`
target (`standard`, `requirement`, or `control`) is a hard reference and a dangling target
fails the command; a `Scope.subject` is a scalar asset reference and a dangling subject is
a NON-BLOCKING warning that does NOT fail the command (see the warnings-on-success
behaviour), because a subject may be a retired or not-yet-discovered asset.

At the command surface the coverage is a representative dangling reference for
EACH kind: a Scope naming an unknown target (a standard, requirement, or control that no
document defines), an EvidenceCollector naming an unknown control, an EvidenceCollector of
`type: integration` naming an unknown connection, and an AttestationTemplate naming an
unknown control. The exhaustive per-edge matrix (for example a Scope naming an unknown
requirement or control, a dangling Scope subject warning, or an Integration naming an
unknown vendor) is owned by the `Freeboard.Core` ConfigValidator unit tests, so the CLI
layer proves command wiring only rather than re-running every edge.

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

#### Scenario: Validate rejects a dangling evidence-collector control reference

- **WHEN** the user runs `gitops validate` on a directory whose EvidenceCollector
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the EvidenceCollector and the
  unknown control id and exits non-zero

#### Scenario: Validate rejects a dangling evidence-collector connection reference

- **WHEN** the user runs `gitops validate` on a directory whose EvidenceCollector
  of `type: integration` names a `connection` id that no Integration
  document defines
- **THEN** the command prints a diagnostic naming the EvidenceCollector and the
  unknown connection id and exits non-zero

#### Scenario: Validate rejects a dangling attestation-template control reference

- **WHEN** the user runs `gitops validate` on a directory whose AttestationTemplate
  names a `control` id that no Control document defines
- **THEN** the command prints a diagnostic naming the AttestationTemplate and the
  unknown control id and exits non-zero

#### Scenario: Sync does not import a config with a dangling target reference

- **WHEN** the user runs `gitops sync` on a directory that fails referential
  integrity for any target reference
- **THEN** the command reports the validation error, exits non-zero, and writes
  no rows for that config

### Requirement: gitops sync round-trips and hard-removes the new config kinds

The `gitops sync` command SHALL persist the unified Scope, Vendor (as an `Asset` of
`type: Vendor`), Integration, EvidenceCollector, and AttestationTemplate resources from a
valid config and, on a later sync that drops a resource, SHALL hard-remove the persisted
row whose id is absent from the new config, in foreign-key-safe order, preserving the
existing hard-remove semantics. A resource kept across syncs SHALL remain persisted. The
unified scope set SHALL be replaced as a whole set (delete-all then insert) each sync, and
that replace SHALL precede the absent standard, requirement, and control deletes, so no
target `RESTRICT` foreign key blocks a removal; a scope's `subject` has no foreign key, so
removing a subject asset never blocks the sync (the scope persists with a dangling
subject). The foreign-key-safe order SHALL prune an absent EvidenceCollector before an
absent Integration it referenced, and an absent Integration before an absent Vendor asset
it referenced.

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
  evidence-collectors, against a migrated store
- **THEN** each scope is persisted and readable with its `subject`, its one target,
  `disposition`, and `justification` intact

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
