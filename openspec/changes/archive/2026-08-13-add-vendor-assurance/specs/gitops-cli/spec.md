## MODIFIED Requirements

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
