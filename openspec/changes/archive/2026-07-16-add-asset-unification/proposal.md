## Why

Object model v2 needs one spine. Today three separate kinds describe "a thing in
the estate": declared `Organisation` (the org tree), declared `Vendor` (software
and third parties), and the discovered machine `Asset` (from #98). They carry
overlapping ideas (identity, containment, ownership, scope subject) in three
schemas, three validators, three read models. v2 scope generalization (#92),
collector retargeting, and discovery (#101) all need a single `Asset` to target.
This change collapses the three into one declared-or-discovered `Asset` kind so
every later v2 ticket builds on one model instead of reconciling three.

This is a pre-production HARD CUTOVER: there is no data contract to preserve, so
the migration transforms and replaces the schema in place and the in-repo YAML
fixtures are hand-migrated in this change. There is no dual-kind compatibility
layer and no converter tool. `apiVersion` stays `freeboard.dev/v1alpha1`.

## What Changes

- **BREAKING** Add `kind: Asset` with `type: Company | Department | Machine |
  Vendor` (Person lands later) and an explicit `source: declared | discovered`.
- **BREAKING** Remove the `Organisation` and `Vendor` document kinds. Their data
  becomes `Asset` rows of the matching `type`. The `Machine`-only asset model
  from #98 is superseded by the unified table.
- Add two mutually exclusive edges, at most one per asset:
  - `parent` (structural containment; target must be `Company` or `Department`;
    drives scope inheritance and read-access; carried by Company/Department/Machine).
  - `owner` (accountability; target `Company` or `Department`; drives read-access
    only, not inheritance; carried by Vendor).
- Carry the discovered-only fields from today's asset row - `identity_kind`,
  `identity_value`, `state` (`Seen`/`Retired`), `first_seen`, `last_seen` - and
  keep the ULID id for discovered rows; declared rows keep an authored slug id.
  Both share one id space in one table.
- Author authority split: a declared config MAY only author `source: declared`;
  ingest is the only writer of `source: discovered`. Authoring `source:
  discovered` in config is a validation error. Unknown fields are rejected.
- **BREAKING** `parent`/`owner` are scalar references with no foreign key,
  validated at write, dangling tolerated. A dangling `parent`/`owner` is a
  NON-BLOCKING warning at sync and in the UI; it never fails a `sync`. This
  generalizes today's scalar-no-FK `asset.organisation_id`.
- `sync` reconciles declared assets only: it hard-removes a declared asset absent
  from config and never touches a discovered asset (or a sync would wipe the
  discovered inventory). Ingest owns discovered assets. A declared id that collides
  with an existing discovered ULID is a hard sync error (it would otherwise rewrite
  the discovered row), rejected before any write.
- A missing required edge - a declared `Vendor` with no `owner` or a `Machine` with
  no `parent` - is a NON-BLOCKING warning, not an error: the asset is invisible
  under the fail-closed read model, which the warning surfaces without wedging sync.
- **BREAKING** Migration (019) merges `organisations`, `vendors`, and the #98
  `asset`/`asset_source` tables into one `assets` table; rewrites the machine
  `organisation_id` scalar to `parent`; re-points the complete, schema-verified set
  of six downstream enforced foreign keys - `scopes.organisation_id`,
  `requirement_scopes.organisation_id`,
  `authz_organisation_role_assignments.organisation_id`, `vendor_scopes.vendor_id`,
  `evidence_collectors.vendor_id`, and `integration_connections.vendor_id` - at
  `assets`, and relaxes the `asset_source` composite FK to a simple
  `asset_id -> assets(id)` FK. A missed FK to a dropped table would block the
  cutover, so the set is enumerated in design.md (D7).
- Read-access: a rooted asset is visible through its `parent` chain to a readable
  org; a vendor is visible through its `owner`. Global vendor readability (every
  authenticated user sees every vendor) is dropped.
- Web AND CLI read models ship together; parity is an acceptance rule.
- Hand-migrate the in-repo YAML fixtures and tests to the new kind.

## Capabilities

### New Capabilities

None. The change reworks existing capabilities; it introduces no new spec folder.

### Modified Capabilities

- `asset-model`: the domain model generalizes from `Machine`-only to a typed,
  declared-or-discovered `Asset`; the schema and migration merge the org, vendor,
  and machine tables; new requirements cover declared-asset sync lifecycle,
  `parent`/`owner` reference integrity (dangling tolerated), and read-access via
  the parent chain and the vendor owner.
- `gitops-config-format`: adds `Asset` authoring and validation; removes
  `Organisation` and standalone `Vendor` authoring; retargets `Scope`,
  `RequirementScope`, `VendorScope`, and `EvidenceCollector.vendor` references at
  the matching typed asset; adds a non-blocking warning severity for dangling
  `parent`/`owner`.
- `organisation-model`: the `Organisation` kind and its acyclic-tree validation
  are removed (superseded by `Asset` of type `Company`/`Department` with a
  tolerated `parent`); `Scope` and `RequirementScope` now bind a Company or
  Department asset.
- `vendor-register`: the web page and CLI command read vendors from the unified
  asset store and narrow to the caller-visible set through the vendor `owner`;
  the global "every authenticated user sees every vendor" behavior is dropped.
- `compliance-web-read`: the `GET /vendors` and `GET /vendor-scopes` endpoints
  narrow their rows to the caller's accessible set through the vendor `owner` edge;
  the prior "intentionally do NOT filter / zero-grant caller reads every vendor and
  vendor-scope" behavior is removed. The `/evidence-collectors` and
  `/attestation-templates` endpoints are unchanged.
- `compliance-write`: the app-managed organisation CRUD and scope-disposition write
  path is retargeted off the dropped `organisations` table onto `Company`/`Department`
  assets, and the "same domain invariants as import" clause is rewritten to state the
  intentional split - gitops sync tolerates a dangling or cyclic `parent` as a
  non-blocking warning while the app CRUD endpoints stay strict and reject it at write
  time, with an explicit repair path (setting `parent` to null always passes the store
  guards, subject to the endpoint's authorization).

## Non-Goals

- This change's vendor-readability narrowing covers `/vendors` and `/vendor-scopes`
  only. The `vendor` id on the `/evidence-collectors` and `/integration-connections`
  surfaces - their endpoints, web pages, and the CLI
  `CollectorCommands`/`ConnectionCommands` - stays readable by a caller who cannot see
  that vendor. The Statement-of-Applicability requirement drill-down likewise renders
  the collector's `vendor` title to a caller who cannot see that vendor: that title is
  the collector's vendor surfaced in the SoA evidence view, so it is part of the same
  collector-vendor-narrowing. Each of these still carries a `vendor` reference readable
  by a caller who cannot see that vendor. This residual exposure is intentional for this
  ticket and tracked: narrowing `/evidence-collectors` and the SoA drill-down
  vendor-title is deferred to the collector-rename ticket, and narrowing
  `/integration-connections` to the IntegrationConnection ticket (#126).

## Impact

- MIT work only, no Enterprise carve-out. Domain model in `Freeboard.Core`
  (`Assets/`, `GitOps/`); schema, migration, stores, and importer in
  `Freeboard.Persistence`; read models in `Freeboard` (web) and `Freeboard.CLI`.
  `Freeboard.Agent` is untouched. No new package dependency.
- Schema: new migration `019_asset_unification.sql`; the `organisations`,
  `vendors`, and #98 `asset`/`asset_source` tables are merged/retargeted.
- Config authors and every in-repo fixture under `examples/` and the tests must
  move from `kind: Organisation`/`kind: Vendor` to `kind: Asset`.
- MySQL integration tests must cover the merged schema, the migration, and the
  declared-only sync lifecycle.
