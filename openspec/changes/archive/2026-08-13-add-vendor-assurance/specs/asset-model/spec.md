## ADDED Requirements

### Requirement: Vendor assurances persist in their own table

The system SHALL persist a vendor's certifications in a `vendor_assurances` table, added
by a forward-only migration applied by `freeboard system migrate`. A row SHALL carry the
vendor asset id, the standard id, the expiry date, an optional per-row warning-window
override in whole days, and created/updated timestamps. The table and its columns SHALL
follow the schema's existing conventions: a child table is named for its parent and a
foreign-key column ends in `_id`, so the table is `vendor_assurances` and its reference
columns are `vendor_id` and `standard_id`. The primary key SHALL be
`(vendor_id, standard_id)`: the row is not addressable by any route, scope, or reference,
so a synthetic id would be a value that appears nowhere in config and that a reader
could mistake for an authored one, and the composite key carries the one-entry-per-
standard rule for free. The table SHALL hold no status column: the state of a
certification is derived from its expiry and the clock (see the derived-status
requirement), and a stored one would be wrong from the moment the clock passed it.

Nullability SHALL follow the schema's convention that a required value is `NOT NULL`. The
two reference columns, the expiry, and both timestamps SHALL be `NOT NULL`. The
warning-window override SHALL be the only nullable column, because it is the only optional
one, and its null SHALL mean "use the deployment's configured window" rather than "no
window".

Both reference columns SHALL be `ON DELETE RESTRICT` foreign keys, to `assets` and to
`standards`, matching every other importer-pruned reference. The warning-window override
column SHALL carry a `CHECK` constraint admitting only null or a non-negative value, as a
backstop to the config validation that already rejects a negative one, so the column's
domain stays true if a later write path forgets the rule. The migration SHALL be
additive: it creates one empty table and changes no existing table, so every existing
row and every older application build stays valid after it, and no backfill runs. It
SHALL add no index on the expiry: the count of expiring vendors is computed in
application code over the owner-narrowed read (see the vendor-register capability), so
nothing queries the expiry in SQL and an index nothing queries would not pay for
itself.

A `sync` SHALL replace the assurance set as a whole (delete-all then insert) from the
declared config, matching how it replaces the unified scope set and the control-to-
requirement relation rows. A whole-set replace SHALL be used rather than an upsert:
an upsert leaves behind the row of an entry the author removed, and a per-vendor
replace additionally misses the rows of a vendor that left the config entirely. That
stale row would misreport a lapsed or withdrawn certification with no diagnostic
anywhere, so the removal path SHALL be covered by a test, not only the write path.

The replace SHALL run before every hard-remove in the same transaction, so neither
`RESTRICT` foreign key can block a deletion: before the absent declared-asset prune,
and before the absent standard, requirement, and control deletes. Removing a vendor
that holds assurances, and removing a standard that an assurance named, SHALL both
succeed without a foreign-key violation.

Assurance rows SHALL be readable through the compliance read store as one read over the
whole set, which the caller groups by vendor, matching how the register reads scopes. That
read SHALL be paired with the asset read in one snapshot, specified by the compliance
persistence capability. This code SHALL live in the MIT `Freeboard.Persistence` project,
SHALL NOT reference `Freeboard.Enterprise`, and SHALL add no new dependency.

#### Scenario: Migration adds the table without touching existing rows

- **WHEN** `freeboard system migrate` runs against a database carrying declared vendor
  assets
- **THEN** the migration applies successfully, an empty `vendor_assurances` table exists,
  and no existing row in any other table is changed

#### Scenario: Sync writes the authored entries

- **WHEN** a `sync` runs against a config declaring a `Vendor` asset with two
  `assurances` entries, one carrying a `warn_days`
- **THEN** two rows exist for that vendor, each carrying its standard, its expiry, and
  its own override where authored and null where not

#### Scenario: Removing an entry deletes its row

- **WHEN** a first `sync` persists a vendor's two assurances and a second `sync` runs
  against a config that keeps the vendor but drops one entry
- **THEN** only the kept entry's row remains, and the dropped entry's row is gone
  rather than left behind

#### Scenario: Removing a vendor removes its assurances and does not fail

- **WHEN** a `sync` runs against a config that no longer declares a vendor that held
  assurances
- **THEN** the vendor's assurance rows are cleared before the vendor row is deleted,
  the sync succeeds without a foreign-key violation, and no orphan row remains

#### Scenario: Removing a referenced standard does not fail the sync

- **WHEN** a `sync` runs against a config that drops both a standard and the assurance
  entry that named it
- **THEN** the assurance rows are replaced before the standard delete, so the sync
  succeeds without a foreign-key violation

#### Scenario: A non-Vendor asset holds no assurance row

- **WHEN** a `sync` runs against a config declaring `Company`, `Department`, and
  `Machine` assets
- **THEN** no assurance row references any of them, because config validation rejects
  `assurances` on a non-Vendor asset (see the gitops-config-format capability)

### Requirement: Assurance status is derived from the clock, never stored

The system SHALL derive a vendor assurance's status from its expiry date, its effective
warning window, and the current date, and SHALL NOT store or author it. The status
vocabulary SHALL be exactly `Valid`, `Expiring`, and `Expired`. The derivation SHALL
live in `Freeboard.Core` as a pure function that takes the current date as a parameter,
matching the collector staleness rule, so it needs no clock abstraction, no database,
and no configuration to be exercised.

The rule SHALL be: an expiry earlier than the current date is `Expired`, because a
certificate is valid through its expiry date; otherwise, when the effective warning window
is above zero and the expiry falls on or before the current date plus that window, it is
`Expiring`; otherwise `Valid`. The current date SHALL be the UTC date supplied by the
caller from the application's time provider, so a status changes at UTC midnight and a
test can pin the date. The effective warning window SHALL be the entry's own override when
it carries one and the deployment's configured window otherwise, resolved by the caller so
that `Freeboard.Core` reads no configuration.

A window of zero days SHALL mean no advance notice of any kind: the entry reads `Valid` up
to and including its expiry date and `Expired` on the day after it, never `Expiring`. The
window-above-zero guard is what carries that meaning. Without it a zero window would still
warn on the expiry date itself, which is a day of advance notice the author asked not to
receive.

There SHALL NOT be an authored status field. An authored status beside an expiry date
gives two facts that can contradict each other, and it would introduce a second status
vocabulary before the vendor review model defines the first one.

#### Scenario: A distant expiry is valid

- **WHEN** an assurance expires later than the current date plus its effective warning
  window
- **THEN** its status is `Valid`

#### Scenario: An expiry inside the window is expiring

- **WHEN** an assurance's effective warning window is above zero and it expires on or
  before the current date plus that window, and the current date has not passed the expiry
- **THEN** its status is `Expiring`, including on the first day of the window and on the
  expiry date itself

#### Scenario: A passed expiry is expired

- **WHEN** the current date is later than the assurance's expiry date
- **THEN** its status is `Expired`, and on the expiry date itself it is not yet expired

#### Scenario: A per-entry override replaces the deployment window

- **WHEN** two assurances share an expiry date and one carries a `warn_days` override
  that the other does not
- **THEN** each is evaluated against its own effective window, so the two may hold
  different statuses on the same day

#### Scenario: A zero window gives no advance notice

- **WHEN** an assurance's effective warning window is zero days
- **THEN** it reads `Valid` before its expiry date and on the expiry date itself, and
  `Expired` on the day after, never `Expiring`

## MODIFIED Requirements

### Requirement: Declared assets are synced by config; discovered assets are owned by ingest

The system SHALL reconcile declared assets from config and SHALL leave discovered
assets to ingest. A `sync` SHALL upsert every declared asset in the config and
SHALL hard-remove a declared asset whose id is absent from the config. A `sync`
SHALL NOT create, update, or delete any asset whose `source` is `discovered`, so a
config with few or zero declared assets never removes discovered inventory. Only
ingest SHALL write `source: discovered` assets; a declared config authoring
`source: discovered` SHALL be rejected as a validation error (see the
gitops-config-format capability). The declared-asset removal SHALL be
foreign-key-safe for every FK-backed reference: the rows that reference a removed
asset through a real `ON DELETE RESTRICT` foreign key (collectors,
integration-connections, org-scoped role assignments, and vendor assurances) SHALL be
pruned or replaced first. A
`Scope`'s subject is NOT such a reference: the unified `scopes` table stores the
subject as a scalar `subject_id` column with NO foreign key, so a scope whose subject
asset is removed is LEFT DANGLING (a tolerated non-blocking warning), not pruned, and
its presence never blocks the asset delete. The unified scope table's remaining
foreign keys - its `standard_id`/`requirement_id`/`control_id` target columns - are
`ON DELETE RESTRICT` but reference `standards`/`requirements`/`controls`, not assets, so
they do not participate in declared-asset removal. The former per-target
`requirement_scopes` and `vendor_scopes` tables no longer exist (merged into `scopes` by
migration `020`), and the former `evidence_collectors` and `attestation_templates` tables
no longer exist (merged into `collectors` by the collector-merge migration), so none of
them is in the pruned set.

Because declared slug ids and discovered ULID ids share one id space, a `sync`
SHALL detect a declared asset whose id equals an existing `discovered` asset's id
and SHALL fail the sync with an error naming the id, mutating nothing, rather than
upsert over the discovered row (which would rewrite it and violate the
never-touch-discovered rule).

#### Scenario: Absent declared asset is hard-removed

- **WHEN** a `sync` runs against a config that no longer declares a
  previously-synced declared asset
- **THEN** that declared asset is deleted from the store

#### Scenario: Discovered assets survive a declared-only sync

- **WHEN** a `sync` runs against a config that declares no `Machine` assets while
  discovered machines exist in the store
- **THEN** every discovered machine remains, unchanged, after the sync

#### Scenario: Empty config does not wipe discovered inventory

- **WHEN** a `sync` runs against a config with zero assets while discovered
  machines exist
- **THEN** all declared assets are removed and all discovered machines remain

#### Scenario: Declared config cannot author a discovered asset

- **WHEN** a config document declares `kind: Asset` with `source: discovered`
- **THEN** validation fails, naming the asset, and nothing is synced

#### Scenario: Declared id colliding with a discovered id fails the sync

- **WHEN** a `sync` runs against a config that declares an asset whose id equals the
  id of an existing `discovered` asset in the store
- **THEN** the sync fails with an error naming the id and nothing in the store is
  created, updated, or deleted

#### Scenario: Removing an asset that a scope names as subject leaves the scope dangling

- **WHEN** a `sync` hard-removes a declared asset that a kept scope names as its
  `subject`
- **THEN** the asset is deleted without a foreign-key violation and the scope persists
  with a now-dangling `subject_id` (surfaced as a non-blocking warning), because the
  scope subject has no foreign key and so is not pruned before the asset delete

#### Scenario: Removing a vendor that holds assurances does not violate a foreign key

- **WHEN** a `sync` hard-removes a declared `Vendor` asset that held assurance rows
- **THEN** the assurance set is replaced before the asset prune, so the vendor row is
  deleted without a foreign-key violation and none of its assurance rows survives
