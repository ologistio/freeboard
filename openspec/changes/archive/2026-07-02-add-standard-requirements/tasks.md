## 1. Core model, loader, validator (feat(core))

- [x] 1.1 Add `Requirement` record to `ConfigModel.cs` (id, title, standard,
      theme, statement, guidance, citation_label, citation_url) and add
      `KindRequirement = "Requirement"` to `GitOpsSchema`.
- [x] 1.2 Add `Version`, `Authority`, `Publisher`, `SourceUrl` to the `Standard`
      record; add `Requirements` list to `GitOpsConfig`.
- [x] 1.3 Reword the stale `Control` XML doc comment ("a requirement under one or
      more standards" -> "an implemented control mapped to one or more
      requirements") now that a distinct `Requirement` kind exists and `maps_to`
      targets requirements.
- [x] 1.4 In `ConfigLoader.cs`: add `version`, `authority`, `publisher`,
      `source_url` to the Standard `SchemaKeys`; add a `Requirement` `SchemaKeys`
      entry (apiVersion, kind, id, title, standard, theme, statement, guidance,
      citation_label, citation_url); add the `apiVersion` alias override for
      `Requirement`; route `KindRequirement` in the switch; add `Requirement` to
      the unknown-kind error message.
- [x] 1.5 In `ConfigValidator.cs`: add `ValidateRequirements` (required id, title,
      standard, theme, statement, citation_label, citation_url; `citation_url` a
      well-formed absolute http/https URI; apiVersion check; per-kind duplicate id;
      `standard` resolves to a known Standard id) and wire the phases in a fixed
      order `ValidateStandards -> ValidateRequirements -> ValidateControls ->
      ValidateOrganisations -> ValidateScopes`. `ValidateStandards` produces the
      standard id set; `ValidateRequirements` consumes it (to resolve
      `Requirement.standard`) and produces the requirement id set; `ValidateControls`
      consumes the requirement id set. Repoint `Control.maps_to` validation to
      resolve each entry against that requirement id set (empty list rejected;
      dangling id rejected; duplicate id within one control rejected), mirroring the
      existing standard-id-set threading into control validation. Add Standard
      metadata validation: `version`/`authority` required and non-empty (whitespace
      -> error); `source_url` gets the URI-format check only when present and
      non-empty. The validator does NOT normalize optional fields (it never mutates
      the config); blank-to-null normalization for `publisher`/`source_url`/
      `guidance` is done in the mapping layer (task 3.4), mirroring
      `Organisation.Parent`.

## 2. Core tests (test(core))

- [x] 2.1 Add loader/validator fixtures under
      `tests/Freeboard.Core.Tests/fixtures/` covering a valid Requirement, a
      Requirement with an unknown `standard`, a missing required field, an unknown
      field on a Requirement, a malformed `citation_url`, a Control whose `maps_to`
      resolves to a Requirement, a Control with a dangling and a duplicate `maps_to`
      Requirement id, a Standard missing `version`/`authority`, a Standard with
      blank/omitted `publisher` and `source_url`, and a malformed `source_url`.
- [x] 2.2 Add unit tests: Requirement loads and routes distinctly from Control;
      Standard `version`/`authority` are required (non-empty) and
      `publisher`/`source_url` are optional (omitted or whitespace-only is absent,
      no error); omitted `guidance` reads back as absent/null; a Control resolves to
      requirements; a dangling or duplicate `maps_to` requirement id is rejected;
      each other validation rule reports the expected diagnostic; loader/validator
      still never throw or print.
- [x] 2.3 Update existing thin `Standard` fixtures that flow through validation so
      they still pass now that `version`/`authority` are required: add honest
      `version`/`authority` to every `Standard` used by
      `tests/Freeboard.Core.Tests/ConfigValidatorTests.cs` and `ConfigLoaderTests.cs`
      (and any CLI/web config used in tests). Retarget any test `Control.maps_to`
      that named a `Standard` id to a `Requirement` id so those tests stay valid.

## 3. Persistence: schema, models, store, importer (feat(persistence))

- [x] 3.1 Add `Migrations/008_standard_requirements.sql`: nullable `version`,
      `authority`, `publisher`, `source_url` columns on `standards`; new
      `requirements` table (binary-collation `id`/`standard_id`, `standard_id` FK to
      `standards` ON DELETE RESTRICT, index on `standard_id`, `theme`, `statement`,
      nullable `guidance`, `citation_label` NOT NULL, `citation_url` NOT NULL,
      `created_at`, `updated_at`); DROP `control_standards`; CREATE
      `control_requirements` (binary-collation `control_id`/`requirement_id`,
      composite PK, FK `control_id` -> `controls(id)` and FK `requirement_id` ->
      `requirements(id)` with ON DELETE CASCADE, index on `requirement_id`).
      Forward-only; `control_requirements` created after `requirements`.
- [x] 3.2 Add `RequirementRow` (with `CitationLabel`/`CitationUrl`) to
      `ComplianceReadModels.cs`; add `Version`, `Authority`, `Publisher`,
      `SourceUrl` to `StandardRow`; add `Requirements` to `ComplianceCounts` in
      positional order `(Standards, Controls, Requirements, Organisations, Scopes)`.
      `ControlRow.MapsTo` semantics change to `Requirement` ids (type unchanged).
- [x] 3.3 Extend `IComplianceStore` with `GetRequirementsAsync`; in
      `MySqlComplianceStore.cs` implement it, add the standards metadata columns to
      `GetStandardsAsync`, join `control_requirements` (not `control_standards`) in
      `GetControlsAsync`, and add the requirements count to the counts query
      (matching the widened `ComplianceCounts` positional order).
- [x] 3.4 In `ImportPlan.cs`: add `RequirementRowPlan` (id, api_version, title,
      standard, theme, statement, guidance, citation_label, citation_url) and a
      `Requirements` list; add a `StandardRowPlan` (id, api_version, title, version,
      authority, publisher, source_url) so standards no longer share the generic
      `DomainRow` upsert with controls; drop `ControlStandardRow` and add
      `ControlRequirementRow(ControlId, RequirementId)` flattened from
      `Control.maps_to` (kept `Distinct()`). Normalize the optional fields here:
      map an omitted-or-whitespace-only `publisher`, `source_url`, and
      `RequirementRowPlan.guidance` to null (`string.IsNullOrEmpty(...) ? null : ...`,
      as `Organisation.Parent` is already mapped) so an absent optional field
      persists as NULL.
- [x] 3.5 In `MySqlGitOpsImporter.cs`: add `UpsertStandardsAsync` (with metadata)
      and `UpsertRequirementsAsync`; upsert order standards, requirements, controls,
      organisations; keep scopes prune-then-upsert (rename safety); replace
      `control_requirements` join rows (delete all, insert new) after controls and
      requirements exist; delete absent requirements before absent standards; keep
      the whole sequence in one transaction, FK-safe. Update the class doc comment.

## 4. Persistence tests (test(persistence))

- [x] 4.1 Extend `MySqlIntegrationTests.cs`: build a `GitOpsConfig` with a standard
      (with metadata), requirements, and a control mapping to a requirement; migrate
      a fresh DB, import, and assert the persisted requirements, standard metadata,
      the control's `maps_to` requirement ids (round-tripped through
      `control_requirements`), and the requirements count read back. Update the
      existing `new ComplianceCounts(...)` assertion for the widened record
      (positional order standards, controls, requirements, organisations, scopes).
- [x] 4.2 Extend `MigrateEmptySchemaCreatesAllTablesWithBinaryCollation` to assert:
      `requirements` and `control_requirements` tables exist and `control_standards`
      does not; `requirements.id`/`standard_id` and the `control_requirements` join
      columns use `utf8mb4_bin`; the `requirements` FK to `standards` and the
      `standard_id` index exist; the `control_requirements` FKs to `controls` and
      `requirements` exist; the four new nullable `standards` columns exist; and two
      requirement ids differing only in case stay distinct.
- [x] 4.3 Add an integration test that removes a standard which had requirements in
      the prior state and asserts the import succeeds without an FK violation.
- [x] 4.4 Keep a regression test for a same-`(organisation, standard)` scope rename
      across `008` (prune-then-upsert): rename a scope's `id` while keeping its
      `(organisation, standard)` pair and assert the scope persists under the new id.
- [x] 4.5 Add an `ImportPlan` unit test (no MySQL) asserting requirements flatten
      and order correctly and `control_requirements` rows flatten from `maps_to`.

## 5. Web read surface (feat(web))

- [x] 5.1 In `ComplianceEndpoints.cs`: add `GET /requirements` (projecting
      `citation_label`/`citation_url` into a `citation: { label, url }` object); add
      `version`, `authority`, `publisher`, `source_url` to the standards
      projection; the controls projection `maps_to` now serves `Requirement` ids
      (no shape change); add `requirements` to the `compliance/status` `persisted`
      object (integer when reachable, null on outage) in the key order standards,
      controls, requirements, organisations, scopes.
- [x] 5.2 Update `FakeComplianceStore.cs` (web test double) with settable
      requirements, standard metadata, and the requirements count; update its
      `new ComplianceCounts(...)` for the widened positional record (insert
      `Requirements.Count` after `Controls.Count`).

## 6. Web tests (test(web))

- [x] 6.1 Add web double tests: requirements endpoint serialization and id
      ordering; standards response includes metadata keys; controls response
      `maps_to` carries requirement ids; status `persisted` includes `requirements`;
      requirements read is 401 when anonymous, 503 when the store is unreachable, and
      served in read-only mode.
- [x] 6.2 Assert the `compliance/status` degraded (store-unreachable) body includes
      `requirements: null`, consistent with the other counts degrading to null.
- [x] 6.3 Update positional `new StandardRow(Id, Title)` constructors for the
      widened record (add the four nullable metadata args) in
      `ComplianceEndpointTests.cs`, `StatementOfApplicabilityPageTests.cs`,
      `RouteMoveReadOnlyTests.cs`, and `StatementOfApplicabilityE2ETests.cs`.

## 7. CLI output (feat(cli))

- [x] 7.1 In `GitOpsCommands.cs`: include a requirements count in the validate,
      dry-run, and sync summaries and a "Requirements" section in the planned-state
      print. CLI stays EE-free and cross-platform.

## 8. CE+ fixture and docs (docs)

- [x] 8.1 Add the CE+ standard (v3.3 metadata; `source_url` the canonical
      requirements PDF) and the full 35-requirement set (7 Firewalls, 8 Secure
      Configuration, 5 Security Update Management, 11 User Access Control, 4 Malware
      Protection) to `examples/gitops/` (a `standards.yaml` entry plus a
      `requirements.yaml` file) using the authored statements in `design.md`. Upgrade
      the existing `std-cyber-essentials` and `std-soc2` placeholders with honest
      `version`/`authority` metadata (now required). Remap
      `examples/gitops/controls.yaml`: `ctrl-mfa.maps_to` ->
      `[req-ce-plus-user-access-control-04]` (dropping the `std-soc2` link, since
      SOC 2 has no authored requirements) and `ctrl-patching.maps_to` ->
      `[req-ce-plus-security-update-management-04, req-ce-plus-security-update-management-05]`.
- [x] 8.2 Update `examples/gitops/README.md` (add the `Requirement` kind and
      `requirements.yaml`; note `Control.maps_to` now names `Requirement` ids) and
      `docs/gitops.md` (document the Requirement kind, the new Standard metadata -
      required `version`/`authority`, optional `publisher`/`source_url` - and the
      `Control.maps_to` retarget to `Requirement` ids).
- [x] 8.3 Author the full 35-requirement CE+ set once in
      `examples/gitops/requirements.yaml` (the single source of the whole set), and
      give each test tier a small, layer-appropriate fixture in its own established
      style: Core uses inline YAML, Persistence uses domain builders, Web uses
      read-model rows. The tiers operate on different representations, so there is no
      duplicated 35-set to consolidate; a shared cross-tier fixture module is
      deliberately not added because it would introduce a new cross-project test
      dependency against the reference graph for negligible gain.

## 9. Verification

- [x] 9.1 `dotnet build` (all projects) and `dotnet test` (unit/web tiers green
      without MySQL; integration tests skip cleanly when `FREEBOARD_TEST_DB` is
      unset).
- [x] 9.2 Run the persistence integration and SMTP-independent suites with
      `FREEBOARD_TEST_DB` set (via the test compose file) to exercise migration
      `008`, the `control_requirements` swap, and the importer.
- [x] 9.3 `freeboard gitops validate examples/gitops` exits 0 and the summary
      reports the new requirements; `freeboard gitops apply examples/gitops
      --dry-run` prints the Requirements section.
- [x] 9.4 `npx markdownlint-cli2 "**/*.md"` passes for changed Markdown, and
      `openspec validate add-standard-requirements --strict` passes.
