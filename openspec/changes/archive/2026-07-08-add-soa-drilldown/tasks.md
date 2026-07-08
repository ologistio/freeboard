## 1. Persistence: add a drill-down snapshot read model and store method

Commit: `feat(persistence): read the SoA drill-down inputs in one snapshot`

- [x] 1.1 Add `SoaDrilldownInputs(Organisations, Scopes, Requirements,
  RequirementScopes, Controls, Collectors, Templates, Vendors)` in
  `src/Freeboard.Persistence/ComplianceReadModels.cs` with a doc comment. Vendors let
  a collector's `vendor_id` map to a vendor title. Leave `SoaInputs` unchanged.
- [x] 1.2 Add `GetStatementOfApplicabilityDrilldownInputsAsync(ct)` to
  `IComplianceStore.cs`, documenting that it reads all the drill-down lists together
  in one repeatable-read snapshot. Leave `GetStatementOfApplicabilityInputsAsync`
  unchanged so evidence ingest and the JSON endpoint are untouched.
- [x] 1.3 Implement it in `MySqlComplianceStore` inside one `RepeatableRead`
  transaction: organisations, scopes, requirements, requirement-scopes, controls
  joined with their `control_requirements` links (resolved `maps_to`),
  evidence-collectors with their `config` map deserialized, attestation-templates
  with `fields` deserialized and the quiz projected to the answer-free `QuizItemView`,
  and vendors (id and title).
- [x] 1.4 Implement `GetStatementOfApplicabilityDrilldownInputsAsync` on every
  `IComplianceStore` test double so they still compile: `FakeComplianceStore`
  (`tests/Freeboard.Web.Tests`) returns seeded controls, collectors, templates, and
  vendors; `CountingComplianceStore` (`tests/Freeboard.Web.Tests/OrgSelectionTests.cs`)
  returns an empty snapshot like its other unused members. The `SoaInputs` record and
  its constructors are unchanged, so do not touch the `OrgSelectionTests` inline
  `SoaInputs` constructor.

## 2. Compliance projection: add the drill-down projection alongside the flat resolver

Commit: `feat(compliance): project the SoA drill-down hierarchy`

- [x] 2.1 Add pure records `SoaDrilldownNode`, `SoaRequirementNode`,
  `SoaControlNode`, `SoaCheckNode` and `enum SoaCheckKind { Collector, Attestation }`
  (with lowercase wire names) in
  `src/Freeboard/Compliance/StatementOfApplicability.cs`. Leave `Resolve` and
  `SoaNode` unchanged.
- [x] 2.2 Add `ResolveDrilldown(...)` that reuses `Resolve` for org-level
  disposition/provenance and, per in-scope node, enumerates every requirement of the
  standard tagged with its disposition (`In`/`Out`) and provenance; a requirement that
  resolves `In` carries its controls (by `MapsTo`) and checks (collectors and
  templates by `Control`, tagged by kind); build the requirement -> control -> check
  catalogue once and reference it per node.
- [x] 2.3 Enforce the inheritance rules: standard `Out` yields no requirement
  children; an excluded (`Out`) requirement is a leaf with no controls; controls
  inherit from their requirement and carry `Evaluation` as metadata; checks inherit
  from their control and carry source metadata only (never quiz answers; the
  collector's vendor is shown by title, or its id when unknown, and never affects
  applicability). Deterministic ordering: requirements by `Id`, controls by `Id`,
  checks by `(Kind, Id)`.

## 3. Web page: render the hierarchical disclosure

Commit: `feat(web): drill down the Statement of Applicability by disclosure`

- [x] 3.1 Update `StatementOfApplicability.cshtml.cs` to read
  `GetStatementOfApplicabilityDrilldownInputsAsync` and pass its organisations,
  scopes, requirements, requirement-scopes, controls, collectors, templates, and
  vendors into `ResolveDrilldown`.
- [x] 3.2 Replace the flat table body in `StatementOfApplicability.cshtml` with the
  nested disclosure: per-organisation `<tbody x-data="{ open: false }">` (kept `soa-nodes`,
  `data-standard`, `tr[data-node-id]`); requirement `<li data-requirement-id>`,
  control `<li data-control-id>`, and check
  `<li data-check-id data-check-kind>` rows, each level its own `x-data` scope,
  reusing the custom-roles / org-selector toggle (native button, `:aria-expanded`,
  `x-show`/`x-cloak`, chevron). Default all collapsed; do not auto-expand the
  selected org.
- [x] 3.3 Tag each check row Collector or Attestation and show its metadata only;
  keep collapsed rows in the DOM; add the `<noscript>` reveal that neutralizes
  `x-cloak` for no-JS readers.
- [x] 3.4 Confirm no `app.js` change is needed (Alpine already registered/started).

## 4. Tests

Commit: `test(web): cover the SoA drill-down hierarchy`

- [x] 4.1 Drill-down projection unit tests in `tests/Freeboard.Web.Tests`
  (`StatementOfApplicabilityTests`): full in-scope requirement enumeration;
  provenance (explicit/inherited/default); standard `Out` dominance; control
  attachment by `MapsTo`; check attachment by collector/template `Control`; both
  kinds tagged; ordering (requirements by `Id`, controls by `Id`, checks by
  `(Kind, Id)`); empty child levels; vendor non-involvement. Leave the
  flat-`Resolve` tests unchanged.
- [x] 4.2 Page tests (`StatementOfApplicabilityPageTests`): initial render shows
  organisation rows only; nested rows and the `data-node-id`/`data-requirement-id`/
  `data-control-id`/`data-check-id`/`data-check-kind` hooks render in the SSR HTML;
  both check kinds shown; out-of-scope node has no requirement children;
  inaccessible node not rendered; standard change resets to all-collapsed; existing
  scoping/auth/read-only/unreachable tests still pass. Seed controls, collectors,
  and templates through the fake store's drill-down method.
- [x] 4.3 Rewrite the existing `StatementOfApplicabilityPageTests` assertions that
  read the flat "Requirement deviations" cell (the `data-requirement-id=req-a`
  deviation-format text) that the disclosure migration breaks; assert the nested
  disclosure rows instead.
- [x] 4.4 Extend the MySQL integration test
  (`tests/Freeboard.Persistence.Tests/MySqlIntegrationTests.cs`) to cover the
  drill-down snapshot: seed controls (with `control_requirements`), collectors,
  templates, and vendors and assert `GetStatementOfApplicabilityDrilldownInputsAsync`
  returns them with resolved `maps_to`, deserialized collector config, template
  fields, an answer-free quiz, and the vendors list.
- [x] 4.5 E2E (`tests/Freeboard.WebE2E/StatementOfApplicabilityE2ETests.cs`): full
  click path organisation -> requirements -> controls -> checks; assert
  `aria-expanded` flips and collapse hides; both check kinds visible; run an axe
  accessibility audit on a fully expanded page. Seed via the fake store.

## 5. Verify

- [x] 5.1 `dotnet build`.
- [x] 5.2 `dotnet test` (unit + web tiers; MySQL/E2E tiers skip cleanly when their
  gates are unset).
- [x] 5.3 Run the E2E suite with `FREEBOARD_TEST_E2E=1` and Chromium installed to
  confirm the disclosure interaction and axe audit.
- [x] 5.4 `openspec validate add-soa-drilldown` passes.
