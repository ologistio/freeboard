## 1. Core model, loader, validator (feat(core))

- [x] 1.1 Add a `RequirementScope` record to `ConfigModel.cs` (apiVersion, kind, id,
      title, organisation, requirement, disposition) and add
      `KindRequirementScope = "RequirementScope"` to `GitOpsSchema`. Add a
      `RequirementScopes` list to `GitOpsConfig`.
- [x] 1.2 In `ConfigLoader.cs`: add a `RequirementScope` `SchemaKeys` entry
      (apiVersion, kind, id, title, organisation, requirement, disposition); add the
      `apiVersion` alias override for `RequirementScope`; route `KindRequirementScope`
      in the kind switch (adding to `config.RequirementScopes`); add `RequirementScope`
      to the unknown-kind error enumeration.
- [x] 1.3 In `ConfigValidator.cs`: add `ValidateRequirementScopes(config,
      organisationIds, requirementIds, diagnostics)` and call it after
      `ValidateScopes` (it consumes the organisation id set from
      `ValidateOrganisations` and the requirement id set from `ValidateRequirements`).
      Rules mirror `ValidateScopes`: apiVersion check; required id/title/organisation/
      requirement/disposition; organisation resolves; requirement resolves;
      `TryParseDisposition` on disposition; per-kind duplicate id; duplicate
      `(organisation, requirement)` pair. Reuse `TryParseDisposition`. Loader and
      validator still never throw or print.

## 2. Core tests (test(core))

- [x] 2.1 Add loader/validator fixtures under `tests/Freeboard.Core.Tests/fixtures/`
      covering a valid RequirementScope, one with an unknown organisation, one with an
      unknown requirement, one missing a required field, one with a `standard` field
      (unknown-field rejection), one with a bad disposition, and two with a duplicate
      `(organisation, requirement)` pair.
- [x] 2.2 Add unit tests: a RequirementScope loads and routes distinctly from Scope;
      each validation rule reports the expected diagnostic; a duplicate
      `(organisation, requirement)` pair is rejected; a config with no
      RequirementScope documents still loads and validates; loader/validator still
      never throw or print.

## 3. Persistence: schema, models, store, importer (feat(persistence))

- [x] 3.1 Add `Migrations/009_requirement_scopes.sql`: create the
      `requirement_scopes` table with `CREATE TABLE IF NOT EXISTS` (re-runnable,
      matching migrations `007`/`008`) (binary-collation `id`/`organisation_id`/
      `requirement_id`; `api_version`, `title`, `disposition` matching the
      `scopes.disposition` definition, `created_at`, `updated_at`; unique key
      `(organisation_id, requirement_id)`; index on `requirement_id`; FK
      `organisation_id` -> `organisations(id)` and FK `requirement_id` ->
      `requirements(id)`, both `ON DELETE RESTRICT`). Additive and forward-only; alter
      no existing table.
- [x] 3.2 Add `RequirementScopeRow(Id, Title, Organisation, Requirement,
      Disposition)` to `ComplianceReadModels.cs`; add a `SoaInputs(Organisations,
      Scopes, Requirements, RequirementScopes)` read model carrying the four SoA inputs;
      add `RequirementScopes` to `ComplianceCounts` in positional order `(Standards,
      Controls, Requirements, Organisations, Scopes, RequirementScopes)`.
- [x] 3.3 Extend `IComplianceStore` with `GetRequirementScopesAsync`; in
      `MySqlComplianceStore.cs` implement it (select `id, title, organisation_id,
      requirement_id, disposition` ordered by `id`, mirroring `GetScopesAsync`) and add
      `(SELECT COUNT(*) FROM requirement_scopes)` to the counts query (matching the
      widened `ComplianceCounts` positional order). Also add
      `GetStatementOfApplicabilityInputsAsync` returning `SoaInputs`: read
      organisations, scopes, requirements, and requirement-scopes (each ordered by `id`)
      in one `IsolationLevel.RepeatableRead` transaction, mirroring `GetControlsAsync`,
      so the SoA inputs cannot straddle a concurrent importer commit.
- [x] 3.4 In `ImportPlan.cs`: add `RequirementScopeRowPlan(Id, ApiVersion, Title,
      Organisation, Requirement, Disposition)`, a `RequirementScopes` list flattened
      from `config.RequirementScopes`, and `RequirementScopeIds`.
- [x] 3.5 In `MySqlGitOpsImporter.cs`: add `UpsertRequirementScopesAsync` (copy
      `UpsertScopesAsync`); immediately after the scope prune-then-upsert step, prune
      absent requirement-scopes (reuse `DeleteAbsentAsync` on `requirement_scopes`)
      then upsert the new set, before the `control_requirements` replacement. The
      requirement-scope prune therefore precedes the absent-organisation and
      absent-requirement deletes, keeping the RESTRICT FKs safe. Keep one transaction.
      Update the class doc comment.

## 4. Persistence tests (test(persistence))

- [x] 4.1 Extend `MySqlIntegrationTests.cs`: build a `GitOpsConfig` with an
      organisation, a standard, a requirement, and a requirement-scope; migrate a
      fresh DB, import, and assert the persisted requirement-scope row and the
      requirement-scopes count read back. Update the existing `new ComplianceCounts(...)`
      assertion for the widened positional record.
- [x] 4.2 Extend the fresh-schema migration test to assert: `requirement_scopes`
      exists; its `id`/`organisation_id`/`requirement_id` use `utf8mb4_bin`; the
      unique key on `(organisation_id, requirement_id)`, the `requirement_id` index,
      and the FKs to `organisations` and `requirements` exist; and two
      requirement-scope ids differing only in case stay distinct.
- [x] 4.3 Add an integration test that removes an organisation and a requirement that
      each had a referencing requirement-scope in the prior state (removing the
      requirement-scope too) and asserts the import succeeds without an FK violation.
- [x] 4.4 Add a rename regression test: rename a requirement-scope's `id` while
      keeping its `(organisation, requirement)` pair and assert it persists under the
      new id (prune-then-upsert).
- [x] 4.5 Add an `ImportPlan` unit test (no MySQL) asserting requirement-scopes
      flatten and order correctly.
- [x] 4.6 Add an integration test for `GetStatementOfApplicabilityInputsAsync`:
      after importing a config with organisations, scopes, requirements, and a
      requirement-scope, assert the method returns all four collections consistently
      (each ordered by `id`) matching the individual reads.

## 5. Persistence write path (feat(persistence))

- [x] 5.1 Extend `IComplianceWriteStore` with
      `UpsertRequirementScopeDispositionAsync(id, title, organisation, requirement,
      disposition)` and `DeleteRequirementScopeAsync(id)`.
- [x] 5.2 Implement both in `MySqlComplianceWriteStore.cs`, copying the scope write
      methods: validate id/title non-blank, `TryParseDisposition`; check the
      organisation and requirement exist; enforce one row per `(organisation,
      requirement)` (select the existing id for the pair, fail if a different id holds
      it); upsert in a per-write transaction. Delete is a plain
      `DELETE FROM requirement_scopes WHERE id = @Id`.
- [x] 5.3 In `DeleteOrganisationAsync`, add a requirement-scope pre-check alongside the
      existing child-organisation and scope pre-checks: `SELECT COUNT(*) FROM
      requirement_scopes WHERE organisation_id = @Id`, returning `WriteResult.Fail`
      ("Cannot delete an organisation that still has requirement-scopes.") so the
      RESTRICT foreign key never surfaces as a raw DB error.

## 6. Web read and write surface (feat(web))

- [x] 6.1 In `ComplianceEndpoints.cs`: add `GET /requirement-scopes` (project `{ id,
      title, organisation, requirement, disposition }` from
      `store.GetRequirementScopesAsync`, ordered by id, 503 on store failure); add
      `requirementScopes` to the `compliance/status` `persisted` object (integer when
      reachable, null on outage) appended after `scopes`.
- [x] 6.2 In `StatementOfApplicability.cs`: add `SoaRequirementResolution(Requirement,
      Disposition, Resolution)`; add a `Requirements` list to `SoaNode`; extend
      `Resolve` to take `requirements` and `requirementScopes` and, for each node whose
      standard resolves `In`, compute per-requirement nearest-ancestor resolution keyed
      by `(organisation, requirement)`, emitting only requirements with an
      explicit/inherited requirement-scope (ordered by requirement id). Nodes resolving
      `Out`/`Undetermined` emit an empty list. Keep `Resolve` pure.
- [x] 6.3 In the SoA endpoint handler: read the four inputs in one snapshot via
      `store.GetStatementOfApplicabilityInputsAsync`, filter requirements to `standardId`
      in memory, and pass all four collections to `Resolve`; project each node's
      `requirements` list into the JSON response.
- [x] 6.4 In `StatementOfApplicabilityModel.OnGetAsync`
      (`Pages/Compliance/StatementOfApplicability.cshtml.cs`): replace the separate
      `GetOrganisationsAsync` + `GetScopesAsync` reads with the single
      `store.GetStatementOfApplicabilityInputsAsync` snapshot, filter requirements to
      `StandardId` in memory, and pass all four collections to `Resolve`, so the page
      shares the endpoint's one repeatable-read snapshot method instead of a second
      separately-read projection path. Keep `GetStandardsAsync` as its own read for the
      selector. Update `StatementOfApplicability.cshtml` to render each node's
      per-requirement exclusions. The existing store-unreachable in-page notice still
      covers the combined read (it throws through the same `IsStoreFailure` path).
- [x] 6.5 In `ComplianceWriteEndpoints.cs`: add
      `RequirementScopeInput(Id, Title, Organisation, Requirement, Disposition)` and
      register `PUT /requirement-scopes/{id}` and `DELETE /requirement-scopes/{id}`
      under the same admin policy, wired through `RunAsync` and the write-store
      methods (reuse the existing `WriteResult`/error mapping). Generalise the shared
      `Conflict()` problem detail so it is not scope-specific ("A scope already maps
      this organisation and standard."): make it kind-neutral (e.g. "The write conflicts
      with an existing record.") or keyed to the written resource, so a requirement-scope
      409 does not report scope wording. `RunAsync`'s catch runs for every write route.
- [x] 6.6 Update `FakeComplianceStore.cs` and the fake write store with settable
      requirement-scopes and the requirement-scopes count; implement
      `GetStatementOfApplicabilityInputsAsync` on the fake by routing through the
      existing `Guard` helper (so it throws when `Unreachable`, exactly like the other
      reads) and returning its settable
      organisations/scopes/requirements/requirement-scopes as a `SoaInputs`. This keeps
      both outage behaviours holding - the endpoint 503 problem response and the page
      in-page unreachable notice - since both now read through this Guard-routed method.
      Update every `new ComplianceCounts(...)` for the widened positional record (insert
      `RequirementScopes.Count` after `Scopes.Count`).

## 7. Web tests (test(web))

- [x] 7.1 Add web double tests: requirement-scopes endpoint serialization and id
      ordering; requirement-scopes read is 401 anonymous, 503 when the store is
      unreachable, and served in read-only mode; status `persisted` includes
      `requirementScopes` (integer reachable, null degraded).
- [x] 7.2 Add SoA projection tests over the pure `Resolve`: company-wide exclusion
      inherited by a department; department re-include overriding the company; exclusion
      ignored when the standard resolves `Out`; empty per-requirement list on
      `Out`/`Undetermined` nodes; per-requirement list ordered by requirement id; a
      requirement-scope bound to a requirement of another standard is absent from the
      requested standard's projection (the standard filter over `Requirement.standard`
      excludes it); and the compound-tree case where a parent resolves the standard
      `Out`, a child re-scopes the standard `In`, and the parent carries a
      requirement-scope `Out` for one of the standard's requirements - the child
      inherits that requirement-scope `Out` (marked `inherited`) under its own `In`
      standard, while the parent lists no per-requirement exclusions.
- [x] 7.3 Add write tests: `PUT /requirement-scopes/{id}` persists and reads back; a
      plain duplicate `(organisation, requirement)` that the write-store pre-check
      catches returns 422 (`WriteResult.Fail`), while a concurrent duplicate that races
      the pre-check onto the unique key (a `DbException` with SQLSTATE `23000`) returns
      409; unresolved reference or bad disposition returns 422; the endpoint is 409 in
      read-only mode; deleting an organisation still referenced by a requirement-scope is
      rejected with a problem body and leaves the store unchanged. This mirrors the
      existing scope write mapping (`ComplianceWriteEndpointTests`).
- [x] 7.4 Add SoA page-model tests over `StatementOfApplicabilityModel.OnGetAsync`
      through the fake store: the page renders per-requirement exclusions for an
      in-scope node (matching the endpoint projection from the same inputs), and an
      unreachable store sets the in-page notice (`StoreUnreachable`) rather than
      throwing - confirming the page's move to `GetStatementOfApplicabilityInputsAsync`
      keeps both the projection and the 503/notice behaviour.

## 8. CLI output (feat(cli))

- [x] 8.1 In `GitOpsCommands.cs`: include a `requirement-scope(s)` count in the
      validate and sync summaries and a `RequirementScopes (n):` section in
      `PrintPlannedState` printing `  - {Id}: {Title} -> {Organisation} /
      {Requirement} = {Disposition}`. CLI stays EE-free and cross-platform.

## 9. Fixtures and docs (docs)

- [x] 9.1 Add `examples/gitops/requirement-scopes.yaml` with a company-wide exclusion
      (an `ologist-products` RequirementScope marking a CE+ requirement `Out`) and a
      department re-include (`ologist-products-eng` marking the same requirement `In`).
      Add a standard-level scope to `examples/gitops/scopes.yaml` binding
      `ologist-products` to `std-cyber-essentials-plus` with disposition `In` so the
      requirement-scopes resolve under an `In` standard.
- [x] 9.2 Update `examples/gitops/README.md` (add the `RequirementScope` kind and
      `requirement-scopes.yaml` to the layout) and `docs/gitops.md` (document the
      `RequirementScope` kind: organisation + requirement + disposition, no `standard`
      field, unique per `(organisation, requirement)`, resolved under the standard).

## 10. Verification

- [x] 10.1 `dotnet build` (all projects) and `dotnet test` (unit/web tiers green
      without MySQL; integration tests skip cleanly when `FREEBOARD_TEST_DB` is unset).
- [x] 10.2 Run the persistence integration suite with `FREEBOARD_TEST_DB` set (via the
      test compose file) to exercise migration `009`, the requirement-scope importer
      step, the rename regression, and the RESTRICT-safe deletes.
- [x] 10.3 `freeboard gitops validate examples/gitops` exits 0 and the summary reports
      the requirement-scopes; `freeboard gitops apply examples/gitops --dry-run` prints
      the RequirementScopes section.
- [x] 10.4 `npx markdownlint-cli2 "**/*.md"` passes for changed Markdown, and
      `openspec validate add-requirement-scope-exclusions --strict` passes.
