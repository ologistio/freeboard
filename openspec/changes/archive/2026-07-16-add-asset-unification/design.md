## Context

This is the first ticket of the object model v2 cutover (decisions in #121). v1
models the estate with three kinds:

- `Organisation` (declared): a `Company`/`Department` tree, id is an authored
  slug, `parent` is a self reference validated as an acyclic tree. Persisted in
  `organisations` (self-FK on `parent_id`). Read model `OrganisationRow`. Drives
  read-access (subtree union in `AuthzOrgAccess`) and scope inheritance
  (`OrgAncestry`).
- `Vendor` (declared): a flat id+title record, org-independent. Persisted in
  `vendors`. Read model `VendorRow`. Read globally (every authenticated user sees
  every vendor, `vendor-register` spec).
- `Asset` (discovered, from #98): the `Machine`-only resolved machine. Persisted
  in `asset` + `asset_source`, id is a ULID, `organisation_id` is a scalar column
  with no FK, written only by ingest through `MySqlAssetWriteStore`. Read model
  `AssetRow`, read store `MySqlAssetStore`.

Downstream references already point at these three. The complete, schema-verified
set of enforced foreign keys (constraint name -> target table) is:

- `scopes.organisation_id` `fk_scopes_organisation` (007) -> `organisations`
- `requirement_scopes.organisation_id` `fk_requirement_scopes_organisation` (009)
  -> `organisations`
- `authz_organisation_role_assignments.organisation_id`
  `fk_authz_org_role_assignments_org` (010) -> `organisations`
- `vendor_scopes.vendor_id` `fk_vendor_scopes_vendor` (011_vendors) -> `vendors`
- `evidence_collectors.vendor_id` `fk_evidence_collectors_vendor` (012) -> `vendors`
- `integration_connections.vendor_id` `fk_integration_connections_vendor` (018)
  -> `vendors`
- `asset_source.(asset_id, organisation_id)` `fk_asset_source_asset` (017) ->
  `asset (id, organisation_id)` (composite)
- `organisations.parent_id` `fk_organisations_parent` (007) -> `organisations`
  (self-FK; dropped with the table, becomes the scalar-no-FK `assets.parent`)

Two columns hold a scalar organisation id with NO foreign key and so need no DDL
(their ids resolve to the merged asset rows unchanged): `evidence_runs.organisation_id`
(011_evidence) and `authz_audit_events.organisation_id` (010).

The config path is: `ConfigLoader` (kind-routing + unknown-field detection) ->
`ConfigValidator` (per-kind rules) -> `ImportPlan` (flatten to rows) ->
`MySqlGitOpsImporter` (one DML transaction: upsert declared rows, prune absent).
The importer already hard-removes declared domain rows absent from config.

Constraints: MIT only (no `Freeboard.Enterprise`); reference graph Core -> nothing,
Persistence -> Core, CLI -> Core+Persistence, web -> Core+Enterprise+Persistence;
`Freeboard.Agent` and `Freeboard.CLI` stay EE-free and cross-platform; no new
package dependency; forward-only migrations discovered by filename ordinal
(`MigrationCatalog`), so a new `019_*.sql` is picked up automatically.

## Goals / Non-Goals

**Goals:**

- One declared-or-discovered `Asset` kind with `type` and explicit `source`,
  replacing `Organisation`, `Vendor`, and the `Machine`-only asset.
- One `assets` table, one id space (authored slug for declared, ULID for
  discovered), carrying the discovered-only fields.
- Two mutually exclusive edges: `parent` (containment, inheritance + read-access)
  and `owner` (accountability, read-access only), both scalar-no-FK and
  dangling-tolerant with a non-blocking warning.
- `sync` reconciles declared assets only and never touches discovered assets.
- Read-access via the parent chain (rooted assets) and via `owner` (vendors);
  global vendor readability dropped.
- Web and CLI read models in the same change (parity is an acceptance rule).
- MySQL integration tests for the merged schema, the migration, and the
  declared-only sync.

**Non-Goals (later v2 tickets):**

- Scope generalization to one `scopes` table (#92 follow-on). This change keeps
  the existing `scopes`/`requirement_scopes`/`vendor_scopes` tables and only
  re-points their FK targets at `assets`.
- Collector rename and typed config.
- `Group`, facts, and group-based resolution (#123).
- `Person` type and device-to-person assignment.
- Any discovery ingest change beyond retargeting the write store at `assets`
  (#101 owns discovery writes).
- Narrowing the residual `vendor`-id exposure on the `/evidence-collectors` and
  `/integration-connections` surfaces - their endpoints, web pages, and the CLI
  `CollectorCommands`/`ConnectionCommands` - and the collector's `vendor` title
  rendered in the Statement-of-Applicability requirement drill-down. Each still carries
  a `vendor` reference readable by a caller who cannot see that vendor. The SoA
  drill-down vendor title is the collector's vendor surfaced in the SoA evidence view,
  so it falls under the same collector-vendor-narrowing. This residual exposure is
  intentional for this ticket and tracked: narrowing `/evidence-collectors` and the SoA
  drill-down vendor-title is deferred to the collector-rename ticket, and narrowing
  `/integration-connections` to the IntegrationConnection ticket (#126). This change's
  vendor-readability narrowing (D5) covers `/vendors` and `/vendor-scopes` only.

## Decisions

### D1: One `assets` table, one id space, `type` + `source` columns

A single table replaces `organisations`, `vendors`, and `asset`. `id
VARCHAR(190)` `utf8mb4_bin` holds both authored slugs and ULIDs in one PK space.
`type` (`Company`/`Department`/`Machine`/`Vendor`) and `source`
(`declared`/`discovered`) are exact-byte (`utf8mb4_0900_bin`) columns.
Declared-shared columns (`api_version`, `title`, `created_at`, `updated_at`) and
discovered-only nullable columns (`identity_kind`, `identity_value`, `state`,
`hostname`, `first_seen_at`, `last_seen_at`, `retired_at`) coexist; declared rows
leave the discovered-only columns null, discovered rows populate them.

Alternative considered: keep three tables and add a `type` view over them.
Rejected - it preserves the three-schema cost the ticket exists to remove and
gives later tickets no single target.

### D2: `parent` and `owner` are scalar, no FK, mutually exclusive, dangling tolerated

Both are `VARCHAR(190)` `utf8mb4_bin` nullable columns, no foreign key, matching
the v1 `asset.organisation_id` precedent (an asset must survive org churn and a
discovered child must not let the inventory block a config `sync`). At most one is
non-null per row; a DB `CHECK` backstops the Core validator
(`(parent IS NULL) OR (owner IS NULL)`). Target-type restrictions (`parent` ->
Company/Department; `owner` -> Company/Department) and the carrier restrictions
(`owner` only on Vendor; `parent` only on Company/Department/Machine) are Core
validation, not DB constraints, because they compare across rows.

Dangling is tolerated: a `parent`/`owner` naming an id absent from the resolved
asset set is a NON-BLOCKING warning, never an error. This is a real behavior
change from v1, where a dangling/cyclic org `parent` is a hard validation error.
Rationale: a discovered child can name a declared parent that a later `sync`
removes; blocking would wedge unrelated config changes.

Cycles: `parent` can still form a cycle among declared assets. The read-access
and inheritance walks already carry a visited-set cycle guard (`OrgAncestry`,
`OrgScope`, `AuthzOrgAccess`), so a cycle is tolerated at resolution the same way
dangling is - surfaced as a warning, never a crash. This drops v1's hard "parent
cycle rejected" error. Import ordering does not need a cycle guard because
`assets.parent` has no FK, so upsert and delete need no parent-before-child order
(D4).

### D3: Non-blocking warning severity

`Diagnostic` currently has no severity and `ConfigResult.IsValid` is "no
diagnostics at all". Add a `DiagnosticSeverity { Error, Warning }` with `Error`
the default, and redefine `IsValid` as "no `Error` diagnostics" (warnings do not
fail `validate`/`apply`/`sync`). Dangling `parent`/`owner` is the first `Warning`.
The CLI prints warnings to stderr but still exits 0 and syncs; the web surfaces
them as a non-blocking notice.

The CLI must print warnings on the SUCCESS path, not only on failure. Today
`GitOpsCommands` calls its diagnostic printer only inside the `if (!IsValid)` branch,
so a warnings-only (valid) result would drop every warning across `validate`,
`apply --dry-run`, and `sync` and exit 0 silently. Redefining `IsValid` to ignore
warnings makes that branch unreachable for warnings, so each of the three commands
must print any `Warning` diagnostics to stderr on the valid path before printing its
summary/planned-state and exiting 0. Otherwise a dangling/missing-edge warning would
never reach the operator - the opposite of the "surface it so the author sees it"
rationale (D2, D11).

Alternative: a separate warnings list on `ConfigResult`. Rejected - a severity on
the existing `Diagnostic` reuses the whole pipeline (loader, validator, importer,
CLI, web) with one field instead of a parallel channel.

### D4: `sync` reconciles declared assets only

The importer upserts declared assets (`source = 'declared'`) and hard-removes a
declared asset whose id is absent from config, filtered `WHERE source =
'declared'`. It MUST NOT delete or touch any `source = 'discovered'` row. The
existing `DeleteAbsentAsync` deletes by `id NOT IN @KeepIds`; assets need a
source-guarded variant so a config with zero declared assets does not truncate the
discovered inventory. Discovered assets are owned by ingest
(`MySqlAssetWriteStore`), unchanged except for the table/column rename.

FK-safety in the importer: declared assets are now the target of all six
retargeted enforced FKs (`scopes.organisation_id`,
`requirement_scopes.organisation_id`,
`authz_organisation_role_assignments.organisation_id`, `vendor_scopes.vendor_id`,
`evidence_collectors.vendor_id`, `integration_connections.vendor_id`; all
`ON DELETE RESTRICT` after retargeting - see D7 for the complete set). The
existing prune order already deletes referencing scope/collector rows before their
targets; the org and vendor prunes are replaced by a single declared-asset prune
placed after those referencing-row prunes. Any referencing row a declared prune
would orphan (a requirement-scope, an integration-connection, or an org role
assignment on a removed asset) is pruned before the declared-asset prune, same as
scopes and collectors.

No parent-before-child ordering is needed for declared-asset upsert or delete.
`assets.parent` has no foreign key (D2), unlike the v1 `organisations.parent_id`
self-FK (`ON DELETE RESTRICT`) that forced the child-before-parent delete order.
With no self-FK the delete of a parent asset cannot be blocked by a surviving child
row, so the `OrderParentBeforeChild` topo sort is dropped for assets. A parent
deleted while its child survives leaves the child with a dangling `parent`, which is
a tolerated non-blocking warning (D2), not an FK violation.

### D5: Read-access via parent chain and vendor owner

`AuthzOrgAccess` and `OrgScope`/`OrgAncestry` already compute the read-subtree
union over the org `parent` graph. Because Company/Department orgs are now
Company/Department assets with the same `parent` edge, these walks operate on the
declared-asset projection with no logic change - they consume `OrganisationRow`,
which is now built from `assets WHERE type IN ('Company','Department')`.

Vendors change: a vendor is visible when its `owner` (a Company/Department asset)
is in the caller's accessible set. Org-wide vendors are owned at the Company root.
The `vendor-register` "every authenticated user sees every vendor" behavior is
dropped; the web page and CLI narrow vendor rows by `owner` membership in the
accessible set. A vendor with a null or dangling `owner` is not globally visible;
its visibility follows the same fail-closed rule (out-of-access owner -> not shown).

Vendor-scopes narrow the same way. The `/vendor-scopes` read is today a global read
(`ComplianceEndpoints`), returning every vendor-scope and its `Out` justification to
any authenticated caller, and a zero-grant caller sees them all. A vendor hidden by
owner narrowing must have its vendor-scopes hidden too: otherwise the hidden vendor's
id and its exception rationale leak through the scope list even though the vendor row
is suppressed. So `/vendor-scopes` (and the CLI read backed by it) MUST narrow to the
vendor-scopes whose vendor's `owner` is in the caller's accessible set, using the same
fail-closed rule as the vendor rows (a vendor with a missing or dangling owner has its
scopes hidden too). This closes the fail-closed condition across every vendor-register
and vendor-scope read surface: a missing/dangling owner hides the vendor on the
register row, its vendor-scope justifications, and the CLI - not just one. It does NOT
cover the `/evidence-collectors` and `/integration-connections` surfaces, where a
hidden vendor's id is still exposed; narrowing those is deferred to sibling tickets
(see Non-Goals).

### D6: Read-model projection keeps existing row shapes where possible

`OrganisationRow(Id, Title, Kind, Parent)` is preserved and projected from
`assets` (type Company/Department; `Kind` = `type`; `Parent` = `parent`). This
keeps `AuthzOrgAccess`, `OrgScope`, `OrgAncestry`, `OrgSelection`, the SoA
projection, and the org selector unchanged. `VendorRow(Id, Title)` gains an
`Owner` field for the readability narrowing. `AssetRow` gains `Type`, `Source`,
`Parent`, `Owner` and its `OrganisationId` is replaced by `Parent`; the
discovered-only fields stay. The only new declared-asset read this change adds is the
owner-narrowed vendor read (register web page and its `/vendors`/`/vendor-scopes`
endpoints). No general asset/machine listing surface exists today, and this change
does not add one - the discovered-machine reads keep their existing shape retargeted
at `assets`. A broader asset listing belongs to a later v2 ticket that has a caller
for it; building it now would be speculative surface.

### D7: Complete FK-holder set the migration must re-point (verified against the schema)

Every enforced foreign key that names `organisations`, `vendors`, or the #98
`asset` table was enumerated from the migration DDL (not inferred). Migration 019
MUST re-point or drop every one; a single missed FK to a dropped table blocks the
`DROP TABLE` and the whole cutover. The complete set (constraint name, source
migration, current target):

1. `fk_scopes_organisation` (007) `scopes.organisation_id` -> `organisations`.
   Re-point at `assets(id)`.
2. `fk_requirement_scopes_organisation` (009) `requirement_scopes.organisation_id`
   -> `organisations`. Re-point at `assets(id)`. Easy to overlook alongside the
   `scopes` FK; verified present against the DDL.
3. `fk_authz_org_role_assignments_org` (010)
   `authz_organisation_role_assignments.organisation_id` -> `organisations`.
   Re-point at `assets(id)`. Present in the schema (verified against the DDL), not
   conditional.
4. `fk_vendor_scopes_vendor` (011_vendors) `vendor_scopes.vendor_id` -> `vendors`.
   Re-point at `assets(id)`.
5. `fk_evidence_collectors_vendor` (012) `evidence_collectors.vendor_id` ->
   `vendors`. Re-point at `assets(id)`.
6. `fk_integration_connections_vendor` (018) `integration_connections.vendor_id`
   -> `vendors`. Re-point at `assets(id)`. Easy to miss because migration 018 is
   recent; verified present against the DDL.
7. `fk_asset_source_asset` (017) `asset_source.(asset_id, organisation_id)` ->
   `asset (id, organisation_id)`, composite. Replaced per D8.
8. `fk_organisations_parent` (007) `organisations.parent_id` -> `organisations`,
   self-FK. Not re-pointed: it is dropped with the `organisations` table and
   becomes the scalar-no-FK `assets.parent` (D2).

`evidence_runs.organisation_id` (011_evidence) and
`authz_audit_events.organisation_id` (010) hold a scalar organisation id with NO
foreign key; their ids resolve to the merged asset rows unchanged, so they need no
migration DDL. Their meaning generalizes to "an asset subject id" without a rename
this ticket (D10).

### D8: Relax the `asset_source` composite FK to a simple `asset_id` FK

The #98 `asset_source` FK is composite - `(asset_id, organisation_id)` ->
`asset (id, organisation_id)` - and enforced cross-org isolation of a source
attachment at the database. The merge makes the machine's `organisation_id` the
nullable, dangling-tolerant `assets.parent` with NO foreign key (D2). A foreign key
cannot be anchored on a column that is deliberately FK-free and nullable, so the
composite FK cannot survive as-is. Resolution: drop the composite FK and add a
simple `asset_source.asset_id -> assets(id)` FK (`ON DELETE RESTRICT`);
`asset_source` keeps its own `organisation_id` column and its
`(organisation_id, source, external_id)` uniqueness key, and cross-org isolation of
sources moves to query filtering, consistent with the rest of the source-filtered
discovered read path.

The DB no longer joins `asset_source` to the machine on a shared org column, so the
isolation the composite FK gave for free must be reasserted in two places:

- Discovered-machine identity uniqueness. Today the discovered `asset` row is unique
  per `(organisation_id, identity_kind, identity_value)` so the same serial under two
  orgs is two machines. On the merged table `organisation_id` is `parent`, so the key
  becomes `(parent, identity_kind, identity_value)`. `parent` is nullable and a MySQL
  unique index treats each `NULL` as distinct, so a null-parent discovered machine is
  NOT subject to the org-scoped uniqueness - two null-parent machines with the same
  identity would both be allowed. That is acceptable and intentional here: ingest
  always writes the machine under the discovering connection's org (the machine's
  `parent`), so the null-parent case does not arise on the ingest write path; the
  merged unique index `(parent, identity_kind, identity_value)` preserves the v1
  per-org dedup for every non-null parent, and the null-parent rows are left to
  ingest's own scoping rather than a DB uniqueness guarantee. State this explicitly
  so a reader does not read the nullable key as an accidental gap.
- Org-scoped source reads. `MySqlAssetStore.GetBySourceAsync` today joins
  `asset_source` to the machine on `s.asset_id = a.id AND s.organisation_id =
  a.organisation_id` and filters `s.organisation_id = @Org`, so a source row cannot
  surface a machine outside its org. After the merge `a.organisation_id` is
  `a.parent`, so the equivalent org-scoped predicate is `s.asset_id = a.id AND
  s.organisation_id = a.parent` with the filter `a.parent = @Org` (equivalently
  `s.organisation_id = @Org`). Every source-filtered read MUST keep the org predicate
  against `a.parent` so a bad or cross-org `asset_source` row cannot return another
  org's machine. This is the query-enforced isolation that replaces the dropped
  composite FK; it is a hard requirement, not a nicety.

An alternative considered was to preserve the composite FK by keeping a parallel
non-null org-scope column on `assets` for Machine rows.
Rejected: that column would reintroduce the exact `organisation_id` coupling the
unification removes, add a column with no reader, and contradict the
nullable/dangling-tolerant `parent` model. The trade-off (a DB-enforced isolation
guarantee becomes a query-enforced one) is flagged in Risks for the reviewer.

### D9: A declared id colliding with an existing discovered ULID is a hard sync error

Declared slugs and discovered ULIDs share one id space (D1). If a `sync` upserts a
declared asset whose id equals an existing `source = 'discovered'` row's id, the
upsert would rewrite that discovered row (flipping its `source`, blanking its
discovered-only fields) - a direct violation of "sync never touches a discovered
asset" (D4). The importer MUST detect a declared id that collides with an existing
discovered id and fail the sync, BEFORE any write, mutating nothing. This is a
sync-time importer check, not a config-load validation, because it compares authored
ids against live DB state.

Surface and exit code. The check runs inside the importer's DML transaction, which
is where the id comparison against live DB state happens, so the importer raises it
as a thrown sync error (an `InvalidOperationException` naming the id); the transaction
rolls back and nothing is mutated. The CLI `sync` command already maps
`InvalidOperationException`/`DbException` to exit `3` (its operational-failure class),
so a collision surfaces as exit `3`, NOT the exit `1` config-validation class. This is
deliberate: the collision is a live-state conflict discovered at sync time, not a
static config error found at load, so it belongs with the operational failures a
`sync` can hit against the database. The thrown error is distinct from a config-load
`Error` diagnostic (which is caught before any DB connection and exits `1`).

### D10: Wire field names `organisation`/`vendor` are kept this ticket (explicit non-goal)

`Scope.organisation`, `RequirementScope.organisation`, `VendorScope.vendor`,
`EvidenceCollector.vendor`, and `IntegrationConnection.vendor` keep their current
YAML field names even though each now names a typed asset id. Renaming them to a
generic `asset`/`subject` field belongs to the later scope-generalization ticket
(#92), which reworks the scope tables as a set; doing it here would widen the blast
radius with no capability gain. The evidence-ingest payload field `organisation_id`
is likewise kept and treated internally as an asset subject id. Recorded here as a
firm non-goal rather than an open question.

### D11: Missing required edge is a non-blocking Warning, not an Error

A declared `Vendor` with no `owner`, and a `Machine` with no `parent`, are visible
to no caller under the fail-closed read model (D5): with no owner/parent anchor there
is no grant that can reach them. Requiring the edge (a hard `Error`) was considered
and rejected in favor of a non-blocking `Warning`, for three reasons:

- Consistency: the issue makes a dangling `parent`/`owner` a Warning, never a hard
  failure, so one uncoordinated writer cannot wedge a `sync`. A missing edge is a
  strictly weaker version of the same "unresolvable read anchor" problem;
  escalating it to Error while dangling is tolerated is incoherent.
- No safety loss: fail-closed already makes a mis-anchored asset invisible, not
  over-exposed. The risk is silent invisibility, which a visible Warning cures - the
  same treatment dangling gets - without blocking the sync.
- Requiredness is per-type and fuzzy. A `Company` is a legitimate parent-less root,
  and a `Department` may sit directly under a Company, so "parent required" cannot
  apply uniformly. The Warning is emitted only where the absence is almost certainly
  an author error: a declared `Vendor` with no `owner`, and a `Machine` with no
  `parent`. It is NOT emitted for a Company/Department with no parent.

### D12: Retarget the app-managed org CRUD and authz assignment at `assets`, keep the write-time guards

The web app has a second, non-gitops write path into the org and vendor tables: the
`PUT`/`DELETE /organisations` endpoints (`ComplianceWriteEndpoints`) backed by
`MySqlComplianceWriteStore`, and org-role assignment backed by
`MySqlAuthzAdministrationStore`. Both read and write the `organisations`/`vendors`
tables directly and break the moment migration 019 drops those tables. They MUST be
retargeted at `assets`:

- `MySqlComplianceWriteStore.UpsertOrganisationAsync`/`DeleteOrganisationAsync`
  INSERT/UPDATE/DELETE `organisations`; retarget them at `assets` filtered to
  `type IN ('Company','Department')`, writing `source = 'declared'`, `type` = the
  authored kind, and `parent` where they wrote `parent_id`. Their child-count,
  scope-count, and requirement-scope-count pre-delete guards, the parent-existence
  check (`ExistsAsync("organisations", ...)`), the self-parent check, the
  `WouldFormCycleAsync` walk, and the `LockedParentChanged` lock all read
  `organisations` and must read `assets` filtered to the Company/Department subset
  instead. The org-existence checks in the scope-disposition and
  requirement-scope-disposition writes (`UpsertScopeDispositionAsync` and
  `UpsertRequirementScopeDispositionAsync`, each `ExistsAsync("organisations",
  organisation)`) also name the dropped `organisations` table in their query TEXT
  and MUST likewise be retargeted at `assets` filtered to
  `type IN ('Company','Department')`, so an app write cannot bind a scope or
  requirement-scope to a non-Company/Department asset. Only the query text is
  wrong: the authored org id value resolves to a merged asset row unchanged, but a
  query naming `organisations` fails at runtime once migration 019 drops that table.
  The standard-existence and requirement-existence checks (against `standards` and
  `requirements`) and the `LockedOwnerChanged` scope-owner lock (which reads
  `organisation_id` from the `scopes`/`requirement_scopes` tables, both retained)
  are unchanged. Where the write store touches vendor data, retarget it at
  `type = 'Vendor'`.
- `MySqlAuthzAdministrationStore.AssignOrganisationRoleAsync` validates the target org
  with `ExistsAsync("organisations", "id", organisationId)`; retarget that at `assets`
  filtered to `type IN ('Company','Department')` so an org-role can only be assigned on
  a Company/Department asset.

Keep the write-time invariants. These app-managed writes stay STRICTER than the
gitops sync path, on purpose. D2 relaxes gitops sync to only WARN on a dangling or
cyclic `parent` (config authoring must not wedge on one uncoordinated writer). The
app CRUD endpoints are a different contract: a human editing one org through the UI,
where a self-parent, a cycle, a dangling parent, or deleting an org that still has
children or scopes is an immediate authoring error to reject at the write, not a
tolerated warning to reconcile later. So the acyclic guard (`WouldFormCycleAsync`),
the parent-exists guard, and the no-delete-with-children/scopes guards are RETAINED as
app-level hard errors against `assets`. This is an intentional split: gitops
sync-time tolerance (warn) versus app write-time strictness (reject). The web
endpoints `PUT`/`DELETE /organisations` keep working, now against `assets`.

Tests: persistence tests cover org create/update/delete and the retained guards
against the merged table, and the authz assignment validation against `assets`; a web
test covers the `PUT`/`DELETE /organisations` endpoints end to end on the merged
schema.

### D13: One id space assumes disjoint pre-migration ids (pre-production)

Merging three primary-key spaces - `organisations.id`, `vendors.id`, and `asset.id` -
into one `assets.id` can abort on a duplicate-key collision if, for example, an org
slug equals a vendor slug or a declared slug equals a discovered ULID. Pre-production
there is no data contract, so the migration assumes the three id spaces are DISJOINT
and does not attempt to rename or reconcile a collision. The `INSERT ... SELECT` copy
steps (2-4) are deterministic and collision-safe only under that assumption: a
collision surfaces as a duplicate-primary-key error that fails the migration loudly
rather than silently merging two distinct subjects into one row. The requirement this
places on seeds and fixtures is that ids are disjoint across the three kinds; the
hand-migrated in-repo fixtures satisfy it (org slugs, vendor slugs, and machine ULIDs
do not overlap). A migration integration test asserts the copy of disjoint ids
succeeds and that a deliberately colliding fixture makes 019 fail on the duplicate key
(so the assumption is checked, not merely stated). This is separate from D9, which
guards the sync-time collision of a declared slug against a live discovered ULID; D13
is the migration-time collision across the three source tables.

## Exact file changes (grouped by project)

### Freeboard.Core (MIT, references nothing)

- `Assets/AssetKind.cs`: extend `AssetKind` to `Company`, `Department`, `Machine`,
  `Vendor`; add `AssetSource { Declared, Discovered }`. Keep `AssetState`.
- `GitOps/ConfigModel.cs`: add `KindAsset` to `GitOpsSchema`; remove
  `KindOrganisation` and `KindVendor`; add an `Asset` record (apiVersion, kind,
  id, title, type, source, parent, owner, and the discovered-only fields);
  remove the `Organisation` and `Vendor` records; retarget `Scope.Organisation`,
  `RequirementScope.Organisation`, `VendorScope.Vendor`, and
  `EvidenceCollector.Vendor` docs to name a typed asset id (field names may stay).
  Update `GitOpsConfig` to carry `Assets` and drop `Organisations`/`Vendors`.
- `GitOps/ConfigLoader.cs`: replace the `Organisation`/`Vendor` schema-key sets
  and switch arms with an `Asset` arm; update the unknown-kind message. Reject an
  authored discovered-only key (`identity_kind`, `identity_value`, `state`,
  `first_seen`, `last_seen`) on an `Asset` document at the loader's key-set check
  (where unknown-field detection already runs, on the parsed keys before
  deserialization) as a distinct `Error` naming the field. This has to be a
  presence check on the parsed key set: the loader deserializes YAML to records
  first, after which an omitted field and an authored-blank field are
  indistinguishable, so a validator running on the records cannot see presence.
- `GitOps/ConfigValidator.cs`: replace `ValidateOrganisations` /
  `ValidateOrganisationParents` and `ValidateVendors` with `ValidateAssets`
  producing the id set the scope/vendor-scope/collector phases consume; add the
  type token, source token, `source: discovered` rejection, parent/owner mutual
  exclusivity, parent/owner target-type and carrier-type rules, the dangling
  `parent`/`owner` WARNING, and the missing-required-edge WARNING (declared Vendor
  with no owner, Machine with no parent; see D11). Keep the acyclic hard-error out
  (see D2). The authored-discovered-only-field rejection is NOT here: it is a
  loader-level presence check (see the ConfigLoader note), because presence is not
  observable once the config has been deserialized to records.
- `GitOps/Diagnostic.cs`: add `DiagnosticSeverity` and a `Severity` field
  (default `Error`); redefine `ConfigResult.IsValid` as "no Error diagnostics".

### Freeboard.Persistence (MIT)

- `Migrations/019_asset_unification.sql`: the merge migration (see Migration Plan).
- `AssetReadModels.cs`: extend `AssetRow` (add `Type`, `Source`, `Parent`,
  `Owner`; replace `OrganisationId` with `Parent`). Add a declared-asset read
  shape if needed for the vendor register/owner.
- `ComplianceReadModels.cs`: keep `OrganisationRow`; add `Owner` to `VendorRow`;
  update `SoaInputs`/`SoaDrilldownInputs` construction sources.
- `IComplianceStore.cs` / `MySqlComplianceStore.cs`: project
  `GetOrganisationsAsync` from `assets WHERE type IN ('Company','Department')`,
  `GetVendorsAsync` from `assets WHERE type = 'Vendor'` (carry `owner`), and the
  counts. No interface signature break beyond the added `VendorRow.Owner`.
- `IAssetStore.cs` / `MySqlAssetStore.cs`: read from `assets`; filter discovered
  reads by `source = 'discovered'` where the write path expects a machine. Keep the
  org-scoped source-read predicate against `a.parent` (was `a.organisation_id`) so a
  cross-org `asset_source` row cannot surface another org's machine (D8).
- `MySqlComplianceWriteStore.cs`: retarget `UpsertOrganisationAsync`/
  `DeleteOrganisationAsync` and their guards (child/scope/requirement-scope
  pre-delete counts, parent-exists, self-parent, `WouldFormCycleAsync`,
  `LockedParentChanged`) from `organisations` at `assets` filtered to
  `type IN ('Company','Department')`; also retarget the org-existence checks in
  `UpsertScopeDispositionAsync` and `UpsertRequirementScopeDispositionAsync`
  (`ExistsAsync("organisations", organisation)`) at the same Company/Department
  asset subset, since their query text names the dropped table; retain the acyclic
  and no-delete-with-children/scopes guards as app-level hard errors (D12).
- `MySqlAuthzAdministrationStore.cs`: retarget the org-existence check in
  `AssignOrganisationRoleAsync` from `organisations` at `assets` filtered to
  `type IN ('Company','Department')` (D12).
- `MySqlAssetWriteStore.cs`: retarget INSERT/UPDATE/SELECT from `asset` to
  `assets`, write `source = 'discovered'` and `type = 'Machine'`, write `parent`
  where it wrote `organisation_id`. The `asset_source` join is retargeted at
  `assets` (see Migration Plan for the FK).
- `GitOps/ImportPlan.cs`: add `AssetRowPlan` (declared assets, parent/owner
  normalized to null-if-blank); remove `OrganisationRowPlan` and the vendor
  `DomainRow` path (or repoint them at assets). No parent-before-child ordering:
  `assets.parent` has no FK (D2/D4), so upsert and delete need no topo sort, and
  `OrderParentBeforeChild` is dropped for assets.
- `GitOps/MySqlGitOpsImporter.cs`: upsert declared assets; replace the org and
  vendor prunes with one source-guarded declared-asset prune (after the
  referencing-row prunes for scopes, requirement-scopes, vendor-scopes,
  evidence-collectors, integration-connections, and the org role assignments);
  keep the `authz_organisation_role_assignments` prune keyed on declared
  Company/Department ids. Before any write, fail the sync with an Error if a
  declared id collides with an existing `source = 'discovered'` id (D9).

### Freeboard (web, MIT + EE consumer)

- Vendor register page (`Pages/Compliance/Vendors.cshtml.cs`): narrow vendor rows
  by `owner` membership in the accessible set (drop global readability). The org
  selector, SoA, and org-scoped pages need no change because `OrganisationRow` is
  preserved.
- `Compliance/ComplianceEndpoints.cs`: narrow the `/vendors` and `/vendor-scopes`
  reads by vendor `owner` membership in the caller's accessible-org set, replacing
  the current global reads so a hidden vendor leaks neither its id nor its exception
  justifications (D5). No other read surface changes: the discovered-machine reads
  keep their shape retargeted at `assets`, and no general asset listing is added.

### Freeboard.CLI (MIT, cross-platform, EE-free)

- `VendorCommands.cs` and the API vendor read: reflect owner-narrowed vendor rows
  so the CLI register matches the web (parity). `GitOpsCommands.cs`: update the
  summary/planned-state printers to list `Asset` counts by `type` instead of
  organisations/vendors, and print warnings without failing.

### Fixtures and docs

- `examples/gitops/*` and `examples/fixture-corp/*`: rewrite `kind: Organisation`
  and `kind: Vendor` documents as `kind: Asset` with `type` and `source:
  declared`, and add `owner` to vendors. Update the config-format doc page.

## Migration Plan

`019_asset_unification.sql`, forward-only and NOT idempotent, matching the repo
convention (015 and 018 document the same "NOT atomically replay-safe" property).
MySQL DDL implicit-commits per statement and the runner records the
`schema_migrations` version only after the whole file succeeds
(`MySqlMigrationRunner.ApplyOneAsync`), so a mid-file crash cannot be recovered by a
naive re-run and the migration does not attempt guards the runner cannot honor.
Pre-production hard cutover:

1. Create `assets` (unified columns from D1/D2; PK `id`; `CHECK` for parent/owner
   mutual exclusivity; a unique index `(parent, identity_kind, identity_value)` that
   carries forward v1's per-org discovered-machine dedup, now keyed on `parent` - see
   D8 for the null-parent behavior). No secondary org-scope column: D8 relaxes
   `asset_source` to a simple `asset_id -> assets(id)` FK that references the primary
   key alone, so no composite unique key is needed.
2. Copy `organisations` -> `assets` as `type = kind`, `source = 'declared'`,
   `parent = parent_id`, `owner = NULL`, discovered-only columns null. Steps 2-4 are
   plain `INSERT ... SELECT` into the fresh `assets` table and assume the three source
   id spaces are disjoint (D13); a colliding id fails on the duplicate primary key
   rather than merging two subjects, which is the intended pre-production behavior.
3. Copy `vendors` -> `assets` as `type = 'Vendor'`, `source = 'declared'`,
   `parent = NULL`, `owner = NULL` (declared owner is re-authored on the next
   `sync`), discovered-only columns null.
4. Copy `asset` (discovered machines) -> `assets` as `type = 'Machine'`,
   `source = 'discovered'`, `parent = organisation_id` (the "rewrite scalar to
   parent"), carrying `identity_kind`, `identity_value`, `state`, `hostname`,
   `first_seen_at`, `last_seen_at`, `retired_at`, `created_at`.
5. Retarget `asset_source` (D8): drop the composite FK `fk_asset_source_asset`;
   add a simple `asset_source.asset_id -> assets(id)` FK `ON DELETE RESTRICT`.
   `asset_source` keeps its `organisation_id` column and its
   `(organisation_id, source, external_id)` uniqueness key; cross-org isolation of
   sources moves to query filtering.
6. Re-point all six downstream enforced FKs (the complete verified set, D7): drop
   `fk_scopes_organisation`, `fk_requirement_scopes_organisation`,
   `fk_authz_org_role_assignments_org`, `fk_vendor_scopes_vendor`,
   `fk_evidence_collectors_vendor`, and `fk_integration_connections_vendor`; re-add
   each against `assets(id)` `ON DELETE RESTRICT`. Existing rows keep their ids,
   which now resolve to the copied asset rows, so no rows are orphaned.
7. Drop `organisations` and `vendors` and the old `asset` table (their data now
   lives in `assets`). This drop only succeeds if step 6 re-pointed every FK; a
   missed FK (for example `requirement_scopes` or `integration_connections`) would
   block the drop, which is why D7 enumerates the complete verified set.

Rollback and recovery: none automatic (forward-only, pre-production), the same
posture as 015 and 018. The migration is NOT idempotent - a re-run after a partial
apply can fail on an already-created table or already-dropped constraint, because
MySQL 8.4 has no universal `IF NOT EXISTS`/`IF EXISTS` guard for every statement
used here and the runner does not wrap the file in a transaction. Operational
recovery is the repo convention: restore the pre-migration database and re-run the
whole migration from a clean state. Because it is pre-production there is no data to
preserve, so a restore-and-rerun is always available.

Fixtures are hand-migrated in the same change so `gitops validate`/`sync` and the
integration tests exercise the new kind end to end.

## Risks / Trade-offs

- [Dropping the hard acyclic/dangling org error weakens an authoring guardrail] ->
  Mitigation: the resolution walks already cycle-guard; surface dangling and
  cyclic `parent`/`owner` as a visible non-blocking warning in CLI and web so
  authors still see the problem, they just are not blocked.
- [`asset_source` cross-org isolation currently rests on a DB composite FK
  (`(asset_id, organisation_id)`); rewriting `organisation_id` to a tolerated,
  FK-free, nullable `parent` (D2) means the composite FK cannot survive] ->
  Resolution (D8): relax to a simple `asset_id -> assets(id)` FK and enforce
  cross-org isolation of sources by query filtering. Trade-off: a DB-enforced
  guarantee becomes a query-enforced one; the reviewer should confirm the source
  read paths all filter by organisation.
- [A declared config authoring an id that collides with an existing discovered
  ULID would rewrite the discovered row on upsert] -> Mitigation (D9): the importer
  raises a thrown sync error before any write when a declared id collides with a live
  discovered id, rolling back the transaction (exit 3, operational); covered by an
  integration test asserting no mutation.
- [A `sync` that forgets the `source = 'declared'` guard wipes the discovered
  inventory] -> Mitigation: the source-guarded prune is the single delete path for
  assets, covered by a dedicated integration test asserting discovered rows
  survive a declared-only sync and an empty-config sync.
- [Re-pointing three FKs plus the `authz` assignment reference in one migration]
  -> Mitigation: `INSERT ... SELECT` preserves ids so no reference dangles; a
  migration integration test asserts every retargeted FK resolves after 019.
- [Read-model parity drift between web and CLI vendor narrowing] -> Mitigation:
  parity is an acceptance rule; both consume the same owner-narrowed read and are
  tested.

## Migration verification / testing strategy

- Core unit tests: `Asset` load/validate (type, source, parent/owner exclusivity,
  target-type and carrier rules, `source: discovered` rejection, unknown-field
  rejection, dangling warning not error, missing-required-edge warning not error -
  Vendor with no owner and Machine with no parent, no warning for a parent-less
  Company/Department).
- `ImportPlan` unit tests: declared-asset flattening (no parent-before-child order,
  since `assets.parent` has no FK), null-if-blank parent/owner.
- Core unit test: a declared config authoring a discovered-only field
  (`identity_kind`, `identity_value`, `state`, `first_seen`, `last_seen`) is an
  `Error`, distinct from the `source: discovered` rejection.
- CLI unit tests: `validate`, `apply --dry-run`, and `sync` print `Warning`
  diagnostics on the valid path and still exit 0 (a warnings-only config is not
  silent).
- MySQL integration tests (gated on `FREEBOARD_TEST_DB`): migration 019 applies on
  a 001-018 database and every one of the six retargeted FKs (D7) resolves and the
  old tables are gone; the copy of disjoint ids succeeds and a deliberately colliding
  fixture fails 019 on the duplicate primary key (D13); declared-only sync
  hard-removes an absent declared asset and leaves discovered assets untouched
  (including empty-config sync); dangling parent/owner does not fail sync; a declared
  id colliding with an existing discovered id fails the sync with no mutation (D9);
  cross-org isolation of `asset_source` holds via the `a.parent = @Org` read
  predicate (a source row under another org does not surface the machine) and
  discovered dedup still works on `(parent, identity_kind, identity_value)` (D8);
  org create/update/delete and the retained app-level guards (cycle, self-parent,
  no-delete-with-children/scopes) work against the merged `assets` table, and
  org-role assignment validates the target against `assets` (D12); read-access via
  parent chain and vendor owner.
- Web/CLI tests: vendor register narrows by owner and an owner-excluded caller sees
  NEITHER the vendor NOR its vendor-scope justifications (D5); the
  `PUT`/`DELETE /organisations` endpoints work against the merged schema; CLI and web
  agree.
- `dotnet build` and `dotnet test` (unit tier passes with no DB; DB tier when
  `FREEBOARD_TEST_DB` is set).

## Open Questions

The three open questions are resolved as decisions:

- `asset_source` composite FK: resolved in D8 - relax to a simple `asset_id`
  FK; isolation moves to query filtering.
- Null-owner vendor visibility: resolved in D5/D11 - fail-closed (invisible), and a
  declared vendor with no owner emits a non-blocking Warning. A migrated vendor
  starts owner-null in the DB until the next `sync` re-authors it from
  config; the fixtures author `owner`, so validation surfaces no error and the
  vendor becomes visible once synced.
- Wire field names: resolved in D10 - keep `organisation`/`vendor` this ticket;
  rename is a scope-generalization (#92) concern.

Remaining tension for reviewers: D8 trades a DB-enforced cross-org isolation
guarantee on `asset_source` for a query-enforced one. Confirm every source read
path filters by organisation before relying on it.
