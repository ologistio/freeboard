## Why

Object model v2 needs one scope. Today three separate kinds record "who is in or
out of what": `Scope` (an organisation asset to a standard), `RequirementScope` (an
organisation asset to a requirement), and `VendorScope` (a vendor asset to one
requirement or control). They carry the same idea - a subject, a target, a
disposition - in three schemas, three validators, three tables, three read models,
and three read endpoints. The asset unification (#125, merged) already made every
subject an `Asset`; the natural next step is one `Scope` kind with a single subject
and a polymorphic target, generalizing today's `vendor_scopes` shape (single subject,
one-of-three target columns) to cover standards, requirements, and controls. This
lets later v2 work (group subjects in #123, control retargeting) bind to one model
instead of reconciling three.

This is a pre-production HARD CUTOVER: there is no data contract to preserve, so the
migration transforms and merges the three tables in place, the in-repo YAML fixtures
are hand-migrated in this change, and there is no dual-kind compatibility layer and
no converter tool. `apiVersion` stays `freeboard.dev/v1alpha1`.

## What Changes

- **BREAKING** Collapse `Scope`, `RequirementScope`, and `VendorScope` into one
  `Scope` kind. The `RequirementScope` and `VendorScope` document kinds are removed.
  A `Scope` now has a `subject` (an asset id), exactly one of `standard`,
  `requirement`, or `control` (the polymorphic target), a `disposition` (`In` or
  `Out`), and a `justification`.
- **BREAKING** Rename the subject field: the old `Scope.organisation` and
  `VendorScope.vendor` become one `subject` field naming any asset id (the field
  rename deferred by the asset-unification change). `RequirementScope.organisation`
  is likewise `subject`.
- Generalize the justification rule: `justification` is REQUIRED on every `Out` scope
  (previously enforced only on `VendorScope`) and optional on `In`. This makes every
  exclusion carry its rationale regardless of target.
- Add one cross-field rule: a `Scope` whose `subject` resolves to a `Vendor` asset
  MAY target only a `requirement` or a `control`, never a `standard` (vendors have no
  standard-level disposition). Org (Company/Department) subjects may target any of the
  three.
- **BREAKING** The `subject` is a scalar reference with NO foreign key, validated at
  write, dangling tolerated: a `subject` that names no asset is a NON-BLOCKING warning
  at sync, at Statement-of-Applicability resolution, and in the web UI ("rule targets
  a resource that does not currently exist" - covers a retired asset and a
  not-yet-discovered one). It never fails a sync. This generalizes the
  scalar-no-foreign-key, dangling-tolerant `parent`/`owner` model from asset
  unification. The three target references (`standard`/`requirement`/`control`) keep a
  real foreign key `ON DELETE RESTRICT` and stay hard errors when dangling.
- **BREAKING** Merge `scopes`, `requirement_scopes`, and `vendor_scopes` into one
  `scopes` table (migration `020`): a scalar `subject_id` (no FK), nullable
  `standard_id`/`requirement_id`/`control_id` (each a foreign key `ON DELETE
  RESTRICT`), `disposition`, and a nullable `justification`. A `CHECK` enforces that
  exactly one target column is set. Three unique keys - `(subject_id, standard_id)`,
  `(subject_id, requirement_id)`, `(subject_id, control_id)` - bound each target pair,
  relying on MySQL treating each NULL as distinct (the pattern today's `vendor_scopes`
  already uses). The three legacy tables are removed. A generic
  `(target_kind, target_id)` discriminator is explicitly rejected because one column
  cannot foreign-key to three tables (see design.md).
- **BREAKING** Collapse the three read endpoints (`/scopes`, `/requirement-scopes`,
  `/vendor-scopes`) into one `GET /api/v1/freeboard/scopes` returning the unified rows
  (`subject`, the one set target, `disposition`, `justification`), narrowed by subject
  readability (an org subject through the accessible-organisation set, a vendor subject
  through the vendor `owner` edge, a machine subject through its `parent` ancestry into that
  set, else hidden fail-closed). The vendor register (web
  page and CLI) reads its exceptions from the unified `/scopes` filtered to
  vendor-subject rows, preserving the owner-narrowed exception display.
- The app-managed disposition writes (`PUT`/`DELETE /scopes` and
  `/requirement-scopes`) are retargeted onto the unified table (writing `subject_id`
  plus the standard resp. requirement target) and now reject an `Out` with no
  `justification`, matching import.
- Web AND CLI read models ship together; parity is an acceptance rule.
- Hand-migrate the in-repo YAML fixtures and tests to the unified `Scope` kind,
  including adding a `justification` to every existing `Scope`/`RequirementScope`
  `Out` document that lacks one (now invalid under the generalized rule).

## Capabilities

### New Capabilities

None. The change reworks existing capabilities; it introduces no new spec folder.

### Modified Capabilities

- `gitops-config-format`: the three scope kinds collapse into one `Scope` kind with a
  `subject` and a polymorphic target; `RequirementScope` and `VendorScope` authoring
  and validation are removed; a dangling `subject` becomes a non-blocking warning while
  target references stay hard errors; the justification-on-`Out` rule generalizes to
  every scope; a Vendor subject targeting a standard is rejected.
- `compliance-persistence`: the `scopes`, `requirement_scopes`, and `vendor_scopes`
  tables merge into one `scopes` table (scalar no-FK `subject_id`, three nullable
  target FKs, single-target `CHECK`, three unique keys); the merge migration `020`
  copies the three source tables and drops them; the importer replaces the whole
  unified scope set; the read store and counts expose one unified scope set.
- `organisation-model`: the `Scope` domain model generalizes from an
  organisation-to-standard mapping to a subject-to-target mapping, and the
  `RequirementScope` domain kind is removed (folded into `Scope` with a requirement
  target); an organisation subject may target a standard, a requirement, or a control.
- `statement-of-applicability`: standard-level and requirement-level resolution now
  read from the unified `scopes` table (a `Scope` targeting the standard, and a `Scope`
  targeting a requirement) instead of two tables; a scope whose `subject` does not
  resolve surfaces a non-blocking warning at resolution time and never fails the
  projection. The resolution semantics (nearest-ancestor inheritance) are unchanged.
- `compliance-web-read`: the `/scopes`, `/requirement-scopes`, and `/vendor-scopes`
  read endpoints collapse into one `/scopes` endpoint over the unified table, narrowed
  by subject readability; `/requirement-scopes` and `/vendor-scopes` are removed.
- `compliance-write`: the scope and requirement-scope disposition writes persist into
  the unified `scopes` table keyed on `subject_id` plus the target column, and reject an
  `Out` disposition with no `justification`.
- `gitops-cli`: `validate`/`sync` referential integrity and round-trip coverage
  re-terms `RequirementScope`/`VendorScope` as the unified `Scope`; a dangling
  `subject` is a non-blocking warning (does not fail), while a dangling target stays a
  hard error; the whole-set-replace round-trip is over the unified scope set.
- `vendor-register`: the web register page and CLI `vendor` command read a vendor's
  exceptions from the unified `/scopes` (vendor-subject rows) instead of the removed
  `/vendor-scopes` endpoint; the owner-narrowed, justification-bearing exception display
  is unchanged.
- `authz-enforcement`: the upsert-PUT authorization requirement reads the owning org from
  `scopes.subject_id` (the merged table) instead of the removed `scopes.organisation_id` /
  `requirement_scopes.organisation_id`, and each write route is confined to its own target
  column so one scope-write permission cannot reach another target kind's row by id; the
  org-narrowed read-endpoint enumeration collapses `GET /scopes` and `GET /requirement-scopes`
  into one `GET /scopes`. No permission is added or removed.
- `asset-model`: the declared-asset removal requirement's FK-safe prune enumeration is
  corrected - `requirement-scopes` and `vendor-scopes` no longer exist as tables, and a
  `Scope`'s subject is now a scalar no-FK reference, so a scope whose subject asset is removed
  is left dangling (a tolerated warning) rather than pruned first; the remaining FK-backed
  references (evidence-collectors, integration-connections, org-scoped role assignments) are
  still pruned before the asset delete.

## Non-Goals

- Group subjects and group-based resolution (#123). `subject` is typed as an asset id
  and the schema tolerates any asset subject, but no `Group` kind or group resolution
  is added here.
- Group subjects and group-membership resolution (#123). `Machine`/discovered subjects ARE
  supported first-class in this change: the read-visibility predicate reads a Machine subject
  through its `parent` ancestry into the accessible-organisation set (the same OrgAncestry walk
  the SoA uses), so a Machine-subject scope is visible to a caller who can see its parent org
  and hidden from one who cannot, and the DB-accurate sync warning surfaces a retired or absent
  discovered `Machine` subject correctly. Only `Group` subjects remain deferred: a scope naming
  a group is schema-tolerated (the no-FK `subject_id` accepts any asset id) and warns at sync
  via the DB check when unresolved, but no group-membership read/resolution branch is built here
  until #123. No validator error forbids any subject type (that would defeat the general
  subject). A declared `Machine` asset IS authorable (`type: Machine`, `source: declared`, with a
  `parent`), so a config MAY name a declared `Machine` as a scope `subject`; discovered `Machine`
  subjects also arrive by ingest (`source: discovered` is rejected when authored). Both are
  first-class scope subjects in reads and the sync warning.
- Control-level Statement-of-Applicability disposition RESOLUTION for organisation subjects.
  An org subject MAY now target a control (the schema and table allow it), but the SoA
  projection continues to resolve only standard-level and requirement-level dispositions; a
  control-target org scope is stored and read back but not yet folded into a node disposition,
  exactly as vendor scopes are not folded into the SoA today. Adding control-level resolution
  is later v2 work. Note this defers only the disposition resolution, NOT the dangling-subject
  warning: the unresolved-subject warning scan covers ALL unified scopes regardless of target
  kind, so a control-target org scope whose subject is unresolved surfaces the same generic
  "rule targets a resource that does not currently exist" notice on the SoA page (and at sync),
  with no target kind exempted.
- Unifying the two app-managed disposition write endpoints into a single write. The
  `PUT`/`DELETE /scopes` and `/requirement-scopes` endpoints are retargeted onto the
  unified table but kept as two routes (distinct permissions and SoA semantics);
  collapsing them is out of scope.
- Vendor scopes remain gitops-write-only. This change does not add an app-managed write
  path for vendor-subject scopes.
- `Person` type, discovery-ingest changes, and collector retargeting.

## Impact

- MIT work only, no Enterprise carve-out. Domain model in `Freeboard.Core`
  (`GitOps/`); schema, migration, stores, and importer in `Freeboard.Persistence`;
  read models and endpoints in `Freeboard` (web) and `Freeboard.CLI`.
  `Freeboard.Agent` is untouched. No new package dependency.
- Schema: new migration `020_scope_generalization.sql`; the `scopes`,
  `requirement_scopes`, and `vendor_scopes` tables are merged into one `scopes` table
  and the two legacy tables are dropped.
- API: `GET /requirement-scopes` and `GET /vendor-scopes` are removed; `GET /scopes`
  changes shape to the unified row. The `/compliance/status` persisted-counts object drops the
  `requirementScopes` and `vendorScopes` count keys and reports one `scopes` count, in both the
  healthy and the degraded/all-null shapes - a breaking public response change. The write
  endpoints keep their routes.
- Config authors and every in-repo fixture under `examples/` and the tests move from
  `kind: Scope`/`RequirementScope`/`VendorScope` to the unified `kind: Scope`, and
  every `Out` document without a `justification` gains one.
- MySQL integration tests must cover the merged schema, the single-target `CHECK`, the
  three unique keys, the merge migration, and the dangling-subject warning.
