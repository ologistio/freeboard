# Tasks

Each group maps to one Conventional Commit. This is a BREAKING change (`!`): it collapses
three kinds and three tables into one and removes two document kinds, two read endpoints,
and the legacy tables. Use the `breaking` label / `BREAKING CHANGE:` footer on the schema,
model, and API commits.

## 1. Core: unified Scope model, loader, and validation

Commit: `feat(core)!: collapse Scope, RequirementScope, and VendorScope into one Scope kind`

- [x] 1.1 In `src/Freeboard.Core/GitOps/ConfigModel.cs`: replace the `Scope`,
  `RequirementScope`, and `VendorScope` records with one `Scope` record (`apiVersion`,
  `kind`, `id`, `title`, `subject`, `standard`, `requirement`, `control`, `disposition`,
  `justification`); drop `KindRequirementScope` and `KindVendorScope` from `GitOpsSchema`;
  carry one `Scopes` list on `GitOpsConfig`, dropping `RequirementScopes`/`VendorScopes`.
- [x] 1.2 In `src/Freeboard.Core/GitOps/ConfigLoader.cs`: set the `Scope` allowed-key set
  to `{ apiVersion, kind, id, title, subject, standard, requirement, control, disposition,
  justification }`; keep one `Scope` switch arm; remove the `RequirementScope`/`VendorScope`
  key sets and arms; and drop those two tokens from the unknown-kind message enumeration
  (so an authored `kind: RequirementScope`/`VendorScope` is now an unknown-kind diagnostic).
- [x] 1.3 In `src/Freeboard.Core/GitOps/ConfigValidator.cs`: replace `ValidateScopes`,
  `ValidateRequirementScopes`, and `ValidateVendorScopes` with one `ValidateScopes`
  enforcing: required `id`/`title`/`subject`/`disposition`; exactly one of
  `standard`/`requirement`/`control`; `disposition` in `In`/`Out`; `Out` requires a
  non-blank `justification`; each target reference resolves as an `Error`; a `subject`
  absent from the resolved asset set is a `Warning` (not an error); a `subject` resolving to
  a `Vendor` asset targeting a `standard` is an `Error`; duplicate id; and pair uniqueness
  on `(subject, standard)`, `(subject, requirement)`, `(subject, control)`. Expose the full
  asset id set from `ValidateAssets` (alongside `OrganisationIds`/`VendorIds`) for the
  subject-dangling check. Make the subject-dangling `Warning` discriminable from other
  warnings (a distinct diagnostic kind/code) so the CLI `sync` path can suppress the DB-less
  Core scope-subject warning in favour of the importer's DB-accurate result while still
  printing the other Core warnings (F-7).
- [x] 1.4 Rewrite the Core unit tests onto the unified kind: fold
  `tests/Freeboard.Core.Tests/RequirementScopeValidationTests.cs` and
  `VendorScopeValidationTests.cs` and the scope cases in `ConfigLoaderTests.cs` /
  `ConfigValidatorTests.cs` into unified `Scope` tests covering every rule in 1.3 - the
  happy path per target, exactly-one-target (none/one/two/three), `Out`-requires-justification
  (and `In` omits it), target dangling is an `Error`, subject dangling is a `Warning` (does
  not fail validation), the Vendor-subject-targets-a-standard error, duplicate id, the three
  pair-uniqueness cases, and unknown-field / retired-kind cases. Run
  `dotnet test tests/Freeboard.Core.Tests`.

## 2. Persistence: merge migration

Commit: `feat(persistence)!: merge scopes, requirement_scopes, and vendor_scopes into one table`

- [x] 2.1 Add `src/Freeboard.Persistence/Migrations/020_scope_generalization.sql`,
  forward-only and NOT idempotent (matching 015/018/019): `CREATE TABLE scopes_v2` with
  `id` PK, no-FK `subject_id`, nullable `standard_id`/`requirement_id`/`control_id` (each
  FK `ON DELETE RESTRICT` to `standards`/`requirements`/`controls`), `disposition`,
  `justification TEXT NULL`, timestamps, `utf8mb4_bin` id/reference columns, the
  single-target `CHECK (((standard_id IS NOT NULL) + (requirement_id IS NOT NULL) +
  (control_id IS NOT NULL)) = 1)`, and three unique keys `(subject_id, standard_id)`,
  `(subject_id, requirement_id)`, `(subject_id, control_id)`. Name the three target FKs
  `fk_scopes_v2_standard`/`fk_scopes_v2_requirement`/`fk_scopes_v2_control` (collision-free):
  InnoDB FK constraint names are schema-wide and the old `scopes` table still carries
  `fk_scopes_standard` from `007` until it is dropped in 2.2, so reusing that name aborts
  `CREATE TABLE scopes_v2`. `RENAME TABLE` keeps the `_v2` constraint names unchanged.
- [x] 2.2 In the same migration: `INSERT ... SELECT` the three source tables into
  `scopes_v2` (`scopes.organisation_id` and `requirement_scopes.organisation_id` and
  `vendor_scopes.vendor_id` map to `subject_id`; carry the target columns, disposition,
  timestamps; assume disjoint ids so a collision fails on the duplicate PK). Write the
  provenance marker `Migrated legacy Out rule; justification was not recorded and requires
  review.` into copied `Out` rows (F-10). The two org tables have NO justification column, so
  their `INSERT ... SELECT` sets `justification = CASE WHEN disposition = 'Out' THEN '<marker>'
  ELSE NULL END` (there is no source column to carry); `vendor_scopes` HAS a justification
  column, so its `INSERT ... SELECT` preserves it and substitutes the marker only for a blank
  `Out`: `justification = CASE WHEN disposition = 'Out' AND NULLIF(TRIM(justification), '') IS
  NULL THEN '<marker>' ELSE justification END`. This ensures no `Out` lacks a rationale in the
  migrate-before-sync window; the marker is overwritten by the importer's whole-set replace on
  the next sync. `DROP TABLE scopes; DROP TABLE requirement_scopes; DROP TABLE vendor_scopes;`
  `RENAME TABLE scopes_v2 TO scopes;`. Document the forward-only, restore-and-rerun recovery.
- [x] 2.3 Confirm `MigrationCatalogTests` needs no change (it runs on synthetic fixtures and
  only asserts `001_initial_schema` is present); touch it only if a specific assertion
  references a table or row `020` removes. Run `dotnet test tests/Freeboard.Persistence.Tests`
  (unit tier).

## 3. Persistence: read models, stores, and importer

Commit: `feat(persistence)!: project the unified scope set in the read and write stores and sync`
(BREAKING: removes the public `RequirementScopeRow`/`VendorScopeRow` read models and the
`GetRequirementScopesAsync`/`GetVendorScopesAsync` store methods, and changes the
`IGitOpsImporter.ImportAsync` signature from `Task` to an import-result return; requires a
`BREAKING CHANGE:` footer.)

- [x] 3.1 `ComplianceReadModels.cs`: replace `ScopeRow`/`RequirementScopeRow`/`VendorScopeRow`
  with one enriched internal read carrier `ScopeRow(Id, Title, Subject, string? Standard,
  string? Requirement, string? Control, Disposition, string? Justification, string? SubjectType,
  string? SubjectSource, string? SubjectState, string? SubjectParent, string? SubjectOwner)`
  (F-13): the first eight fields are the public/wire (`ApiScope`) shape; the trailing five
  subject-narrowing fields are populated by the `GetScopesAsync` `LEFT JOIN assets`, are
  server-side only, and are NEVER serialized (the `/scopes` endpoint projects only the eight
  public fields into its response object). Drop the `RequirementScopes` field from
  `SoaInputs` and `SoaDrilldownInputs` (they carried `IReadOnlyList<RequirementScopeRow>`) so
  the requirement layer comes from the one `Scopes` list; and collapse `ComplianceCounts` -
  remove `RequirementScopes` and `VendorScopes`, leaving one `Scopes` count.
- [x] 3.2 `GitOps/ImportPlan.cs`: replace the three row-plan types with one `ScopeRowPlan`
  (subject + one target, null-if-blank target sides and justification); keep one `ScopeIds`.
- [x] 3.3 `IComplianceStore.cs` / `MySqlComplianceStore.cs`: one `GetScopesAsync` over the
  unified table (ordered by `id`), enriched by a `LEFT JOIN assets` on `subject_id` so each row
  carries the subject's resolved `type`/`source`/`state`/`parent`/`owner` for the web `/scopes`
  subject-readability narrowing and the D2 fail-closed check (F-13) - these narrowing fields are
  server-side only and are NOT surfaced in the API response; `GetStatementOfApplicabilityInputsAsync` and
  `GetStatementOfApplicabilityDrilldownInputsAsync` stop reading `requirement_scopes` and
  select unified scopes (standard-target for the standard layer, requirement-target for the
  requirement layer) plus the full unified scope set of ALL target kinds for the
  dangling-subject warning scan (so a control-target org scope's dangling subject also warns,
  F-6), and return the resolvable asset id set for the dangling-subject check - a subject is
  resolvable when an `assets` row has its id and is NOT a retired discovered asset (`NOT
  (source = 'discovered' AND state = 'Retired')`), the D2 predicate; collapse the
  `GetCountsAsync` query and `ComplianceCounts` from three scope counts into one.
- [x] 3.4 `GitOps/MySqlGitOpsImporter.cs`: replace the scope upsert + requirement-scope
  replace + vendor-scope replace with one `ReplaceScopesAsync` (delete-all then insert),
  ordered before the absent-standard/requirement/control deletes and before the declared-asset
  prune, so no target `RESTRICT` FK is violated and a removed subject asset leaves a
  dangling-subject scope. Add the importer-side DB dangling-subject warning (F-7, D8 reversal):
  after all writes but BEFORE commit, within the same DML transaction, run a `LEFT JOIN assets`
  applying the D2 predicate (`NOT EXISTS` a live row, i.e. no row OR a discovered `Retired` row)
  over the persisted scope subjects, observing the final post-write asset state; capture the
  unresolved-subject set into the import result and THEN commit, so a check failure rolls the
  whole import back (preserving all-or-nothing) rather than committing then throwing (F-14).
  Change `IGitOpsImporter.ImportAsync` to return an import result carrying that list instead of
  `Task` (void); `MySqlGitOpsImporter` populates it. This is the DB-accurate signal Core cannot
  produce (Core has no database), covering discovered and retired `Machine` subjects.
- [x] 3.5 `MySqlComplianceWriteStore.cs`: retarget `UpsertScopeDispositionAsync`/
  `DeleteScopeAsync` and `UpsertRequirementScopeDispositionAsync`/`DeleteRequirementScopeAsync`
  at the unified `scopes` table (write `subject_id` plus the standard resp. requirement
  target), enforce `(subject, standard)` / `(subject, requirement)` uniqueness, reject `Out`
  with no justification, and read the owning org from `scopes.subject_id` in the org-delete
  guard and the `LockedOwnerChanged` check. Confine each route to its own target column so an
  id cannot cross the target boundary (an authz boundary, F-1) WITHOUT breaking create: on a
  PUT, look up the row by GLOBAL id (NOT target-filtered) and branch - no row anywhere INSERTs
  a new row of the route's target kind (the standard route a `standard_id` row, the requirement
  route a `requirement_id` row); a row whose own target column matches the route is UPDATED; a
  row whose target column is a DIFFERENT kind returns the not-found result (a PUT MUST NOT
  convert an existing row's target kind). A naive `... WHERE id=@Id AND standard_id IS NOT NULL`
  selector cannot branch this way (it is null both for a new id and a wrong-kind row), so it
  would 404 every new-scope PUT. The DELETE stays target-column-scoped (`... WHERE id=@Id AND
  standard_id IS NOT NULL` for the standard route, `requirement_id IS NOT NULL` for the
  requirement route) and is not-found when it affects zero rows. Control-target rows stay
  unreachable by either app route (gitops-write-only). Do NOT emit an unqualified `DELETE FROM
  scopes WHERE id=@Id`. Add a not-found result to `WriteResult` (a `bool IsNotFound` /
  `WriteResult.NotFound()`) returned when a PUT's global-id lookup finds a wrong-kind row or a
  DELETE affects no row, and map it to 404 in `ComplianceWriteEndpoints.RunAsync` (which today
  has only 204/409/422 paths), so a wrong-kind id returns NOT-FOUND rather than falling through
  to a duplicate-PK 409.
- [x] 3.6 Update `tests/Freeboard.Persistence.Tests/ImportPlanTests.cs` and the write-store
  unit tests for the unified plan and the retargeted disposition writes (including the
  `Out`-requires-justification rejection). Run `dotnet test tests/Freeboard.Persistence.Tests`
  (unit tier).

## 4. Persistence: MySQL integration tests

Commit: `test(persistence): cover the merged schema, CHECK, uniqueness, and migration`

- [x] 4.1 Migration test (gated on `FREEBOARD_TEST_DB`): `020` applies on a 001-019 database;
  the unified `scopes` table exists with the `CHECK`, the three unique keys, and the three
  target FKs present under the collision-free names `fk_scopes_v2_standard`/
  `fk_scopes_v2_requirement`/`fk_scopes_v2_control`; `requirement_scopes` and `vendor_scopes`
  are gone; a disjoint-id fixture copies and a deliberately colliding fixture fails `020` on
  the duplicate primary key; and the provenance-marker backfill is tested across all three
  source shapes separately (F-10): an org `Out` row (from `scopes`/`requirement_scopes`, which
  have no justification column) lands with the marker, a `vendor_scopes` `Out` row with a real
  justification is preserved unchanged, and a blank `vendor_scopes` `Out` row lands with the
  marker.
- [x] 4.2 Constraint tests: the `CHECK` rejects a zero-target and a two-target row written
  directly; each unique key rejects a duplicate `(subject, target)` pair; the standard,
  requirement, and control target `RESTRICT` holds (a targeted catalogue row cannot be dropped
  while a scope references it).
- [x] 4.3 Sync tests: a whole-set-replace sync round-trips unified scopes and hard-removes an
  absent scope; removing a scope's subject asset does not fail the sync and the subject dangles
  (warning only, no FK on `subject_id`); the importer prunes scopes before deleting a targeted
  standard/requirement/control. DB-accurate sync-warning test (F-7): the importer result reports
  a scope whose `Machine` subject is absent from `assets` OR present only as a discovered
  `Retired` row as unresolved, while a scope whose `Machine` subject resolves to a live
  discovered asset is NOT reported - proving the post-import `LEFT JOIN assets` D2 predicate,
  which Core cannot evaluate.
- [x] 4.4 Write-store target-isolation test (F-1): against the real MySQL store, seed a
  requirement-target and a standard-target scope row, then assert `MySqlComplianceWriteStore`'s
  standard-route delete/upsert is a no-op (returns the not-found result, mutates nothing) for
  the requirement-target row's id and vice versa, and neither route reaches a control-target
  row; AND include an ordinary new-id create case - a PUT whose id names no existing `scopes`
  row SUCCEEDS in creating a row of the route's own target kind - so the target-column scoping
  cannot regress normal creation into a 404. This proves the production SQL carries `standard_id
  IS NOT NULL` / `requirement_id IS NOT NULL` on the DELETE and branches create-vs-wrong-target
  on the global id for the PUT (a web test with fake write stores cannot prove the SQL). Run
  `dotnet test tests/Freeboard.Persistence.Tests` with `FREEBOARD_TEST_DB` set.
- [x] 4.5 Migrate `tests/Freeboard.Persistence.Tests/MySqlIntegrationTests.cs`, which is
  materially falsified by this change (F-21): it constructs the removed Core `RequirementScope`/
  `VendorScope` types and the `GitOpsConfig.RequirementScopes`/`VendorScopes` lists, calls the
  removed `GetRequirementScopesAsync`/`GetVendorScopesAsync`, asserts the dropped
  `requirement_scopes`/`vendor_scopes` tables EXIST in its migrated-tables assertion (a guaranteed
  test FAILURE once 4.1's `020` drops them, not a compile error), asserts their per-table
  schema/FK/unique-key/CHECK shapes, and has whole test methods keyed on the separate tables (the
  requirement-scope and vendor-scope resync/removal tests). Fold every requirement_scopes/
  vendor_scopes schema, constraint, FK, and resync assertion and test method into the unified
  `scopes` equivalents, and update the migrated-tables assertion to REMOVE `requirement_scopes`
  and `vendor_scopes` from the `Contains` list and `DoesNotContain` them instead (mirroring the
  existing `DoesNotContain("organisations"/"vendors")` precedent). Two OTHER integration tests are
  falsified the same way and must be migrated in step:
  `tests/Freeboard.Persistence.Tests/AssetUnificationIntegrationTests.cs` applies ALL migrations
  via `ApplyPendingAsync` (so `020` runs on top of `019`), yet its `Migration019RepointsAllForeignKeysAndDropsOldTables`
  test asserts the six retargeted FKs (`fk_scopes_organisation`, `fk_requirement_scopes_organisation`,
  `fk_authz_org_role_assignments_org`, `fk_vendor_scopes_vendor`, `fk_evidence_collectors_vendor`,
  `fk_integration_connections_vendor`) reference `assets` - three of which (`fk_scopes_organisation`,
  `fk_requirement_scopes_organisation`, `fk_vendor_scopes_vendor`) `020` drops. This migration-019-specific
  test SHALL apply migrations only THROUGH `019` (not through `020`) and RETAIN all six retargeted-FK
  assertions, because the unchanged `asset-model` spec still requires `019` to repoint all six FKs;
  do NOT drop any FK assertion. Other tests in that class that legitimately need the final schema
  continue applying through `020` (add an apply-through-ordinal path rather than making every test
  stop at `019`); and (in the CLI commit, group 6)
  `tests/Freeboard.CLI.Tests/SyncMySqlIntegrationTests.cs` authors inline `kind: VendorScope`
  documents and counts rows in the dropped `vendor_scopes` table, which must move to the unified
  `kind: Scope` and the `scopes` table. Also `tests/Freeboard.Persistence.Tests/AuthzIntegrationTests.cs`
  constructs `new GitOpsConfig { ... RequirementScopes = [] }` (a plain compile break once task 1.1
  removes that property, which fails the whole `Freeboard.Persistence.Tests` build regardless of
  `FREEBOARD_TEST_DB`) - drop the removed list initializer. Run `dotnet test tests/Freeboard.Persistence.Tests`
  with `FREEBOARD_TEST_DB` set.

## 5. Web: unified scope read, write retarget, and SoA dangling warning

Commit: `feat(web)!: serve one unified scopes endpoint and warn on a dangling subject`

- [x] 5.1 `Compliance/ComplianceEndpoints.cs`: replace the `/scopes`, `/requirement-scopes`,
  and `/vendor-scopes` handlers with one `GET /scopes` returning the unified row, narrowed by
  subject readability with a branch per parent-anchored subject family (F-7): an org subject in
  the accessible set, a vendor subject whose `owner` is in the accessible set, and a machine (or
  other parent-anchored) subject whose `parent` org's inclusive ancestry intersects the accessible
  set. The vendor `owner` and machine `parent` come from the `GetScopesAsync` enriched subject
  fields (F-13). The machine branch seeds `OrgAncestry.InclusiveAncestors` (reused UNCHANGED, org
  dictionary, existing visited-set cycle guard) from the subject's `parent` org id and admits the
  scope when that inclusive ancestry INTERSECTS the accessible set - exact parity with the
  org-subject rule (the accessible set is the caller's downward-closed read-subtree union, so the
  intersection admits only via a real accessible org in the chain). A dangling `parent` resolves to
  no org and never intersects; a cycle is bounded by the visited-set guard and admits only if a
  real accessible org sits in it; an accessible parent-org grants visibility even if its own
  grandparent is broken. No rooted-vs-cycle signal and no `OrgAncestry` change is added, and no new
  graph resolver; else omitted, fail-closed (also for a subject unresolved by the D2 predicate).
  Remove the two other endpoints. Also rewrite the `/compliance/status` handler in the same file to emit
  one `scopes` count and DROP the `requirementScopes` and `vendorScopes` keys from BOTH the
  healthy `persisted` shape and the degraded/all-null shape (F-17): `ComplianceCounts` no longer
  carries `RequirementScopes`/`VendorScopes` (3.1), so the handler reading them will not compile,
  and the persisted-counts wire shape loses those two keys.
- [x] 5.2 `Compliance/ComplianceWriteEndpoints.cs`: keep `PUT`/`DELETE /scopes` and
  `/requirement-scopes` retargeted onto the unified table; the DTOs use `subject` and gain an
  optional `justification`; the handlers reject an `Out` with no justification. Confine each
  route to its own target column (F-1) without breaking create: a new-id PUT creates a row of
  the route's own target kind, a same-kind id updates, and a wrong-kind id (or control-target
  row) is NOT-FOUND / 404 via the new `WriteResult.NotFound()` path (a PUT MUST NOT convert an
  existing row's target kind), not a conflict; control-target rows are unreachable from both
  routes. Filter the ENDPOINT's stored-owner authorization lookup by the route's target column
  too (F-1), not just the store: the PUT in-handler stored-owner read (`UpsertScopeAsync`,
  `UpsertRequirementScopeAsync`) and the DELETE authz selector (`StoredScopeOrgSelector`,
  `StoredRequirementScopeOrgSelector`) SHALL read from the one `GetScopesAsync` filtered to the
  route's own target column (standard route `s.Standard is not null`, requirement route
  `s.Requirement is not null`), replacing the removed `GetRequirementScopesAsync`. A same-id
  wrong-kind row yields no stored owner, so the handler takes the new/absent-row path and the
  write resolves to 404 - never a 403 against, or a silent authorization from, the wrong-kind
  row's owning org.
- [x] 5.3 `Compliance/StatementOfApplicability.cs` and
  `Pages/Compliance/StatementOfApplicability.cshtml(.cs)`: drop the
  `IReadOnlyList<RequirementScopeRow>` parameter from `Resolve` and `ResolveDrilldown` (the
  requirement layer now comes from the one unified scopes list); feed the resolver the unified
  scopes and the asset id set; surface a GENERIC non-blocking page notice for any unified
  scope of ANY target kind (standard, requirement, OR control) whose `subject` resolves to no
  asset ("rule targets a resource that does not currently exist") that does NOT name the scope
  id or subject id to an ordinary caller (F-6) - only the disposition resolution stays
  standard/requirement-level, the warning scan is not exempted by target kind; resolution
  logic (nearest-ancestor) unchanged. Update the callers of
  the reshaped `Resolve`/`ResolveDrilldown` signatures to drop the removed
  `RequirementScopes` argument (F-9, F-18): `Pages/Compliance/ControlDetail.cshtml.cs`
  (`ResolveDrilldown(... inputs.RequirementScopes ...)`), `Evidence/EvidenceIngestEndpoints.cs`
  (`Resolve(soa.Organisations, soa.Scopes, soa.Requirements, soa.RequirementScopes, standardId)`),
  and the SoA JSON endpoint in `Compliance/ComplianceEndpoints.cs`
  (`Resolve(inputs.Organisations, inputs.Scopes, inputs.Requirements, inputs.RequirementScopes, standardId)`).
  `Pages/Compliance/ControlDetailProjection.cs` is NOT touched (it maps resolved node output
  types, not the reshaped input types or signatures). Update `Pages/Compliance/Vendors.cshtml.cs`
  to read vendor exceptions from the unified scopes (vendor-subject rows), owner-narrowed. Update
  `tests/Freeboard.Web.Tests` for the evidence-ingest SoA-resolve call as well as the SoA page.
- [x] 5.4 Update `tests/Freeboard.Web.Tests` (reshape the web fakes named below first): the
  unified `/scopes` returns the unified row and hides a scope whose subject is org-inaccessible or
  owner-excluded (no leaked `Out` justification); the machine-readability parity rule (F-7/F-13),
  covering (i) a `Machine` subject under an accessible parent-org is VISIBLE; (ii) a `Machine`
  subject under a NON-accessible org whose ancestry does not intersect the accessible set is
  OMITTED; (iii) a `Machine` subject with a dangling/missing immediate `parent` is OMITTED; (iv) a
  `Machine` subject under an accessible parent-org that itself has a dangling ANCESTOR is STILL
  VISIBLE (the accessible parent grants it); (v) a `Machine` subject with a cyclic `parent`
  ancestry is bounded and admitted ONLY if a real accessible org is in the cycle (otherwise
  omitted); and a `Machine` subject that is a retired discovered asset is OMITTED (unresolved by
  D2, independent of ancestry); the removed endpoints 404; the SoA page renders the generic dangling-subject
  notice ("rule targets a resource that does not currently exist") without failing and without
  naming the scope or subject id, including for a control-target org scope with a dangling
  subject (proving the warning is not exempted by target kind, F-6); the write endpoints reject an `Out` with no
  justification; and a cross-route target-isolation test - a caller holding only
  `compliance.scope.write` cannot delete or convert a requirement-target row via `/scopes/{id}`,
  and a caller holding only `compliance.requirement-scope.write` cannot reach a standard-target
  row via `/requirement-scopes/{id}` (both treated as not-found), and neither route reaches a
  control-target row (F-1). Add a distinct endpoint-authz test (F-1): a wrong-kind row (a
  requirement-target row addressed through `/scopes/{id}`) whose owning organisation the caller
  CANNOT write returns 404, NOT 403 and NOT a leak - proving the endpoint's stored-owner lookup
  is target-column-filtered so the wrong-kind row is never authorized against its owner (distinct
  from the same-org store-level SQL test in 4.4). Also update the `/compliance/status`
  status-count assertions to expect one `scopes` key and NO `requirementScopes`/`vendorScopes`
  keys (F-17), in both the healthy and unreachable-store cases. Add a direct unit test of the
  machine-readability predicate (which reuses `OrgAncestry.InclusiveAncestors` UNCHANGED and
  intersects the seeded ancestry with the accessible set), covering the accessible-parent (admit),
  outside-set (omit), dangling-parent (omit), accessible-parent-with-dangling-ancestor (admit),
  and cyclic (omit unless a real accessible org is in the cycle) cases, so the fail-closed omission
  is proven at the unit level and not only through the endpoint.
  Reshape the web-test fakes this group depends on: `FakeComplianceStore.cs` (drop the
  `RequirementScopes`/`VendorScopes` properties and the `GetRequirementScopesAsync`/
  `GetVendorScopesAsync` methods; feed the reshaped `SoaInputs`/`SoaDrilldownInputs` and one
  `Scopes` count into `ComplianceCounts`; supply the enriched `ScopeRow` subject-narrowing fields
  (`SubjectType`/`SubjectSource`/`SubjectState`/`SubjectParent`/`SubjectOwner`) the readability
  tests need); `OrgSelectionTests.cs` (its inline `IComplianceStore` fake `CountingComplianceStore`
  drops the removed `GetRequirementScopesAsync`/`GetVendorScopesAsync` methods); and
  `ComplianceAuthzTests.cs` (its inline `IComplianceWriteStore` fake `RecordingWriteStore` whose
  `UpsertRequirementScopeDispositionAsync`/`DeleteRequirementScopeAsync` and
  `UpsertScopeDispositionAsync`/`DeleteScopeAsync` signatures change to `subject` + optional
  `justification`); and `ComplianceWriteEndpointTests.cs` (its inline `IComplianceWriteStore` fake
  `FakeComplianceWriteStore` whose `UpsertScopeDispositionAsync`/`UpsertRequirementScopeDispositionAsync`
  signatures likewise change to `subject` + optional `justification`, and which gains the not-found
  `WriteResult` path for the wrong-target-kind 404 test). The web-test data files that construct the
  removed `RequirementScopeRow`/`VendorScopeRow` or set `GitOpsConfig`/`SoaInputs` requirement-scope
  and vendor-scope lists - `StatementOfApplicabilityPageTests.cs`, `StatementOfApplicabilityTests.cs`,
  `VendorsPageTests.cs`, and `ComplianceEndpointTests.cs` (the last also asserts the removed
  `requirementScopes`/`vendorScopes` status-count keys, F-17) - move to the unified `ScopeRow`/one
  `scopes` count. Run `dotnet test tests/Freeboard.Web.Tests`.

## 6. CLI: read-model parity

Commit: `feat(cli)!: read the unified scopes endpoint and print Scope summaries`

- [x] 6.1 API client (`IFreeboardApiClient.cs` / `HttpFreeboardApiClient.cs`): replace
  `ListVendorScopesAsync` with `ListScopesAsync` returning the unified `ApiScope(Id, Title,
  Subject, string? Standard, string? Requirement, string? Control, Disposition, string?
  Justification)`.
- [x] 6.2 `VendorCommands.cs`: render each vendor's exceptions from the unified scopes filtered
  to its subject (parity with the web register). `GitOpsCommands.cs`: print one `Scope` count and
  list unified scopes in the validate/apply/sync summaries and planned state. On the `sync` path
  (F-7, D8 reversal), print the importer's DB-accurate unresolved-subject warnings after the
  import commits, and SUPPRESS the DB-less Core scope-subject-dangling warnings on that path (so
  a healthy discovered `Machine` subject that resolves in the DB draws no false-positive
  warning); the other Core warnings (dangling `parent`/`owner`, cycles) still print via
  `PrintWarnings`. `validate` and `apply --dry-run` keep the DB-less Core scope-subject warning
  unchanged (they have no database).
- [x] 6.3 Update `tests/Freeboard.CLI.Tests`: `validate`/`apply --dry-run` print the DB-less
  Core subject-dangling `Warning` on the valid path and exit 0; the unified `Scope`
  count/listing and owner-narrowed vendor output. `GitOpsCommandTests.cs`'s
  `ValidateVendorScopeWithUnknownVendorExitsOneNamingTheVendor` authors an inline `kind: VendorScope`
  and asserts vendor-scope-specific validation - once `VendorScope` is an unknown kind (task 1.2) it
  must move to the unified `kind: Scope` (a Vendor subject with a requirement/control target) or be
  replaced by the equivalent unified-scope assertion. The `sync`-path warnings are exercised by
  MANDATORY CLI tests using the existing `FakeImporter` (`tests/Freeboard.CLI.Tests/Fakes.cs`),
  updated to return a configurable `ImportResult` (F-15): (a) an EMPTY importer result
  suppresses the discriminable Core scope-subject-dangling false-positive on the sync path (no
  scope-subject warning printed); (b) one unresolved importer subject prints EXACTLY one
  warning; (c) a non-scope Core warning (a dangling `parent`/`owner` or cycle) STILL prints
  during sync; (d) `FakeImporter.ImportAsync` returns the configured `ImportResult` and `sync`
  prints its unresolved-subject warnings without double-printing the Core scope-subject warning.
  Run `dotnet test tests/Freeboard.CLI.Tests`.

## 7. Fixtures and architecture tests

Commit: `test(examples)!: migrate fixtures and architecture tests to the unified Scope kind`
(a data/test migration, not a code refactor; breaking because the fixtures now require the
unified `kind: Scope`).

- [x] 7.1 Rewrite `examples/gitops/*` and `examples/fixture-corp/*` `kind: Scope`,
  `kind: RequirementScope`, and `kind: VendorScope` documents as the unified `kind: Scope`
  (subject + one target), and add a `justification` to every `Out` that lacks one (in the repo
  today: `scope-products-soc2`, `rs-products-firewalls-01-out`, `rs-fixture-corp-eng-updates-05-out`).
  Update the CLI/test fixtures under `tests/**/fixtures/**` the same way. Also rewrite the example
  READMEs `examples/fixture-corp/README.md` and `examples/gitops/README.md`: their kind tables and
  scope prose describe the removed `RequirementScope`/`VendorScope` kinds and the
  `requirement-scopes.yaml`/`vendor-scopes.yaml` filenames - fold them into the unified `Scope`
  kind, and reconcile the referenced fixture filenames (rename/consolidate the two legacy scope
  files, or note that gitops loads by content not filename).
- [x] 7.2 Update `tests/Freeboard.Architecture.Tests` if any store-surface assertion references
  the removed tables/rows or the removed read models.

## 7b. Docs

Commit: `docs(gitops)!: document the unified Scope kind and the removed scope endpoints`
(breaking authoring guidance: the `RequirementScope`/`VendorScope` kinds and the
`/requirement-scopes` / `/vendor-scopes` endpoints are gone).

- [x] 7b.1 Update `docs/gitops.md`: replace the three scope sections with one unified `Scope`
  section (subject, the three targets, the Vendor-subject-no-standard rule, the generalized
  `Out`-requires-justification rule, the dangling-subject warning); update the noun table and
  supported-kinds list; update the persistence section (one `scopes` table) and the read-endpoint
  section (one `/scopes`, no `/requirement-scopes` or `/vendor-scopes`). Record the
  persisted-counts wire-shape change (F-17): the `/compliance/status` `persisted` object now
  carries one `scopes` count and NO `requirementScopes`/`vendorScopes` keys (in both the healthy
  and unreachable-store shapes), wherever that shape is documented.

## 8. Verification

Commit: folded into the last relevant commit (no standalone commit).

- [x] 8.1 Run `dotnet build` (whole solution) and `dotnet test` (unit/web tier, no DB) - all green.
  The removals in groups 1-3 (the `RequirementScope`/`VendorScope` records and `GitOpsConfig` lists,
  `RequirementScopeRow`/`VendorScopeRow`, `GetRequirementScopesAsync`/`GetVendorScopesAsync`,
  `ListVendorScopesAsync`/`ApiVendorScope`, and the `ComplianceCounts.RequirementScopes`/`VendorScopes`
  members) are compile-breaking, so `dotnet build` is the exhaustive backstop for the test-file lists
  named in groups 4-7: fix EVERY remaining reference to a removed symbol the build reports (migrating
  each to the unified `Scope`/`ScopeRow`/`ApiScope`/one-`scopes`-count equivalent) - a green whole-solution
  build proves no falsified test/fixture reference was missed. Do NOT touch the intentionally-kept
  `compliance.requirement-scope.write` permission and its `PUT`/`DELETE /requirement-scopes/{id}` write
  route (Divergence 2).
- [x] 8.2 With the test MySQL up and `FREEBOARD_TEST_DB` set, run `dotnet test` including the
  integration tier - migration, CHECK/uniqueness, sync, and dangling-subject tests pass.
- [x] 8.3 Run `freeboard gitops validate examples/fixture-corp` (and `examples/gitops`) to
  confirm the migrated fixtures validate and any warnings surface without failing.
- [x] 8.4 Run `npx markdownlint-cli2 "**/*.md"` for the changed Markdown docs.
