## Context

This is a follow-on ticket of the object model v2 cutover (decisions in #121),
building directly on the asset unification (#125, merged) that made every scope
subject an `Asset`. v1 (post-asset-unification) records scoping with three kinds and
three tables:

- `Scope` (declared): an organisation asset to a `Standard`, with a disposition.
  Fields `apiVersion, kind, id, title, organisation, standard, disposition`. Persisted
  in `scopes` (`organisation_id`, `standard_id`, unique on the pair; `disposition`;
  `fk_scopes_organisation` retargeted to `assets(id)` by migration `019`,
  `fk_scopes_standard` to `standards(id)`). Read model `ScopeRow`. Read endpoint
  `GET /scopes`. App-writable via `PUT`/`DELETE /scopes`.
- `RequirementScope` (declared): an organisation asset to a `Requirement` (no
  `standard` field; the requirement fixes the standard). Persisted in
  `requirement_scopes` (unique on `(organisation_id, requirement_id)`). Read model
  `RequirementScopeRow`. Read endpoint `GET /requirement-scopes`. App-writable via
  `PUT`/`DELETE /requirement-scopes`.
- `VendorScope` (declared): a vendor asset to exactly one of a `Requirement` or a
  `Control`, with a `disposition` and a `justification` (required on `Out`). Fields
  `apiVersion, kind, id, title, vendor, requirement, control, disposition,
  justification`. Persisted in `vendor_scopes` (nullable `requirement_id`/`control_id`,
  a `CHECK ((requirement_id IS NULL) <> (control_id IS NULL))`, two unique keys
  `(vendor_id, requirement_id)` and `(vendor_id, control_id)`; `fk_vendor_scopes_vendor`
  retargeted to `assets(id)` by `019`). Read model `VendorScopeRow`. Read endpoint
  `GET /vendor-scopes`, narrowed by vendor `owner`. Not app-writable (gitops only).

The three share the same abstraction - a subject, a target, a disposition - already
visible in the `vendor_scopes` shape. The config path is: `ConfigLoader`
(kind-routing + unknown-field detection, per-kind allowed key sets) ->
`ConfigValidator` (per-kind rules; `ValidateAssets` returns `AssetIdSets`
(Company/Department `OrganisationIds`, `VendorIds`) that the scope phases consume) ->
`ImportPlan` (flatten to `ScopeRowPlan`/`RequirementScopeRowPlan`/`VendorScopeRowPlan`)
-> `MySqlGitOpsImporter` (one DML transaction). The `Diagnostic` model already carries
`DiagnosticSeverity { Error, Warning }` and `ConfigResult.IsValid` is "no `Error`
diagnostics" (asset unification added it); dangling asset `parent`/`owner` is the
existing non-blocking `Warning` precedent (`CheckEdgeTarget`, `WarnMissingRequiredEdges`
in `ConfigValidator`). The Statement of Applicability resolver
(`Freeboard/Compliance/StatementOfApplicability.cs`) reads scopes and requirement-scopes
from the store and resolves by nearest-ancestor inheritance (`OrgAncestry`), silently
dropping a scope whose organisation no longer resolves.

Constraints: MIT only (no `Freeboard.Enterprise`); reference graph Core -> nothing,
Persistence -> Core, CLI -> Core+Persistence, web -> Core+Enterprise+Persistence;
`Freeboard.Agent` and `Freeboard.CLI` stay EE-free and cross-platform; no new package
dependency; forward-only migrations discovered by filename ordinal (`MigrationCatalog`),
so a new `020_*.sql` is picked up automatically (the next free ordinal after `019`).

## Goals / Non-Goals

**Goals:**

- One `Scope` kind with a `subject` (asset id), a polymorphic target (exactly one of
  `standard`/`requirement`/`control`), a `disposition`, and a `justification`, replacing
  `Scope`, `RequirementScope`, and `VendorScope`.
- One `scopes` table generalizing the `vendor_scopes` shape: scalar no-FK `subject_id`,
  three nullable target FKs (`ON DELETE RESTRICT`), a single-target `CHECK`, and three
  unique keys.
- Justification required on every `Out`; the Vendor-subject-cannot-target-a-standard
  cross-field rule.
- A dangling `subject` is a non-blocking warning at sync, at SoA resolution, and in the
  web UI, and never fails a sync; the three target references stay hard errors.
- A forward-only migration `020` that merges the three tables and maps org/vendor
  subjects to their asset ids.
- Web and CLI read models in the same change (parity is an acceptance rule).
- MySQL integration tests for the merged schema, the `CHECK`, the uniqueness, the
  migration, and the dangling-subject warning.

**Non-Goals (later v2 tickets or out of scope):**

- Group subjects and group resolution (#123). `subject` accepts any asset id; no `Group`
  kind is added.
- Control-level SoA disposition RESOLUTION for organisation subjects. An org subject MAY
  target a control (stored and read back), but the SoA projection resolves only standard-level
  and requirement-level dispositions, exactly as vendor scopes are not folded into the SoA
  today (D8). This defers only the disposition resolution: the dangling-subject warning scan
  still covers control-target scopes (F-6/D8).
- Unifying the two app-managed disposition write endpoints into one (D6).
- An app-managed write path for vendor-subject scopes (vendors stay gitops-write-only).
- `Person` type, discovery-ingest changes, collector retargeting.

## Decisions

### D1: One `scopes` table generalizing the `vendor_scopes` shape

A single table replaces `scopes`, `requirement_scopes`, and `vendor_scopes`. Columns:

- `id VARCHAR(190)` `utf8mb4_bin`, primary key.
- `api_version VARCHAR(64)`, `title VARCHAR(512)`.
- `subject_id VARCHAR(190)` `utf8mb4_bin` NOT NULL, NO foreign key (D2).
- `standard_id`, `requirement_id`, `control_id`, each `VARCHAR(190)` `utf8mb4_bin`
  NULL, each a foreign key `ON DELETE RESTRICT` to `standards(id)`, `requirements(id)`,
  `controls(id)` respectively.
- `disposition VARCHAR(16)`, `justification TEXT NULL`.
- `created_at`, `updated_at DATETIME(6)`.
- `CONSTRAINT ck_scopes_single_target CHECK
  (((standard_id IS NOT NULL) + (requirement_id IS NOT NULL) + (control_id IS NOT NULL)) = 1)`.
  MySQL evaluates each `IS NOT NULL` as 0/1 and sums them, so exactly one target column
  set passes. This extends the two-way `vendor_scopes` XOR check
  (`(requirement_id IS NULL) <> (control_id IS NULL)`) to three targets.
- Three unique keys: `uq_scopes_subject_standard (subject_id, standard_id)`,
  `uq_scopes_subject_requirement (subject_id, requirement_id)`,
  `uq_scopes_subject_control (subject_id, control_id)`. MySQL treats each `NULL` as
  distinct in a unique index, so a key constrains only the rows whose own target column
  is non-null - the same "partial unique" behavior `vendor_scopes` already relies on.
  This is why they are plain composite unique keys, not filtered/partial indexes (MySQL
  has no partial indexes).
- Secondary keys on `subject_id`, `standard_id`, `requirement_id`, `control_id` for the
  foreign-key and lookup paths.

Alternative considered: keep three tables and add a `type` view. Rejected - it preserves
the three-schema cost the ticket exists to remove and gives later v2 work no single
target.

### D2: `subject_id` is scalar, no foreign key, dangling tolerated

`subject_id` has NO foreign key, unlike today's `scopes.organisation_id` and
`vendor_scopes.vendor_id` (both `fk ... -> assets(id) ON DELETE RESTRICT` after `019`).
This is a deliberate behavior change matching the asset-unification `parent`/`owner`
precedent (a scalar `utf8mb4_bin` column, no FK, dangling tolerated): a subject must be
able to name a discovered asset that a later sync removes or a not-yet-discovered asset,
without wedging the sync or blocking the deletion of the asset. So a scope whose
`subject` no longer resolves is a NON-BLOCKING `Warning` at validation/sync (D4), a
non-blocking notice at SoA resolution (D8), never an error.

The three target columns are the opposite: they keep a real foreign key `ON DELETE
RESTRICT`, so a dangling `standard`/`requirement`/`control` stays a hard error, and a
standard/requirement/control cannot be deleted while a scope targets it (the importer
prunes referencing scopes first, D5). The asymmetry is intentional: the subject side
tolerates asset churn; the target side enforces catalogue integrity.

**Subject-resolution predicate (used by every DB-backed surface).** A `subject` is
RESOLVED when the `assets` table holds a row with that `id` that is a live authorization
anchor, and UNRESOLVED otherwise. Concretely, against the merged `assets` table (migration
`019`), a subject resolves when a row exists with `id = subject_id` AND NOT (`source =
'discovered'` AND `state = 'Retired'`); it is unresolved when no `assets` row has that id
OR the row is a discovered asset in the `Retired` state. The exact tokens are the schema's:
`source` holds `'declared'`/`'discovered'` (`019`), and `state` holds `'Seen'`/`'Retired'`
(`MySqlAssetWriteStore` constants `StateSeen`/`StateRetired`), non-null only for discovered
assets. A retired discovered machine keeps its `assets` row (retirement is a state change,
not a delete), so "row exists" alone is not resolution - the retired branch makes a retired
subject unresolved, matching the issue's "retirement counts as unresolved". The predicate is
one definition shared by the DB-accurate sync dangling check (D8), the SoA resolution notice
(D8), and the read-visibility fail-closed rule (D6). For declared `Company`/`Department`/`Vendor`
subjects (`state` null) the retired branch is inert - a declared asset resolves whenever its row
exists - but for a discovered `Machine` subject (now first-class, D6/D8) the retired branch is
live: a `Retired` discovered machine keeps its row yet is unresolved, so its scope warns at sync
and hides on read. Only `Group` subjects, which have no read/resolution branch here, defer to
#123.

**Supported-subject boundary (which subject types are first-class here).** The schema is
deliberately general - `subject_id` is a no-FK scalar that accepts ANY asset id, which is the
whole point of D2. THREE subject families are readable-and-warned first-class in THIS change:
the org tree (`Company`/`Department`), `Vendor`, and `Machine` (and any other parent-anchored
asset). The read-visibility predicate (D6) has a branch for each - an org subject through the
accessible-organisation set, a vendor subject through its `owner` edge, and a machine subject
through its `parent` ancestry into that set - so a `Machine`-subject scope is visible to a
caller who can see its parent org and hidden from one who cannot (it is no longer
resolved-but-invisible). The DB-accurate sync warning (D8) and the SoA notice reason about all
three correctly, including a discovered or `Retired` `Machine` subject, because they apply the
D2 predicate against the persisted asset set. Only `Group` subjects remain deferred to #123: a
scope naming a group still loads and persists (D2's general subject) and warns at sync via the
DB check when the group id resolves to no asset, but no group-membership read/resolution branch
is built here. We deliberately do NOT add a validator error forbidding any subject type (that
would defeat D2's general subject). A declared `Machine` subject IS authorable: a
`type: Machine`, `source: declared` asset with a `parent` validates today
(`AssetValidationTests` loads exactly such an asset with no warning), so a config MAY name a
declared `Machine` as a scope `subject`. Discovered `Machine` subjects additionally arrive by
ingest (`source: discovered` is rejected when authored, per asset-model). Either way a `Machine`
subject is a first-class scope subject here: the read and warning branches above handle it. This
boundary is intentional and visible.

### D3: The `(target_kind, target_id)` discriminator is rejected

A generic two-column polymorphic target - a `target_kind` enum plus one `target_id`
column - was considered and rejected. One `target_id` column cannot carry a foreign key
to three different tables (`standards`, `requirements`, `controls`), so it would lose the
`ON DELETE RESTRICT` catalogue-integrity guarantee that the three-nullable-columns design
keeps, and it would push the "target row exists" check entirely into application code.
The three-nullable-columns-plus-`CHECK` pattern is already proven in `vendor_scopes`, so
generalizing it (adding a `standard_id`) is the smaller, lower-risk change and preserves
real database referential integrity on the target.

### D4: The unified Scope kind, validation, and the generalized rules

The Core model collapses the three records into one `Scope`:

```
apiVersion: freeboard.dev/v1alpha1
kind: Scope
id: ...
title: ...
subject: <asset id>                    # was organisation / vendor
standard: <id> | requirement: <id> | control: <id>   # exactly one
disposition: In | Out
justification: <required on Out, optional on In>
```

`ConfigModel` gains one `Scope` record (`apiVersion, kind, id, title, subject,
standard, requirement, control, disposition, justification`) and drops
`RequirementScope` and `VendorScope`; `GitOpsSchema` keeps `KindScope` and drops
`KindRequirementScope`/`KindVendorScope`; `GitOpsConfig` carries one `Scopes` list.
`ConfigLoader` keeps one `Scope` allowed-key set
(`apiVersion, kind, id, title, subject, standard, requirement, control, disposition,
justification`) and one switch arm, and drops the other two.

`ConfigValidator.ValidateScopes` enforces (all `Error` unless noted):

- required `id`, `title`, `subject`, `disposition`.
- exactly one of `standard`/`requirement`/`control` is set (reusing the boolean-count
  idea from `ValidateVendorScopes`: none, or more than one, is the same diagnostic).
- `disposition` is `In` or `Out` (`TryParseDisposition`).
- `Out` requires a non-blank `justification` - generalized from `VendorScope` to every
  scope (the check fires only when the disposition parsed to `Out`).
- the target reference resolves: `standard` in the standard id set, `requirement` in the
  requirement id set, `control` in the control id set. A dangling target is an `Error`
  (the target keeps a real FK).
- cross-field: if `subject` resolves to a `Vendor` asset AND the target is `standard`,
  `Error` (a vendor has no standard-level disposition). Org subjects may target any of
  the three. This uses the `AssetIdSets.VendorIds` set `ValidateAssets` already returns.
- a dangling `subject` (names no asset in the resolved set) is a NON-BLOCKING `Warning`,
  not an error, generalizing `CheckEdgeTarget`. Because the subject may be missing at
  validation, the cross-field vendor rule is only evaluable when the subject resolves; a
  dangling subject warns and skips the cross-field check.
- duplicate `id`; and pair uniqueness per `(subject, standard)`, `(subject,
  requirement)`, and `(subject, control)` (three tuple sets, mirroring the existing
  vendor-scope pair sets).

`AssetIdSets` grows a use: today it exposes `OrganisationIds` (Company/Department) and
`VendorIds`. The unified validator needs the full asset id set (to decide "subject
resolves at all" for the dangling warning) plus `VendorIds` (for the cross-field rule).
`ValidateAssets` returns the union already implicitly; expose an `AllIds` set (or reuse
the seen-id set) so the subject-dangling check does not special-case type.

### D5: Importer merges to one whole-set-replace of `scopes`

`ImportPlan` replaces the three row-plan types with one `ScopeRowPlan(Id, ApiVersion,
Title, Subject, string? Standard, string? Requirement, string? Control, Disposition,
string? Justification)` (the unused target sides and a blank justification normalized to
null via `NullIfBlank`, as `VendorScopeRowPlan` does today). Core validation guarantees
exactly one target is set, so the plan carries a valid row.

`MySqlGitOpsImporter` replaces `UpsertScopesAsync` + `ReplaceRequirementScopesAsync` +
`ReplaceVendorScopesAsync` with one `ReplaceScopesAsync` (delete-all then insert the
unified set). Whole-set replace is required because the table has a primary key plus
three unique keys, so a pair-swap that keeps ids cannot be upserted safely - the same
reason `vendor_scopes` full-replaces today. The single scope replace runs BEFORE the
absent-standard/requirement/control deletes and BEFORE the declared-asset prune, so no
`RESTRICT` target FK blocks a catalogue deletion (the referencing scope rows are gone
first). The `subject_id` has no FK, so the declared-asset prune is never blocked by a
scope and a removed asset simply leaves the scope with a dangling subject (a tolerated
warning), consistent with the asset-unification prune model.

### D6: Read endpoints collapse to one `/scopes`; writes stay two retargeted routes

Read: `GET /api/v1/freeboard/scopes` returns the unified row `{ id, title, subject,
standard, requirement, control, disposition, justification }` (exactly one of the three
target ids set, the others null; `justification` null when unset). `GET
/requirement-scopes` and `GET /vendor-scopes` are removed. Narrowing generalizes the
old rules into one subject-readability rule with a branch per parent-anchored subject family:

- a Company/Department (org-tree) subject is readable when it is in the caller's
  accessible-organisation set;
- a Vendor subject is readable when its `owner` (a Company/Department asset) is in that set -
  read through the `owner` edge, as `/vendor-scopes` did;
- a Machine (or other parent-anchored asset) subject is readable when its parent-org ancestry
  resolves into that set - walk the asset's `parent` chain to the org node it hangs under
  (reusing the same `OrgAncestry` walk the SoA uses) and admit the scope when that org node is
  accessible.

A subject that is missing, dangling, or resolves to no live asset (the D2 predicate) - or whose
resolving anchor (org node, vendor `owner`, or machine parent-org) is outside the accessible set
- hides the scope (fail-closed). This preserves the old `/scopes` org-narrowing and the old
`/vendor-scopes` owner-narrowing, adds the machine parent-ancestry branch so a `Machine`-subject
scope is no longer invisible to a caller who can see its parent org, and closes the same leak: a
hidden subject's exception `justification` never surfaces. The branches reuse the existing
`OrgAncestry`/accessible-set machinery; no general graph resolver is built.

**Read seam feeding the branches (D6/D7).** A bare `subject_id` cannot drive a vendor `owner`
or a machine `parent`-ancestry branch - the endpoint has no way to reach the subject's edges.
So the store's unified scope read SHALL `LEFT JOIN assets ON assets.id = scope.subject_id` and
carry, per scope, the subject's resolved `type` (`Company`/`Department`/`Vendor`/`Machine`, or
none when the subject resolves to no asset row), `source`, `state`, `parent`, and `owner`. That
is exactly enough to evaluate, server-side, BOTH the D2 subject-resolution predicate (a row
exists and is not a retired discovered asset) and the readability branch per family: an org
subject via the accessible set, a vendor subject via its `owner`, a machine (or other
parent-anchored) subject by taking its `parent` org id from the enriched read and walking
`OrgAncestry` from there (OrgAncestry still walks the org dictionary; the machine branch just
seeds that walk from the machine's parent-org). The enriched subject fields are narrowing inputs
only: the `/scopes` response projects just the public fields (`id, title, subject, standard,
requirement, control, disposition, justification`), so no subject `type`/`state`/`parent`/`owner`
leaks to the client. A subject that resolves to no asset row or a retired discovered machine (the
D2 predicate) fails closed (the scope is omitted). The machine branch admits the scope ONLY when
the inclusive ancestry of its `parent` org - built by the SAME `OrgAncestry.InclusiveAncestors`
walk the SoA uses, seeded from the machine's `parent` org id taken from the enriched read -
INTERSECTS the caller's accessible-organisation set. This is exact PARITY with the org-subject
rule, which admits an org subject whose id is in that same accessible set: the accessible set is
the caller's read-subtree union (a downward closure of granted roots), so a descendant department
under a granted company is already a member, and intersecting a subject's inclusive ancestry with
that closure yields the same admit/deny as a plain membership test on the anchoring org. An admit
therefore happens ONLY through a REAL accessible org in the chain, which gives the correct
fail-closed behaviour without any rooted-vs-dangling or rooted-vs-cycle distinction:

- A dangling `parent` (an id resolving to no asset row, hence to no org) is never a member of the
  accessible set, so the intersection is naturally empty and the scope is omitted (fail-closed).
  No rooted-vs-dangling signal is needed - a broken anchor simply fails to intersect.
- A `Machine` hanging under an org the caller CAN access stays VISIBLE even when that org's own
  parent is dangling or broken: the accessible parent-org is itself in the intersection, so it
  grants visibility, and hiding the machine would wrongly deny a legitimate admin. The broken
  grandparent is a separate org-tree integrity warning, not a visibility question.
- A `parent` cycle is already bounded by the existing visited-set guard in `InclusiveAncestors`
  (the walk returns each distinct node once and stops on the first repeat). An admit happens only
  if a real accessible org sits in that bounded chain, which is correct; a cycle among orgs none of
  which is accessible yields an empty intersection and is omitted.

`OrgAncestry.InclusiveAncestors` is reused UNCHANGED: this change adds NO rooted-vs-cycle signal
and no general graph resolver, and leaves the existing callers (org-subject narrowing and the SoA
projection) untouched.

Write: the app-managed `PUT`/`DELETE /scopes` and `PUT`/`DELETE /requirement-scopes`
endpoints keep their routes, permissions (`compliance.scope.write`,
`compliance.requirement-scope.write`), and SoA-disposition semantics, retargeted onto the
unified table. They write `subject_id` plus the standard (resp. requirement) target,
enforce uniqueness on `(subject, standard)` (resp. `(subject, requirement)`), and now
reject an `Out` with no `justification` (the DTOs gain an optional `justification`). The
org-delete guard and `LockedOwnerChanged` concurrency check read the owning org from
`scopes.subject_id` (formerly `scopes.organisation_id` and
`requirement_scopes.organisation_id`, now one table filtered by target column). Vendor
subjects remain gitops-write-only. Keeping two write routes rather than unifying them
bounds the blast radius and preserves the two distinct permissions; unifying is a
non-goal.

**Write-route target isolation (an authz boundary, not just a query detail).** Before the
merge, `DeleteScopeAsync` and `DeleteRequirementScopeAsync` each issued
`DELETE FROM <its-own-table> WHERE id=@Id`, so the table name enforced the boundary. After
the merge both routes address one `scopes` table by global `id`; an unqualified
`DELETE FROM scopes WHERE id=@Id` (or an unqualified upsert) would let `compliance.scope.write`
delete or retarget a requirement-target row, or a control-target gitops-only row, and vice
versa - an id-based authz-boundary bypass. So each route SHALL be confined to its own target
column, but a PUT MUST still distinguish a brand-new id (create) from a wrong-kind id (reject).
A naive target-filtered selector (`... WHERE id=@Id AND standard_id IS NOT NULL`) cannot make
that distinction: it is null both when the id is absent (create) and when the id is a
different-target row (reject), so it would wrongly 404 every new-scope PUT. The write store
therefore branches on the GLOBAL id - a lookup NOT filtered by target column:

- No `scopes` row has that `id` at all -> CREATE: INSERT a new row of this route's own target
  kind (the standard route a `standard_id` row, the requirement route a `requirement_id` row).
  This is the ordinary new-scope PUT.
- A row has that `id` AND its populated target column matches the route (a `standard_id` row on
  the standard route, a `requirement_id` row on the requirement route) -> authorize against its
  owning subject and UPDATE it.
- A row has that `id` but its populated target column is a DIFFERENT kind (a requirement- or
  control-target row on the standard route, or a standard- or control-target row on the
  requirement route) -> NOT-FOUND (404); a PUT MUST NOT convert an existing row from one target
  kind to another.

The DELETE stays target-column-scoped - the standard route deletes
`... WHERE id=@Id AND standard_id IS NOT NULL`, the requirement route
`... WHERE id=@Id AND requirement_id IS NOT NULL` - and returns NOT-FOUND when zero rows are
affected (the id is absent or is a wrong-kind row). Control-target rows populate neither
route's target column, so they are unreachable by both a PUT and a DELETE.

**The same target-column confinement binds the ENDPOINT's authz selectors, not only the store.**
`UpsertScopeAsync`/`UpsertRequirementScopeAsync` read the stored owning subject in-handler, and
`StoredScopeOrgSelector`/`StoredRequirementScopeOrgSelector` read it in the DELETE authz filter,
today via a `GetScopesAsync`/`GetRequirementScopesAsync` lookup by UNQUALIFIED `id`. After the
merge there is one `GetScopesAsync` returning rows of ALL target kinds, so an unfiltered `id`
lookup would load a wrong-kind row (a requirement- or control-target row on the standard route,
or a standard- or control-target row on the requirement route) and authorize - or 403 - the
caller against THAT row's owning subject, returning 403 (or silently authorizing) instead of the
specified 404. So each route's stored-owner lookup AND its DELETE authz selector SHALL consider
ONLY a row whose OWN target column matches the route: the standard route only a row whose
`standard` target is set (`GetScopesAsync().FirstOrDefault(s => s.Id == id && s.Standard is not
null)`), the requirement route only a row whose `requirement` target is set. A same-id row of a
different target kind is treated as NOT-FOUND for that route: the lookup/selector finds no stored
owner, so the handler takes the new/absent-row path - a PUT authorizes only the requested subject
and the store's global-id branch then reaches the wrong-kind-404, and a DELETE's
target-column-scoped statement affects no row and returns 404. A wrong-kind `id` therefore never
authorizes or 403s against another kind's row, and the requirement route's stored-owner lookup
moves from the removed `GetRequirementScopesAsync` onto the one `GetScopesAsync` filtered to
`requirement`-target rows. The fix is minimal: filter the existing in-handler lookup and DELETE
selector by the route's target column.

The wrong-route outcome is NOT-FOUND, not a conflict. Today `WriteResult`
(`IComplianceWriteStore.cs`) carries only `Error` and `IsConflict`, and the endpoint result
mapping (`ComplianceWriteEndpoints.RunAsync`) maps success to 204, `IsConflict` to 409, and
otherwise to a 422 problem body - there is NO not-found path, so an unqualified wrong-kind
delete or PUT would currently fall through to a duplicate-PK 409 rather than a 404. This
change adds a not-found result to `WriteResult` (a `bool IsNotFound` / `WriteResult.NotFound()`
factory) that the write stores return when a PUT's global-id lookup finds an existing wrong-kind
row, or a DELETE's target-column-scoped statement affects no row, and `RunAsync` maps it to
`Results.NotFound()` (404). A brand-new id is NOT a not-found: the PUT creates it (the global-id
lookup finds no row and the store INSERTs). A DELETE on the wrong route returns 404; a PUT that
addresses a wrong-kind id returns 404, never a silent retarget. Control-target rows stay
GitOps-write-only, reachable by neither app route. This is stated in the `compliance-write`
delta and the `authz-enforcement` delta, and is covered by a cross-route authz/web test (a
caller holding only one write permission cannot delete or convert the other route's target-kind
row) AND by a direct MySQL write-store integration test asserting the production DELETE carries
`standard_id IS NOT NULL` / `requirement_id IS NOT NULL` (so a wrong-kind id is a no-op) and the
PUT branches create-vs-wrong-target on the global id - the test MUST include an ordinary new-id
create case alongside the wrong-target-isolation cases so the fix cannot regress normal creation
into a 404; a web test with fake write stores cannot prove the SQL.

Alternative considered: keep three read endpoints as filtered views over the unified
table. Rejected - it preserves the three-surface cost, and an org subject targeting a
control would have no endpoint. One `/scopes` is less total code and matches the one-Scope
model.

### D7: Read-model parity, CLI, and the vendor register

`ScopeRow` is the ONE enriched internal read carrier and becomes
`ScopeRow(Id, Title, Subject, string? Standard, string? Requirement, string? Control,
Disposition, string? Justification, string? SubjectType, string? SubjectSource,
string? SubjectState, string? SubjectParent, string? SubjectOwner)`; `RequirementScopeRow` and
`VendorScopeRow` are removed. The first eight fields are the public/wire shape; the trailing
five (`SubjectType`, `SubjectSource`, `SubjectState`, `SubjectParent`, `SubjectOwner`) are the
subject-narrowing fields carried by the `LEFT JOIN assets` on `subject_id`, are server-side only,
and are NEVER serialized. `IComplianceStore` exposes one `GetScopesAsync` returning these unified
`ScopeRow`s (ordered by `id`), each enriched with the subject's resolved `type`, `source`,
`state`, `parent`, and `owner` so the web `/scopes` endpoint can run the D6 subject-readability
branches and the D2 fail-closed check server-side, then project ONLY the eight public fields into
the wire shape (the `ApiScope` shape: `id, title, subject, standard, requirement, control,
disposition, justification`). The endpoint already projects each row into an explicit response
object, so the five narrowing fields on `ScopeRow` never reach the client. The
SoA-input reads read the unified table, and the
per-kind counts collapse the three scope counts into one `scopes` count. The web
`/scopes` endpoint and the CLI both consume this one read (parity is an acceptance rule).
The CLI API client's `ListVendorScopesAsync` becomes `ListScopesAsync` returning the
unified `ApiScope` shape; the CLI `vendor` register command filters the unified scopes to
vendor-subject rows to render each vendor's exceptions with justifications (owner-narrowed
by the endpoint), so the register output is unchanged. `GitOpsCommands` prints one `Scope`
count and lists unified scopes in the planned state.

### D8: Dangling subject warns at SoA resolution and in the UI

The SoA resolver today groups scopes by `scope.Organisation` and iterates the org list,
so a scope whose subject is gone is silently dropped. This change surfaces it. The
store's SoA-input read additionally returns the full unified scope set and the resolvable
asset id set (or the resolver is handed both), the set computed with the DB-accurate D2
subject-resolution predicate (a row exists and is not a retired discovered asset). The
resolver flags any unified scope - standard-target, requirement-target, OR control-target -
whose `subject` is unresolved as a non-blocking notice ("rule targets a resource that does
not currently exist"), rendered on the `/compliance/statement-of-applicability` page without
failing the projection. The warning scan covers ALL target kinds; only the disposition
RESOLUTION stays limited to standard-target and requirement-target org scopes, so a
control-target scope contributes no node disposition but its dangling subject still warns on
the page. The JSON endpoint keeps its shape; the warning is a
page-level notice (the web has no existing per-record warning surface, so this adds a
modest one). The notice is GENERIC and uses the issue's exact wording - "rule targets a
resource that does not currently exist" - and does NOT name the scope id or the unresolved
subject id to an ordinary caller: the unresolved subject has no authorization anchor to
check readability against, so disclosing its id (or the scope id) to any authenticated SoA
viewer would leak. Any detailed-id surface would be system-admin-only and is out of scope
here. This resolves the earlier contradiction with the fail-closed `compliance-web-read`
rule (which hides a dangling scope entirely), keeping both surfaces from disclosing an
unanchored id. The warning is strictly about a subject that resolves to no live asset at all
(the D2 predicate): a scope whose subject DOES resolve - an org node, a vendor, or a
resolvable machine - is not a dangling-subject warning; it simply may not contribute an
org-node disposition (a vendor or control-target scope is not consulted by the
standard/requirement resolution). Control-target org scopes are stored but not resolved into a
node disposition by the SoA (a non-goal), YET a control-target org scope whose subject is
unresolved DOES warn on the SoA page, because the warning scan covers every target kind. This
removes the earlier contradiction (the proposal said the warning surfaces in the web UI while
a non-goal exempted control-target scopes): only control-level disposition RESOLUTION is
deferred, never the dangling-subject warning. The warning reaches an operator on both the
write path (sync) and the read path (SoA page) for every target kind.

**The dangling-subject warning has two producers with a clean Core-vs-DB split (this
REVERSES the earlier decision to omit the importer-side DB warning).** The signal must be
DB-accurate wherever a database is available, because only the DB sees discovered and retired
`Machine` subjects; `Freeboard.Core` has no database and sees only the AUTHORED asset set.

- DB-less paths (`validate`, `apply --dry-run`): the Core `Warning` (D4), computed against the
  AUTHORED asset id set (the YAML), printed by the CLI via the existing `PrintWarnings`. This is
  a superset check: it warns whenever a `subject` is not authored in the config. For a config
  that authors all its subjects (declared orgs and vendors) it is exact; it cannot distinguish a
  subject that resolves only to a DB-only discovered `Machine` (a false positive) or a
  discovered subject that is `Retired`. That is acceptable for the DB-less paths, whose only
  ground truth is the YAML, and a `Warning` still exits 0.
- Sync path (and the SoA read): the DB-accurate D2 predicate applied against the PERSISTED asset
  set. On `sync` the importer runs the dangling-subject DB check (a `LEFT JOIN assets` applying
  the D2 predicate, including the retired branch) after all its writes but BEFORE the transaction
  commits, inside the same DML transaction, so the query observes the final post-write asset
  state; it captures the set of scope subjects that do not resolve into the import result and
  THEN commits. Running the check inside the transaction preserves the importer's all-or-nothing
  outcome: a failure of the check rolls the whole import back, rather than leaving a committed
  import whose `ImportAsync` then throws (which would exit the CLI non-zero on an already-committed
  sync, or, if caught, silently drop the required warning). This ordering is part of the fixed
  import sequence (D5). The CLI prints one non-blocking warning per unresolved subject on the sync
  success path. This is the new import-result warnings channel: `IGitOpsImporter.ImportAsync`
  returns an import result carrying the unresolved-subject list rather than `void`. It covers a
  discovered or retired `Machine` subject that Core cannot evaluate.

On the sync path the DB check is AUTHORITATIVE for the scope subject: the CLI SHALL NOT emit the
Core authored-set scope-subject `Warning` (a potential false positive) for a subject that
resolves in the DB. Concretely, the sync flow suppresses the Core scope-subject-dangling
warnings (identifiable as the scope-subject-resolution diagnostics) and prints the importer's
DB-accurate result instead; the other Core warnings (dangling `parent`/`owner`, cycles) still
print. So a healthy discovered `Machine` subject draws no false sync warning, and a retired or
truly-absent subject warns at sync with the DB as ground truth. The SoA page notice uses the
same DB-accurate predicate (above). The warning therefore reaches an operator accurately on both
the write path (sync, CLI stderr) and the read path (SoA page) for org, vendor, and machine
subjects alike.

### D9: Migration `020` merges three tables, maps subjects to asset ids

`020_scope_generalization.sql`, forward-only and NOT idempotent, matching the repo
convention (`015`/`018`/`019` document the same "NOT atomically replay-safe" property;
MySQL DDL implicit-commits per statement and the runner records `schema_migrations` only
after the whole file succeeds). Pre-production hard cutover:

1. `CREATE TABLE scopes_v2` with the unified columns, the single-target `CHECK`, the
   three unique keys, and the three target foreign keys `ON DELETE RESTRICT`; no
   `subject_id` foreign key. The three target FKs MUST use collision-free constraint names
   `fk_scopes_v2_standard`/`fk_scopes_v2_requirement`/`fk_scopes_v2_control`, NOT
   `fk_scopes_standard`: InnoDB FK constraint names are schema-wide, and the old `scopes`
   table (dropped only in step 5) still carries `fk_scopes_standard` from migration `007`
   (migration `019` re-pointed only `fk_scopes_organisation`), so reusing that name would
   fail `CREATE TABLE scopes_v2` with a duplicate-constraint error. `RENAME TABLE` (step 6)
   does not rename constraints, so the `_v2` names survive the rename unchanged; the
   migration test asserts the three target FKs exist under those names after `020`.
2. `INSERT ... SELECT` from `scopes`: `subject_id = organisation_id`, `standard_id =
   standard_id`, `requirement_id`/`control_id` NULL, `disposition`, timestamps carried. The
   legacy `scopes` table (migration `007`) has NO `justification` column, so there is nothing
   to carry: the migration SETS `justification` to the marker only for an `Out` row and NULL
   otherwise, via the exact expression
   `justification = CASE WHEN disposition = 'Out' THEN 'Migrated legacy Out rule;
   justification was not recorded and requires review.' ELSE NULL END`. This closes the
   migrate-before-sync window (`system migrate` commits before any importer runs) so
   `GET /scopes` cannot return an `Out` with no rationale. See D10 / Divergence 1.
3. `INSERT ... SELECT` from `requirement_scopes`: `subject_id = organisation_id`,
   `requirement_id = requirement_id`, `standard_id`/`control_id` NULL, `disposition`. The
   legacy `requirement_scopes` table (migration `009`) ALSO has no `justification` column, so
   it uses the SAME source-less marker expression as step 2:
   `justification = CASE WHEN disposition = 'Out' THEN '<marker>' ELSE NULL END`.
4. `INSERT ... SELECT` from `vendor_scopes`: `subject_id = vendor_id`, `requirement_id`
   and `control_id` carried as-is, `disposition`. Unlike the two org tables, `vendor_scopes`
   (migration `011`) HAS a real `justification` column, so this step PRESERVES the recorded
   value and substitutes the marker only for a blank `Out`, via
   `justification = CASE WHEN disposition = 'Out' AND NULLIF(TRIM(justification), '') IS NULL
   THEN '<marker>' ELSE justification END`. Vendor scopes already require a justification on
   `Out`, so the marker branch is a defensive backstop, not the expected path.
5. `DROP TABLE scopes; DROP TABLE requirement_scopes; DROP TABLE vendor_scopes.` These
   are leaf tables (nothing foreign-keys INTO them), so the drops need no FK re-pointing;
   dropping each table drops its own outbound FKs (`fk_scopes_organisation`,
   `fk_requirement_scopes_organisation`, `fk_vendor_scopes_vendor`, and the target FKs,
   which `scopes_v2` re-creates).
6. `RENAME TABLE scopes_v2 TO scopes.`

Subject mapping (issue: "map org/vendor subjects to their `Asset` ids"): because
asset-unification already made `scopes.organisation_id`, `requirement_scopes.organisation_id`,
and `vendor_scopes.vendor_id` hold the asset id (the org/vendor rows became `assets`
rows keeping their ids, and the FKs point at `assets`), the copy is a direct column
rename - `organisation_id`/`vendor_id` are already asset ids, no lookup or translation is
needed.

Id-space collision: merging three primary-key spaces (`scopes.id`, `requirement_scopes.id`,
`vendor_scopes.id`) into one `scopes.id` can abort on a duplicate key if two source rows
share an id. Pre-production there is no data contract, so the migration assumes the three
id spaces are DISJOINT and does not reconcile a collision: an `INSERT ... SELECT` collision
fails on the duplicate primary key, failing the migration loudly rather than silently
merging two distinct scopes. The hand-migrated fixtures satisfy disjointness (the `scope-`,
`rs-`, `vs-` id prefixes do not overlap). A migration integration test asserts disjoint ids
copy successfully and a deliberately colliding fixture fails `020` on the duplicate key.

Rollback and recovery: none automatic (forward-only, pre-production), the same posture as
`015`/`018`/`019`. Recovery from a failed run is restore-and-rerun; because it is
pre-production there is no data to preserve.

### D10: The generalized `Out`-requires-justification and pre-existing `Out` rows

The justification-on-`Out` rule now applies to every scope, so existing `Scope Out` and
`RequirementScope Out` fixtures with no justification become invalid config. Two
consequences:

- Fixtures/tests are hand-migrated in this change: every existing `Out` document without a
  justification gains one (in the repo today: `scope-products-soc2`,
  `rs-products-firewalls-01-out`, `rs-fixture-corp-eng-updates-05-out`). Vendor-scope `Out`
  fixtures already carry justifications and copy unchanged.
- The DB does NOT constrain justification (it stays `TEXT NULL`, matching `vendor_scopes`);
  the `Out`-requires-justification rule is validator/write-API enforced. So the migration
  copies pre-existing `Out` rows without failing; a later `sync` of a config that still
  lacks the justification fails validation at load, which is the intended forcing function
  to author the rationale.
- To close the migrate-before-sync window (`system migrate` commits the copied rows before
  any importer runs, so `GET /scopes` could otherwise return an `Out` with no rationale),
  migration `020` writes a provenance marker - the exact text "Migrated legacy Out rule;
  justification was not recorded and requires review." - into copied `Out` rows. The two org
  tables (`scopes`, `requirement_scopes`) have NO `justification` column, so every copied `Out`
  row from them gets the marker unconditionally; `vendor_scopes` DOES have a justification
  column, so its real value is preserved and the marker is substituted only when the recorded
  justification is blank on an `Out` row (D9 steps 2-4). The marker is self-cleaning: the
  importer whole-set-replaces `scopes` on the next `gitops sync` (D5), overwriting the migrated
  rows with the hand-migrated fixtures' real, contextual justifications (task 7). This reverses
  the original Divergence 1 decision (see below).

## Exact file changes (grouped by project)

### Freeboard.Core (MIT, references nothing)

- `GitOps/ConfigModel.cs`: replace the `Scope`, `RequirementScope`, `VendorScope` records
  with one `Scope` record (`subject`, `standard`, `requirement`, `control`, `disposition`,
  `justification`); drop `KindRequirementScope`/`KindVendorScope` from `GitOpsSchema`; carry
  one `Scopes` list on `GitOpsConfig`, drop `RequirementScopes`/`VendorScopes`.
- `GitOps/ConfigLoader.cs`: keep the `Scope` allowed-key set (add `subject`, `requirement`,
  `control`, `justification`; drop `organisation`, `vendor`), keep one `Scope` switch arm,
  drop the `RequirementScope`/`VendorScope` key sets and arms, and drop those two from the
  unknown-kind message enumeration.
- `GitOps/ConfigValidator.cs`: replace `ValidateScopes`/`ValidateRequirementScopes`/
  `ValidateVendorScopes` with one `ValidateScopes` implementing D4 (exactly-one-target,
  `Out`-requires-justification, target reference resolution as `Error`, subject dangling as
  `Warning`, Vendor-subject-no-standard cross-field, duplicate id, three pair-uniqueness
  sets). Expose the full asset id set from `ValidateAssets` for the subject-dangling check.
  Make the subject-dangling `Warning` discriminable (a distinct diagnostic kind/code) so the
  CLI `sync` path can suppress it in favour of the importer's DB-accurate result (D8).
- No `Diagnostic.cs` change beyond, if needed, a discriminable warning kind for the
  scope-subject-dangling case (the severity model already exists).

### Freeboard.Persistence (MIT)

- `Migrations/020_scope_generalization.sql`: the merge migration (D9).
- `ComplianceReadModels.cs`: replace `ScopeRow`/`RequirementScopeRow`/`VendorScopeRow`
  with one enriched `ScopeRow(Id, Title, Subject, string? Standard, string? Requirement,
  string? Control, Disposition, string? Justification, string? SubjectType, string?
  SubjectSource, string? SubjectState, string? SubjectParent, string? SubjectOwner)` (the
  13-field carrier defined in D7): the first eight fields are the public/wire (`ApiScope`) shape
  and the trailing five subject-narrowing fields are populated by the `GetScopesAsync` `LEFT JOIN
  assets`, are server-side only, and are NEVER serialized; reshaping `ScopeRow` and removing
  `RequirementScopeRow` forces `SoaInputs` and `SoaDrilldownInputs` (whose `RequirementScopes`
  field is typed `RequirementScopeRow`) to drop that field and read the requirement-layer from
  the one `Scopes` list; drop `RequirementScopes`/`VendorScopes` from `ComplianceCounts`,
  leaving one `Scopes` count.
- `GitOps/ImportPlan.cs`: replace the three row-plan types with one `ScopeRowPlan` (D5);
  keep one `ScopeIds` helper.
- `IComplianceStore.cs` / `MySqlComplianceStore.cs`: one `GetScopesAsync` over the unified
  table; `GetStatementOfApplicabilityInputsAsync` and
  `GetStatementOfApplicabilityDrilldownInputsAsync` stop reading `requirement_scopes` and
  select the unified scopes (target = standard for the standard-layer, target = requirement
  for the requirement-layer) and return the asset id set for the dangling check (D8);
  collapse the three scope counts into one.
- `MySqlComplianceWriteStore.cs`: retarget `UpsertScopeDispositionAsync`/`DeleteScopeAsync`
  and `UpsertRequirementScopeDispositionAsync`/`DeleteRequirementScopeAsync` at the unified
  `scopes` table (write `subject_id` plus the standard/requirement target), enforce the
  `(subject, standard)` / `(subject, requirement)` uniqueness, reject `Out` with no
  justification, and read the owning org from `scopes.subject_id` in the org-delete guard
  and `LockedOwnerChanged` check.
- `GitOps/MySqlGitOpsImporter.cs`: replace the three scope upsert/replace steps with one
  `ReplaceScopesAsync`, ordered before the absent-catalogue deletes and the declared-asset
  prune (D5). After all writes but BEFORE commit, inside the same import transaction, run the
  DB-accurate dangling-subject `LEFT JOIN assets` check (D2 predicate) against the final
  post-write asset state, capture the unresolved-subject set into the import result, then commit
  (a check failure rolls the import back); change `IGitOpsImporter.ImportAsync` to return an
  import result carrying those warnings instead of `void` (D8).

### Freeboard (web, MIT + EE consumer)

- `Compliance/ComplianceEndpoints.cs`: replace the `/scopes`, `/requirement-scopes`, and
  `/vendor-scopes` handlers with one `/scopes` returning the unified row, narrowed by
  subject readability with the org, vendor-`owner`, AND machine-`parent`-ancestry branches
  (D6), reusing the existing `OrgAncestry`/`IOrgAccess` machinery. The vendor register page's
  exception data reads from the unified scopes filtered to the vendor subject. The SoA JSON
  endpoint in this file calls `StatementOfApplicability.Resolve(inputs.Organisations,
  inputs.Scopes, inputs.Requirements, inputs.RequirementScopes, standardId)` and MUST move to the
  reshaped signature (drop the `RequirementScopes` argument, F-18). The `/compliance/status`
  handler MUST emit one `scopes` count and drop `requirementScopes`/`vendorScopes` from both the
  healthy and degraded/all-null `persisted` shapes (F-17), since `ComplianceCounts` no longer
  carries those two counts.
- `Compliance/ComplianceWriteEndpoints.cs`: keep `PUT`/`DELETE /scopes` and
  `/requirement-scopes`; the DTOs gain an optional `justification` and use `subject` for the
  subject field; the handlers reject `Out` with no justification. Filter each route's
  stored-owner authorization lookup by the route's target column (D6/F-1): the PUT in-handler
  stored-owner read (`UpsertScopeAsync`, `UpsertRequirementScopeAsync`) and the DELETE authz
  selector (`StoredScopeOrgSelector`, `StoredRequirementScopeOrgSelector`) read from the one
  `GetScopesAsync` filtered to the route's own target column (the removed `GetRequirementScopesAsync`
  folds into it), so a same-id wrong-kind row yields no stored owner and the write resolves to 404
  rather than 403-ing against or silently authorizing from the wrong-kind row's owning org.
- `Compliance/StatementOfApplicability.cs` + `Pages/Compliance/StatementOfApplicability.cshtml(.cs)`:
  the `Resolve` and `ResolveDrilldown` signatures drop their second `IReadOnlyList<RequirementScopeRow>`
  parameter (the requirement layer now comes from the one unified scopes list); feed the
  resolver the unified scopes and the asset id set, surface the dangling-subject
  non-blocking notice on the page (D8). The resolution logic (`OrgAncestry`, nearest-ancestor)
  is unchanged.
- `Pages/Compliance/ControlDetail.cshtml.cs`: update the
  `ResolveDrilldown(... inputs.RequirementScopes ...)` call to the new signature (it will not
  compile otherwise, since `SoaDrilldownInputs.RequirementScopes` is removed).
  `Pages/Compliance/ControlDetailProjection.cs` is NOT touched: it maps already-resolved
  `SoaRequirementNode`/`SoaControlNode` output types and never references `RequirementScopes`
  or the `Resolve`/`ResolveDrilldown` signatures, so the signature change does not reach it.
- `Evidence/EvidenceIngestEndpoints.cs`: update the
  `StatementOfApplicability.Resolve(soa.Organisations, soa.Scopes, soa.Requirements,
  soa.RequirementScopes, standardId)` call to the new signature (it will not compile otherwise,
  since `SoaInputs.RequirementScopes` is removed and the requirement layer now comes from the
  one unified scopes list).
- `Pages/Compliance/Vendors.cshtml.cs`: read vendor exceptions from the unified scopes
  (vendor-subject rows), owner-narrowed as before.

### Freeboard.CLI (MIT, cross-platform, EE-free)

- API client (`IFreeboardApiClient.cs` / `HttpFreeboardApiClient.cs`): `ListVendorScopesAsync`
  becomes `ListScopesAsync` returning `ApiScope(Id, Title, Subject, string? Standard, string?
  Requirement, string? Control, Disposition, string? Justification)`.
- `VendorCommands.cs`: render each vendor's exceptions from the unified scopes filtered to
  its subject (parity with the web register).
- `GitOpsCommands.cs`: print one `Scope` count and list unified scopes in
  validate/apply/sync summaries. `validate`/`apply --dry-run` print the DB-less Core
  scope-subject warnings as today; on `sync`, print the importer result's DB-accurate
  unresolved-subject warnings and suppress the Core scope-subject warnings (D8), while the
  other Core warnings still print via `PrintWarnings`.

### Fixtures and docs

- `examples/gitops/*` and `examples/fixture-corp/*`: rewrite `kind: Scope`,
  `kind: RequirementScope`, and `kind: VendorScope` documents as the unified `kind: Scope`
  (subject + one target), and add a `justification` to every `Out` that lacks one (D10).
- `docs/gitops.md`: replace the three scope sections with one unified `Scope` section
  (subject, the three targets, the cross-field rule, the generalized justification rule, the
  dangling-subject warning); update the noun table and supported-kinds list; update the
  persistence and read-endpoint sections (one `scopes` table, one `/scopes` endpoint).

## Risks / Trade-offs

- [Merging three primary-key spaces can collide on a shared id] -> Mitigation (D9): the
  migration assumes disjoint ids and fails loudly on a duplicate primary key rather than
  silently merging two scopes; a migration test asserts both the disjoint-copy success and
  the colliding-fixture failure. Fixtures use disjoint `scope-`/`rs-`/`vs-` prefixes.
- [Dropping the subject foreign key weakens a guardrail: a scope can now dangle] ->
  Mitigation (D2/D4/D8): a dangling subject is surfaced as a visible non-blocking warning at
  sync (CLI stderr) and at SoA resolution (page notice); reads are fail-closed (an unreadable
  or unresolved subject hides the scope). This matches the asset `parent`/`owner` posture the
  cutover already adopted.
- [The `Out`-requires-justification rule now applies to org scopes that never needed one] ->
  Mitigation (D10): the in-repo fixtures are hand-migrated to add justifications; the DB does
  not constrain justification, so the migration copies pre-existing `Out` rows without failing.
  Because the legacy org scope tables have no justification column, migration `020` writes the
  provenance marker into every copied org `Out` row (and into a blank vendor `Out`), so no
  `Out` row is readable without a rationale in the migrate-before-sync window; the importer's
  whole-set replace then overwrites the marker with the fixture's real justification on the
  first sync, and only a later sync of a still-incomplete config fails validation (the intended
  forcing function).
- [Collapsing the three read endpoints breaks API consumers] -> Accepted: pre-production hard
  cutover, no compatibility layer; the removed endpoints' data is available on the unified
  `/scopes`, and web+CLI move together.
- [The three-way `CHECK` relies on MySQL casting `IS NOT NULL` to an integer] -> Mitigation:
  MySQL 8.4 evaluates boolean expressions to 0/1 in arithmetic (the existing two-way
  `vendor_scopes` XOR check relies on the same coercion); a persistence integration test
  writes a zero-target and a two-target row directly and asserts the `CHECK` rejects both.
- [The SoA gains a new dangling-subject notice surface with no existing web precedent] ->
  Mitigation (D8): keep it a modest page-level notice, not a per-record UI. The sync-path
  surface is the DB-accurate importer result (D8), which the CLI prints while suppressing the
  DB-less Core scope-subject warning on that path; `validate`/`apply --dry-run`, which have no
  database, keep the Core authored-set `Warning`.

## Migration Plan

Apply `020_scope_generalization.sql` via `system migrate` (or `gitops sync --migrate`) as
the forward-only, non-idempotent merge in D9. It is picked up automatically by
`MigrationCatalog` as the next ordinal after `019`. There is no automatic rollback;
recovery from a partial apply is restore-and-rerun (pre-production, no data to preserve).
Fixtures are hand-migrated in the same change so `gitops validate`/`sync` and the
integration tests exercise the unified kind end to end.

## Verification / testing strategy

- Core unit tests: unified `Scope` load/validate happy path and every D4 rule -
  exactly-one-target (none, one, two, three), disposition token, `Out`-requires-justification
  (and `In` omits it), target dangling is an `Error`, subject dangling is a `Warning` (not an
  error, does not fail validation), the Vendor-subject-targets-a-standard `Error`, duplicate
  id, and the three pair-uniqueness cases. Rewrite the existing `RequirementScopeValidationTests`
  and `VendorScopeValidationTests` and the loader/validator scope tests onto the unified kind.
- `ImportPlan` unit tests: unified flattening, null-if-blank target sides and justification.
- CLI unit tests: `validate`/`apply --dry-run`/`sync` print a subject-dangling `Warning` on
  the valid path and still exit 0; the unified `Scope` count and listing appear.
- MySQL integration tests (gated on `FREEBOARD_TEST_DB`): migration `020` applies on a
  001-019 database; the unified `scopes` table exists with the `CHECK`, the three unique keys,
  and the three target FKs, and the two legacy tables are gone; the copy of disjoint ids
  succeeds and a deliberately colliding fixture fails `020` on the duplicate primary key;
  the `CHECK` rejects a zero-target and a two-target row written directly; each unique key
  rejects a duplicate `(subject, target)` pair; a whole-set-replace sync round-trips and
  hard-removes an absent scope; a dangling subject does not fail sync (warning only); the
  standard/requirement/control target `RESTRICT` holds (a targeted catalogue row cannot be
  dropped while a scope references it, and the importer prunes the scope first).
- Web/CLI tests: the unified `/scopes` endpoint returns the unified row narrowed by subject
  readability (an org-excluded and an owner-excluded caller each see neither the scope nor its
  `Out` justification); the removed endpoints are gone; the SoA page renders the
  dangling-subject notice without failing; the write endpoints reject an `Out` with no
  justification; CLI and web agree (parity).
- SoA tests: standard-level and requirement-level resolution over the unified table produce
  the same projection as before (nearest-ancestor inheritance unchanged).
- `dotnet build` and `dotnet test` (unit/web tier with no DB; DB tier when
  `FREEBOARD_TEST_DB` is set); `freeboard gitops validate examples/fixture-corp`; markdownlint
  on changed docs.

## Open Questions

Resolved as decisions:

- Polymorphic target shape: D1/D3 - three nullable target columns with a `CHECK`, not a
  `(target_kind, target_id)` discriminator.
- Subject foreign key: D2 - scalar no-FK, dangling tolerated (a `Warning`), matching
  `parent`/`owner`.
- Read surface: D6 - one `/scopes`, narrowed by subject readability; the two write routes are
  retargeted but kept.
- Control-target org scopes in the SoA: D8 / Non-Goals - stored and read back but not resolved
  by the SoA this ticket.

Remaining tension for reviewers: D6 collapses three read endpoints into one and removes
`/requirement-scopes` and `/vendor-scopes`. Confirm the subject-readability predicate preserves
the exact fail-closed behavior of both prior narrowings (org-accessible for org subjects,
`owner`-in-accessible for vendor subjects) so no scope or `Out` justification leaks.

## Plan synthesis: sources and divergence resolution

This design merges two independently produced plans: Plan A (the OpenSpec artifacts this
change started from) and Plan B (a second reviewer's plan). They agree on the architecture -
one `scopes` table generalizing the `vendor_scopes` shape; a scalar no-FK `subject_id`; three
nullable target FK columns `ON DELETE RESTRICT`; an exactly-one-target `CHECK`; three
composite UNIQUE keys relying on MySQL NULL-distinctness; rejecting a `(target_kind,
target_id)` discriminator; a whole-table-replacement merge migration `020`; collapsing to one
`Scope` kind, one read model, and one `GET /scopes`; hand-migrated fixtures; SoA
control-target org scopes deferred; and a discovered-`Retired` subject counting as
unresolved. The synthesis keeps that shared core and resolves the divergences below. (Note:
the `D1`..`D10` labels in the Decisions section above are this design's decision numbers; the
`Divergence 1`..`Divergence 6` labels here are the reviewer divergences and use a separate
numbering - they do not line up one-to-one.)

Ideas that came from each source:

- Plan A: the full decision set `D1`-`D10` above (table shape, no-FK subject, discriminator
  rejection, unified validator, whole-set-replace importer ordering, one read endpoint with
  two retained write routes, read-model/CLI parity, SoA dangling notice, migration `020`
  mechanics, and the `Out`-requires-justification generalization). Plan A is the baseline.
- Plan B: sharpened several shipping concerns - the DB-accurate resolution predicate including
  the `Retired` branch, the fail-closed exposure of a dangling subject's id/justification, the
  explicit whole-set-replace-not-upsert reasoning, and the request to audit the authz
  permission surface. These are folded in below where they strengthen the baseline.

### Divergence 1: migration justification backfill (Plan A: none; Plan B: provenance marker)

Resolution (reversed): adopt Plan B's provenance marker after all. Migration `020` writes the
marker "Migrated legacy Out rule; justification was not recorded and requires review." into
copied `Out` rows. The two org source tables (`scopes` from `007`, `requirement_scopes` from
`009`) have no `justification` column at all, so every copied org `Out` row gets the marker
unconditionally (there is no source value to carry); `vendor_scopes` (from `011`) does have a
`justification` column, so its recorded value is preserved and the marker is used only for a
blank `Out` (D9 steps 2-4, D10). `justification` still stays `TEXT NULL` (no DB constraint);
the marker is data, not schema.

Why the reversal: the original "no marker" choice missed that `system migrate` runs
SEPARATELY from `gitops sync`. MySQL DDL and the copy `INSERT`s commit when `020` finishes,
before any importer runs, so between `system migrate` and the first `gitops sync` the copied
legacy `Out` rows are live and readable. In that window `GET /scopes` would return an `Out`
row with no rationale - the very leak the generalized `Out`-requires-justification rule
exists to prevent - even though the in-repo fixtures carry real justifications the importer
has not yet applied. The marker closes that window with a truthful, reviewable caption. It
remains low-liability and self-cleaning: the importer whole-set-replaces `scopes` on the
first `gitops sync` (design `D5`), overwriting every migrated row with the hand-migrated
fixture's real, contextual justification (task 7), so the synthetic string never persists
past the first sync and needs no later scrub. A migration integration test asserts a legacy
null-justification `Out` row lands with a non-blank justification. The validator/write-API
`Out`-requires-justification rule (design `D10`) remains the forcing function for future
config edits.

### Divergence 2: authz permission cleanup (Plan B: merge and remove `requirement-scope.write`)

Verified against code: exactly two scope-write permissions exist -
`compliance.scope.write` (`AuthzActions.ComplianceScopeWrite`) and
`compliance.requirement-scope.write` (`AuthzActions.ComplianceRequirementScopeWrite`). There
is NO `compliance.vendor-scope.write` (vendor scopes are gitops-write-only). Both keys appear
in `Freeboard.Core/Authz/AuthzActions.cs`, the seed in
`Freeboard.Persistence/Migrations/010_authorization.sql` (permission rows and `org-owner` /
`compliance-manager` grants), the custom-role allow-list `Freeboard.Core/Authz/AuthzCustomRoles.cs`,
the EE presentation `Freeboard.Enterprise/CustomRolePresentation.cs`, and tests
(`RouteAuthzMetadataTests`, `FakeAuthzStore`, `AuthzCustomRolesTests`, `AuthorizationEngineTests`,
`AuthzIntegrationTests`). They map to the two app-managed write routes `PUT`/`DELETE /scopes/{id}`
and `PUT`/`DELETE /requirement-scopes/{id}`.

Resolution: KEEP both permissions unchanged; do NOT merge or remove
`compliance.requirement-scope.write`. Plan B's H-3 merge is premised on collapsing the two
app-managed write endpoints into one, which is an explicit non-goal here (design `D6`): the
two write routes are retargeted onto the unified table but kept, because they carry distinct
permissions and distinct SoA semantics (a standard-level vs a requirement-level disposition).
While `020` removes the `requirement_scopes` TABLE and the change removes the READ
`/requirement-scopes` endpoint, the WRITE route `PUT`/`DELETE /requirement-scopes/{id}` and
its `compliance.requirement-scope.write` permission remain live, so the permission is still
needed and must not be dropped. Removing it would be a large blast radius (migration seed,
`AuthzActions`, `AuthzCustomRoles`, the EE `CustomRolePresentation`, and the `org-rbac`,
`authz-persistence`, `authz-enforcement`, `authorization-engine`, and `custom-role-designer`
specs, plus their tests) for no capability gain, because the requirement-level write still
exists and still needs an authz key. This is the lower-liability option that satisfies
acceptance. Consequently no permission-merge step is added to migration `020`, and no
`authz-persistence` delta is written (no permission row or authz-schema row is removed). An
`authz-enforcement` delta IS written, but for a different reason than Plan B's permission
merge: two of its ratified requirements DESCRIBE the now-removed per-target scope tables and
read endpoints and are falsified by this change - "An upsert PUT authorizes both the stored
and the requested organisation" reads the owning org from a `scopes.organisation_id` /
`requirement_scopes.organisation_id` that no longer exists (now `scopes.subject_id`, and the
routes are target-column-scoped, F-1), and "Compliance reads narrow to the caller's
authorized subtree" enumerates the removed `GET /requirement-scopes` read. The delta MODIFIES
those two requirements without touching any permission (see Divergence 4). The
`enterprise-edition.md` one-way rule is respected: `CustomRolePresentation` stays in
`Freeboard.Enterprise` and is not touched.

### Divergence 3: whole-set replace vs per-id upsert in the merged importer

Verified against code: today `MySqlGitOpsImporter` uses `UpsertScopesAsync` (`INSERT ... ON
DUPLICATE KEY UPDATE`) for `scopes`, but uses whole-set replace (`DELETE FROM ...;` then
insert) for `requirement_scopes` (`ReplaceRequirementScopesAsync`) and `vendor_scopes`
(`ReplaceVendorScopesAsync`). The importer's own inline comment already documents why the
latter two must replace rather than upsert: the table has a primary key plus a
`(subject, target)` unique key, so a row that swaps pairs while keeping its id can match the
pair key under `ON DUPLICATE KEY UPDATE` and update the WRONG row.

Resolution (both plans agree, made explicit): the unified `ReplaceScopesAsync` MUST be
whole-set replace (delete-all then insert in the existing DML transaction), never per-id
upsert - the merged table has a primary key plus THREE `(subject, target)` unique keys, so
the pair-swap hazard is strictly larger than on either legacy table. The upsert path
(`UpsertScopesAsync`) is deleted, not generalized. Design `D5` and tasks 3.4 / 4.3 state this
and its reason.

### Divergence 4: spec-capability coverage (Plan A: 8; resolved: 10)

Resolution: 10 capabilities - `gitops-config-format`, `compliance-persistence`,
`organisation-model`, `statement-of-applicability`, `compliance-web-read`, `compliance-write`,
`gitops-cli`, `vendor-register`, `authz-enforcement`, and `asset-model`. Plan A listed 8; a
later review found `authz-enforcement`'s durable text is falsified too (delta below), and a
further review found a second `asset-model` requirement is falsified as well (delta below).
Do NOT add Plan B's other one (`authz-persistence`):

- `authz-enforcement`: DELTA ADDED. Not for Plan B's permission merge (no permission is
  removed, per Divergence 2), but because two ratified `authz-enforcement` requirements are
  falsified by the table merge and endpoint collapse: "An upsert PUT authorizes both the
  stored and the requested organisation" names the owning org column
  `scopes.organisation_id` / `requirement_scopes.organisation_id` (now `scopes.subject_id`,
  and both write routes are target-column-scoped, F-1), and "Compliance reads narrow to the
  caller's authorized subtree" enumerates the removed `GET /requirement-scopes` read (now one
  `GET /scopes`). The delta MODIFIES those two requirements to match the merged table and the
  single read endpoint. No permission is added or removed.
- `authz-persistence`: only warranted if a permission row or an authz-schema row were removed.
  Per Divergence 2 none is, so `authz-persistence`'s text stays true. No delta.
- `asset-model`: DELTA ADDED. asset-model mentions scopes in TWO requirements, and the second
  is falsified by this change. The first, "Asset and asset-source schema and migration",
  DESCRIBES what migration `019` did - it enumerates the six foreign keys `019` re-pointed at
  `assets(id)` (`scopes.organisation_id`, `requirement_scopes.organisation_id`,
  `vendor_scopes.vendor_id`, and three others) and the scenario "Retargeted references resolve
  after the migration". That is a historical statement about `019`, which remains true (`019`
  genuinely re-pointed those FKs; `020` is a separate later step that does not rewrite `019`),
  so it needs no delta. But the SECOND requirement, "Declared assets are synced by config;
  discovered assets are owned by ingest", states a LIVE invariant this change falsifies: "The
  declared-asset removal SHALL be foreign-key-safe: rows referencing a removed asset (scopes,
  requirement-scopes, vendor-scopes, evidence-collectors, integration-connections, org-scoped
  role assignments) SHALL be pruned first." After this change `scopes.subject_id` has NO
  foreign key, so a scope whose subject asset is removed is LEFT DANGLING (a tolerated warning),
  not pruned; and `requirement-scopes`/`vendor-scopes` no longer exist as tables. The delta
  MODIFIES that requirement to drop the two removed tables from the prune enumeration and to
  state the scope subject is a scalar no-FK reference (left dangling, not pruned) while the
  remaining FK-backed references (evidence-collectors, integration-connections, org-scoped role
  assignments) are still pruned first. The header is char-for-char identical to the base. The
  earlier "no asset-model delta" reasoning was WRONG: it inspected only the first requirement
  and missed this second one.

Every capability whose durable spec text this change makes false has a delta; no delta is
added to a capability the change does not touch.

### Divergence 5: dangling-subject read exposure and the strict write path

Resolution (Plan B sharpening folded into Plan A's `D6`): reads are fail-closed. A scope whose
`subject` is unresolved by the D2 predicate - no `assets` row, a retired discovered asset, or
a subject that resolves but is not readable (an org subject outside the accessible set, or a
vendor subject whose `owner` is missing/dangling/outside the set) - is OMITTED from
`GET /scopes` entirely, so neither the subject id nor an `Out` `justification` leaks. The app
write path is the strict counterpart: a `PUT` scope whose `subject` does not resolve to a live
`Company`/`Department` asset is rejected with an RFC 7807 problem body (a 422-class validation
response), because an unresolvable subject has no authorization anchor to write against. The
`compliance-web-read` "Unreadable subject hides the scope and its justification" scenario and
the `compliance-write` "Unresolved scope reference rejected on write" scenario already encode
this; the D2 predicate ties "resolve" to the exact `assets.source`/`state` values.

### Divergence 6: retired-vs-missing resolution predicate

Resolution: unified into the single D2 subject-resolution predicate (see the D2 decision
above), verified against the schema: a subject is unresolved when NO `assets` row has that id
OR the row has `source = 'discovered'` AND `state = 'Retired'`. The tokens are the code's own:
`source` is `'declared'`/`'discovered'` (migration `019`), `state` is `'Seen'`/`'Retired'`
(`MySqlAssetWriteStore.StateSeen`/`StateRetired`), and a retired discovered machine keeps its
row (retirement is a state change, not a delete), which is why "row exists" is insufficient.
The same predicate drives the SoA notice and the read-visibility rule. `Company`/`Department`/
`Vendor` AND `Machine` subjects are first-class here - read and warned, per D2's supported-subject
boundary: for a declared org or vendor subject `state` is null and the retired branch is inert,
while for a discovered `Machine` subject the retired branch is live (a `Retired` discovered
machine keeps its row yet is unresolved). Only `Group` subjects remain deferred to #123: a scope
naming a group still loads, persists, and warns at sync when unresolved, but no group-membership
read/resolution branch is built here. The predicate is already written correctly so it stays right
once group subjects become scopable.

### Endpoint-collapse reconciliation

Both plans collapse the three read endpoints into one `GET /api/v1/freeboard/scopes` and
remove `GET /requirement-scopes` and `GET /vendor-scopes`. Kept. The removed read routes are
named in the `compliance-web-read` delta ("Removed requirement-scope and vendor-scope
endpoints are gone" scenario), and task 5.4 plans a test asserting both removed routes 404.
The WRITE routes `PUT`/`DELETE /requirement-scopes/{id}` are a different surface and are
retained (Divergence 2).

### Reviewer tensions for the next round

- The asset-model "Asset and asset-source schema and migration" sentence a reviewer could
  contest: "That complete set is `scopes.organisation_id`, `requirement_scopes.organisation_id`,
  ... `vendor_scopes.vendor_id`, ...". It reads true as a record of migration `019`, but a
  reader tracing the full `001`-`020` chain will find `requirement_scopes`/`vendor_scopes` gone
  and `scopes.subject_id` FK-free. Confirm the team agrees THIS requirement stays a
  `019`-historical statement (no delta on it) rather than a live-schema invariant needing a
  footnote. (The SEPARATE "Declared assets are synced by config" requirement IS given an
  asset-model delta, Divergence 4, because its FK-safe prune enumeration is a live invariant this
  change falsifies.)
- Divergence 1: confirm no environment carries pre-production scope data outside the in-repo
  fixtures that would want the Plan B provenance marker after all.
- Divergence 2: confirm keeping two write routes / two permissions (vs a future unification)
  is still desired; the requirement-scope permission's continued existence rests on that.
