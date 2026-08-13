## ADDED Requirements

### Requirement: Vendor assurance inputs are read as one snapshot

The store SHALL expose the vendor assurance inputs as one repeatable-read snapshot carrying
the ONE unified asset set and the whole vendor assurance set, and SHALL NOT expose a
standalone assurance read.

Both lists SHALL be read inside one transaction at repeatable-read isolation, so the
snapshot cannot straddle a concurrent importer commit. The pairing is not an optimisation:
every consumer narrows the assurances by the `owner` edges carried on the asset rows, so
two separate reads can pair the pre-import owner edges with post-import assurance rows and
produce a combination that never existed in the database. The asset list SHALL be the same
unified read every other consumer uses, not a vendor-only or otherwise filtered one, so the
narrowing decision resolves over the same tree everywhere.

The snapshot SHALL carry the assets and the assurances and nothing else, and the standards
and the unified scopes SHALL stay separate reads. The criterion is NOT that a separate read
takes no part in narrowing. The standards are a shared reference label: an unresolvable
standard title already renders as the standard id, so a title read from the far side of a
commit costs a label rather than a narrowing decision. The unified scopes ARE narrowed by
the same vendor visibility, and the register renders the justification of every excluded
scope behind that narrowing, so a separate scope read straddles exactly as a separate
assurance read would. That straddle is pre-existing and this requirement does not close it:
it is stated here so a later reader does not mistake the scope read's exclusion for a rule
that a narrowed read may sit outside the snapshot.

The assurance list SHALL be the whole set, ordered by vendor id then standard id, which the
caller groups by vendor, matching how the register reads scopes.

#### Scenario: The snapshot reads the assets and the assurances once, together

- **WHEN** a caller reads the vendor assurance input snapshot
- **THEN** it receives one unified asset list and the whole assurance set, both read in one
  repeatable-read transaction, and there is no separate assurance-only read method to call
  instead

#### Scenario: Narrowing and the rows it narrows agree

- **WHEN** the snapshot is read while an importer concurrently commits a sync that changes
  both a vendor's `owner` and its assurance rows
- **THEN** the owner edges and the assurance rows in the result are from one side of that
  commit, so no surface derived from the snapshot can show a vendor's assurances against
  an owner edge that no longer decides its readability

## MODIFIED Requirements

### Requirement: Import order is FK-safe and replaces the whole persisted set

The importer SHALL run in a fixed order within one DML transaction: upsert domain
rows by `id` in FK-safe order (standards, including their metadata; then
requirements, whose rows reference standards; then controls; then the declared assets,
including organisations - the asset `parent` is a scalar column with no foreign key, so no
parent-before-child ordering is required among them); then replace the whole unified scope
set (delete every `scopes` row then insert the new set), which is a whole-set replacement
rather than a per-row diff because the table has a primary key plus three unique keys, so an
id-keyed pair-swap cannot be upserted safely, and the scope `subject_id` has no foreign key
while its `standard_id`/`requirement_id`/`control_id` targets are already upserted; then
replace all `maps_to` cross-ref join rows for the imported set in the `control_requirements`
join (delete the existing join rows and insert the rows derived from the new config, a
whole-set replacement rather than a per-parent diff), which is safe because controls and
requirements are both upserted by now; then delete remaining domain rows whose `id`
is absent from the config in FK-safe order (the declared assets, including organisations -
with no child-before-parent ordering required because the asset `parent` has no foreign key;
then controls; then requirements; then standards, requirements being deleted before the
standards they reference). The whole-set scope
replace precedes the absent standard, requirement, and control deletes, so a removed
standard, requirement, or control no longer has a referencing scope row when it is
deleted, and precedes the declared-asset prune, so a removed asset simply leaves a scope
with a dangling `subject_id` (no foreign key blocks the delete).

That sequence states the ordering constraints THIS requirement fixes, not every statement
the importer runs. Constraints on the same transaction stated elsewhere bind it equally:
the integration-connection upsert and prune placements by the integration-connection
capability, the collector placements by this capability's collector storage requirement,
and the prune of every row that references a removed asset through a `RESTRICT` foreign key
- collectors, integration-connections, org-scoped role assignments, and vendor assurances -
by the asset-model capability. The sequence above SHALL therefore be read as a partial order
that admits those writes rather than as the complete list of them.

The importer SHALL also replace the whole vendor assurance set (delete every
`vendor_assurances` row then insert the new set), a whole-set replacement rather than an
upsert because an upsert leaves behind the row of an entry the author removed and a
per-vendor replace additionally misses the rows of a vendor that left the config entirely.
Its position is fixed by one constraint rather than two: the replace SHALL run after the
declared assets and the standards are upserted and BEFORE the first of the absent-row
deletes. Both of its `ON DELETE RESTRICT` foreign keys follow from that one placement,
because the declared-asset prune already precedes the absent-standard delete, so neither a
removed vendor nor a removed standard can be blocked by an assurance row.

After all inserts,
replacements, and deletes but BEFORE the transaction commits, the importer SHALL run the
unresolved-subject check (a `LEFT JOIN assets` applying the subject-resolution predicate) within
the same transaction so it observes the final post-write asset state, capture the unresolved
subjects into the import result, and only then commit; a failure of that check SHALL roll the
whole import back, preserving the all-or-nothing outcome. The order is foreign-key-safe not
because the config is acyclic (an asset `parent` cycle is a tolerated non-blocking warning, not a
rejection), but because the hard scope-target references (`standard`/`requirement`/`control`)
resolve and are upserted before the scope set that references them, and a tolerated asset `parent`
cycle cannot affect ordering: `parent` carries NO foreign key, so a dangling or cyclic `parent`
is a tolerated edge and never a foreign-key violation, and no parent-before-child ordering among
assets is required. A stable order therefore exists and every foreign-key constraint holds at
commit.

#### Scenario: Dropping a referenced standard in the same sync succeeds

- **WHEN** a sync removes a Standard that, in the prior persisted state, was
  referenced by a Requirement via `standard` or by a Scope targeting the standard, and the
  new config also removes those references
- **THEN** the import succeeds without a foreign-key violation, because the
  referencing rows are replaced or removed before the standard row is deleted

#### Scenario: Dropping a referenced requirement in the same sync succeeds

- **WHEN** a sync removes a Requirement that, in the prior persisted state, was
  targeted by a Scope, and the new config also removes that scope
- **THEN** the import succeeds without a foreign-key violation, because the whole scope
  set is replaced before the requirement row is deleted

#### Scenario: Removing a scope's subject asset does not block the sync

- **WHEN** a sync removes an asset that, in the prior persisted state, was the `subject` of
  a scope the new config keeps
- **THEN** the import succeeds without a foreign-key violation, because `subject_id` has no
  foreign key; the scope persists with a now-dangling subject, surfaced as a non-blocking
  warning

#### Scenario: Removing an assurance entry leaves no row behind

- **WHEN** a sync runs against a config that keeps a vendor but drops one of its assurance
  entries, or that drops the vendor and its entries together
- **THEN** the whole-set replace removes the dropped rows, no row survives for an entry the
  config no longer declares, and the import succeeds without a foreign-key violation
  because the replace ran before the declared-asset prune

#### Scenario: Requirement upserted after its standard

- **WHEN** a sync imports a standard and a requirement that references it in one
  config
- **THEN** the standard row is upserted before the requirement that references it,
  and on removal the requirement is deleted before the standard

#### Scenario: Assets reconcile without a parent-before-child ordering

- **WHEN** a sync imports a company and its department in one config
- **THEN** both reconcile in the unified `assets` table; because the asset `parent` is a
  scalar column with no foreign key, no parent-before-child (or child-before-parent)
  ordering is required and neither the insert nor the delete order can violate a foreign key
