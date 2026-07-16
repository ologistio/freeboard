# Tasks

Each group maps to one Conventional Commit. This is a BREAKING change (`!`): it
removes the `Organisation` and `Vendor` kinds and merges the schema. Use the
`breaking` label / `BREAKING CHANGE:` footer on the schema and model commits.

## 1. Core: Asset domain model and diagnostic severity

Commit: `feat(core)!: unify Organisation, Vendor, and Machine into an Asset model`

- [x] 1.1 Extend `AssetKind` in `src/Freeboard.Core/Assets/AssetKind.cs` to
  `Company`, `Department`, `Machine`, `Vendor`; add `AssetSource { Declared,
  Discovered }`; keep `AssetState`. Update XML docs.
- [x] 1.2 Add `DiagnosticSeverity { Error, Warning }` and a `Severity` field
  (default `Error`) to `src/Freeboard.Core/GitOps/Diagnostic.cs`; redefine
  `ConfigResult.IsValid` as "no `Error` diagnostics".
- [x] 1.3 In `src/Freeboard.Core/GitOps/ConfigModel.cs`: add `KindAsset` to
  `GitOpsSchema`, remove `KindOrganisation` and `KindVendor`; add an `Asset`
  record (apiVersion, kind, id, title, type, source, parent, owner, and the
  discovered-only fields); remove the `Organisation` and `Vendor` records; carry
  `Assets` on `GitOpsConfig`, drop `Organisations`/`Vendors`.
- [x] 1.4 Build `src/Freeboard.Core` and run `dotnet test tests/Freeboard.Core.Tests`
  (compile-check; validator wired next).

## 2. Core: Asset loader routing and validation

Commit: `feat(core): load and validate the Asset kind`

- [x] 2.1 In `ConfigLoader.cs`: add the `Asset` schema-key set (apiVersion, kind,
  id, title, type, source, parent, owner), the `Asset` switch arm, and update the
  unknown-kind message; remove the `Organisation`/`Vendor` arms. In the loader's
  key-set check (the same place unknown-field detection runs, on the parsed key set
  BEFORE deserialization), reject any authored discovered-only key (`identity_kind`,
  `identity_value`, `state`, `first_seen`, `last_seen`) on an `Asset` document as a
  distinct `Error` naming the field (not the generic unknown-field message). This
  must be a presence check on the parsed keys: after deserialization an omitted field
  and an authored-blank field are indistinguishable, so the validator cannot detect
  presence.
- [x] 2.2 In `ConfigValidator.cs`: replace `ValidateOrganisations` /
  `ValidateOrganisationParents` and `ValidateVendors` with `ValidateAssets` that
  returns the id sets (all assets, plus Company/Department subset and Vendor
  subset) the scope/requirement-scope/vendor-scope/collector phases consume.
- [x] 2.3 Implement the Asset rules in `ValidateAssets`: type token, source token,
  reject `source: discovered` in declared config, `parent`/`owner` mutual
  exclusivity, `parent` target must be Company/Department, `owner` target must be
  Company/Department, `parent` carrier must be Company/Department/Machine, `owner`
  carrier must be Vendor, duplicate id. Emit dangling `parent`/`owner`, `parent`
  cycles, and a missing required edge (declared Vendor with no owner, Machine with no
  parent; no warning for a parent-less Company/Department) as `Warning` diagnostics,
  not errors.
- [x] 2.4 Retarget the Scope, RequirementScope, VendorScope, and EvidenceCollector
  reference checks at the correct typed asset subset (organisation refs ->
  Company/Department assets; vendor refs -> Vendor assets).
- [x] 2.5 Add Core unit tests in `tests/Freeboard.Core.Tests`: Asset load/validate
  happy path and every rule above, including the dangling-warning-not-error, the
  missing-required-edge-warning-not-error (Vendor no owner, Machine no parent; no
  warning for a parent-less Company/Department), the authored-discovered-only-field
  error (a loader-level presence check, distinct from both the `source: discovered`
  error and the generic unknown-field error), and unknown-field cases. Run
  `dotnet test tests/Freeboard.Core.Tests`.

## 3. Persistence: merge migration

Commit: `feat(persistence)!: merge organisations, vendors, and assets into one table`

- [x] 3.1 Add `src/Freeboard.Persistence/Migrations/019_asset_unification.sql`,
  forward-only and NOT idempotent (matching 015/018): create `assets` (unified
  columns, parent/owner scalar-no-FK, discovered-only nullable columns, `CHECK` for
  parent/owner exclusivity, a unique index `(parent, identity_kind, identity_value)`
  carrying forward the per-org discovered dedup, `utf8mb4_bin` / `utf8mb4_0900_bin`
  collations; no org-scope column or composite unique key); copy `organisations`,
  `vendors`, and `asset` into it by `INSERT ... SELECT` (org/vendor -> declared,
  machine -> discovered with `organisation_id` rewritten to `parent`), assuming the
  three source id spaces are disjoint (a collision fails on the duplicate primary
  key).
- [x] 3.2 Relax `asset_source`'s composite FK (`fk_asset_source_asset`) to a simple
  `asset_id -> assets(id)` FK (D8); re-point the complete, verified set of six
  enforced FKs at `assets(id)` `ON DELETE RESTRICT` - `fk_scopes_organisation`,
  `fk_requirement_scopes_organisation`, `fk_authz_org_role_assignments_org`,
  `fk_vendor_scopes_vendor`, `fk_evidence_collectors_vendor`, and
  `fk_integration_connections_vendor`; then drop the old `organisations`, `vendors`,
  and `asset` tables (the drop fails if any FK was missed). The migration is
  forward-only and not idempotent; recovery from a failed run is restore-and-rerun
  (pre-production), not a replay guard.
- [x] 3.3 No `MigrationCatalogTests.cs` change is expected: it runs on synthetic
  fixtures (`001_first`..`020_broken`) and only asserts `001_initial_schema` is
  present in the real assembly, so there is no real migration count or hash to bump
  for 019. Touch it only if a specific assertion there references a table or row 019
  removes. Run `dotnet test tests/Freeboard.Persistence.Tests` (unit tier).

## 4. Persistence: read models, stores, and importer

Commit: `feat(persistence): project assets in the read and write stores and sync`

- [x] 4.1 `AssetReadModels.cs`: extend `AssetRow` with `Type`, `Source`, `Parent`,
  `Owner`; replace `OrganisationId` with `Parent`. Keep the discovered-only fields.
- [x] 4.2 `ComplianceReadModels.cs`: add `Owner` to `VendorRow`; keep
  `OrganisationRow` shape.
- [x] 4.3 `MySqlComplianceStore.cs`: project `GetOrganisationsAsync` from
  `assets WHERE type IN ('Company','Department')`, `GetVendorsAsync` (with `owner`)
  from `assets WHERE type = 'Vendor'`, and update the counts and SoA input reads.
- [x] 4.4 `MySqlAssetStore.cs` / `MySqlAssetWriteStore.cs`: read/write `assets`
  instead of `asset`; write `source = 'discovered'`, `type = 'Machine'`, and
  `parent` (was `organisation_id`); retarget the `asset_source` join and keep its
  org predicate against `a.parent` (was `a.organisation_id`) so a cross-org
  `asset_source` row cannot surface another org's machine (D8).
- [x] 4.5 `GitOps/ImportPlan.cs`: add `AssetRowPlan` (declared assets;
  parent/owner null-if-blank); remove `OrganisationRowPlan` and the vendor
  `DomainRow` path. Do not add parent-before-child ordering: `assets.parent` has no
  FK (D2/D4), so drop `OrderParentBeforeChild` for assets.
- [x] 4.6 `GitOps/MySqlGitOpsImporter.cs`: upsert declared assets; before any write,
  detect a declared id that collides with an existing `source = 'discovered'` id
  (D9) and raise it as a thrown sync error (`InvalidOperationException` naming the id)
  inside the DML transaction so it rolls back and mutates nothing - the CLI maps that
  to exit 3 (operational), not the exit-1 config-validation class; replace the org
  and vendor prunes with one `source = 'declared'`-guarded declared-asset prune placed
  after the referencing-row prunes (scopes, requirement-scopes, vendor-scopes,
  evidence-collectors, integration-connections); keep the
  `authz_organisation_role_assignments` prune keyed on Company/Department ids.
  Ensure the prune never touches `source = 'discovered'` rows.
- [x] 4.7 Retarget `MySqlComplianceWriteStore.cs`
  (`UpsertOrganisationAsync`/`DeleteOrganisationAsync` and their guards: child/scope/
  requirement-scope pre-delete counts, parent-exists, self-parent,
  `WouldFormCycleAsync`, `LockedParentChanged`; AND the org-existence checks in
  `UpsertScopeDispositionAsync` and `UpsertRequirementScopeDispositionAsync`
  (`ExistsAsync("organisations", organisation)`), whose query text names the dropped
  table) and
  `MySqlAuthzAdministrationStore.AssignOrganisationRoleAsync`'s org-existence check
  from `organisations` at `assets` filtered to `type IN ('Company','Department')`,
  writing `source = 'declared'`. Retain the acyclic and no-delete-with-children/scopes
  guards as app-level hard errors (stricter than the gitops sync-time tolerance, D12).
- [x] 4.8 Update `tests/Freeboard.Persistence.Tests/ImportPlanTests.cs` and add
  persistence tests for org create/update/delete plus the retained guards and the
  authz org-role assignment validation against the merged `assets` table. Run
  `dotnet test tests/Freeboard.Persistence.Tests` (unit tier).

## 5. Persistence: MySQL integration tests

Commit: `test(persistence): cover the merged schema, migration, and declared-only sync`

- [x] 5.1 Migration test (gated on `FREEBOARD_TEST_DB`): 019 applies on a 001-018
  database; `assets`/`asset_source` exist; old tables gone; every one of the six
  retargeted FKs resolves and no FK targets a dropped table; a fixture with disjoint
  ids copies successfully and a deliberately colliding fixture fails 019 on the
  duplicate primary key (D13).
- [x] 5.2 Sync test: declared assets upsert; an absent declared asset is
  hard-removed; a declared-only sync and an empty-config sync both leave discovered
  machines untouched; a declared id colliding with an existing discovered id fails
  the sync with no mutation (D9).
- [x] 5.3 Reference-integrity test: dangling `parent`/`owner` does not fail sync
  (warning only); discovered machine with a since-removed declared parent survives.
- [x] 5.4 Isolation test: a source-filtered read
  (`MySqlAssetStore.GetBySourceAsync`) keeps its org predicate against `a.parent`, so
  an `asset_source` row under another org does not surface the machine; discovered
  dedup still resolves to one row on `(parent, identity_kind, identity_value)` (D8).
- [x] 5.5 Read-access test: rooted asset visible via parent chain; vendor visible
  via owner; vendor with unreadable owner hidden. Run
  `dotnet test tests/Freeboard.Persistence.Tests` with `FREEBOARD_TEST_DB` set.

## 6. Web: asset-backed read models and vendor readability

Commit: `feat(web): read vendors as owner-scoped assets`

- [x] 6.1 Narrow the vendor register (`Pages/Compliance/Vendors.cshtml.cs`) to
  vendors whose `owner` is in the caller's accessible set; drop global readability.
- [x] 6.2 Narrow the `/vendors` AND `/vendor-scopes` reads in
  `Compliance/ComplianceEndpoints.cs` by vendor `owner` membership in the caller's
  accessible-org set, replacing the current global reads, so a hidden vendor leaks
  neither its id nor its `Out` justifications (D5). Confirm the org selector, SoA,
  and org-scoped pages still resolve on the preserved `OrganisationRow`. Do not add a
  general asset/machine listing surface: none exists today and this change does not
  introduce one (code-as-liability.md).
- [x] 6.3 Update the `PUT`/`DELETE /organisations` web endpoints so they keep working
  against the retargeted write store (`assets`); add/adjust `tests/Freeboard.Web.Tests`
  for owner-narrowed vendor AND vendor-scope rendering (an owner-excluded caller sees
  neither the vendor nor its vendor-scope justifications; replace the prior
  "zero-grant caller reads every vendor/vendor-scope" assertion) and for the
  `PUT`/`DELETE /organisations` endpoints on the merged schema, including repairing a
  gitops-created dangling/cyclic `parent` through `PUT /organisations/{id}` and its
  auth precondition: moving to root (`parent` null) requires `system.admin`, and
  re-parenting requires `org.write` on both the stored and new parent, so a node-writer
  lacking that parent-side permission is forbidden. Run
  `dotnet test tests/Freeboard.Web.Tests`.

## 7. CLI: read-model parity

Commit: `feat(cli): match the web owner-scoped vendor read and Asset summaries`

- [x] 7.1 `VendorCommands.cs` and the API vendor read: reflect owner-narrowed
  vendor rows so the CLI register matches the web.
- [x] 7.2 `GitOpsCommands.cs`: print `Asset` counts by `type` in the validate/sync
  summaries and planned state; print `Warning` diagnostics on the SUCCESS path across
  `validate`, `apply --dry-run`, and `sync` (today the diagnostic printer runs only
  inside the `if (!IsValid)` branch, so a warnings-only valid result would exit 0
  silently), still exiting 0 (D3).
- [x] 7.3 Update `tests/Freeboard.CLI.Tests`, covering warnings printed on the valid
  path across validate/apply/sync (exit 0) and owner-narrowed vendor/vendor-scope
  output. Run `dotnet test tests/Freeboard.CLI.Tests`.

## 8. Fixtures, docs, and architecture tests

Commit: `refactor(examples): migrate fixtures to the Asset kind`

- [x] 8.1 Rewrite `examples/gitops/*` and `examples/fixture-corp/*`
  `kind: Organisation` and `kind: Vendor` documents as `kind: Asset` with `type`,
  `source: declared`, and (for vendors) `owner`.
- [x] 8.2 Update the config-format documentation page to cover the `Asset` kind and
  remove `Organisation`/`Vendor`. Keep the existing YAML field names
  `Scope.organisation`, `RequirementScope.organisation`, `VendorScope.vendor`,
  `EvidenceCollector.vendor`, and `IntegrationConnection.vendor` (rename is deferred
  to scope generalization #92; D10) and note each now names a typed asset id.
- [x] 8.3 Update `tests/Freeboard.Architecture.Tests` (e.g. `AssetStoreSurfaceTests`)
  for the merged store surface if its assertions reference the old tables/rows.

## 9. Verification

Commit: folded into the last relevant commit (no standalone commit).

- [x] 9.1 Run `dotnet build` (whole solution) and `dotnet test` (unit/web tier,
  no DB) - all green.
- [x] 9.2 With the test MySQL up and `FREEBOARD_TEST_DB` set, run `dotnet test`
  including the integration tier - migration, sync, reference-integrity, and
  read-access tests pass.
- [x] 9.3 Run `freeboard gitops validate examples/fixture-corp` and
  `freeboard gitops sync --dry-run` (where applicable) to confirm the migrated
  fixtures validate and warnings surface without failing.
- [x] 9.4 Run `npx markdownlint-cli2 "**/*.md"` for any changed Markdown docs.
