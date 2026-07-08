## Why

The Statement of Applicability (SoA) page renders every organisation as a single
flat row with a "Requirement deviations" column. A reviewer cannot see, from that
page, which requirements a standard imposes on an in-scope organisation, which
controls implement each requirement, or which checks verify each control. The
structural chain Organisation -> Requirement -> Control -> Check already exists in
the persisted model but is not navigable. This change makes it navigable by
progressive disclosure so an assessor can drill from an organisation down to the
configured checks that evidence its controls.

## What Changes

- Replace the flat SoA results table with a hierarchical, expand/collapse view.
  The disclosure hierarchy is Organisation -> Requirement -> Control -> Check: an
  organisation node expands to its in-scope requirements, a requirement expands to
  the controls mapped to it, and a control expands to the checks configured on it.
- A "check" is a configured verification source attached to a control: an
  evidence-collector (`EvidenceCollectorRow`) or an attestation-template
  (`AttestationTemplateRow`). Each check row is tagged by kind (Collector or
  Attestation). This is NOT live evidence (`EvidenceCheckRow` / `IEvidenceStore`):
  the SoA stays a structural, config-time projection.
- Add a pure drill-down projection alongside the existing flat `SoaNode` resolver
  (the flat resolver and its tests are left intact). The new projection attaches,
  per in-scope organisation node, its full in-scope requirement list (not only the
  deviations listed today), the controls mapped to each requirement, and the checks
  configured on each control. Requirement-level provenance
  (`explicit`/`inherited`/`default`) is preserved.
- Read the drill-down tree in a new single-snapshot store method
  (`SoaDrilldownInputs` / `GetStatementOfApplicabilityDrilldownInputsAsync`) that
  returns organisations, scopes, requirements, requirement-scopes, controls,
  evidence-collectors, and attestation-templates in one repeatable-read snapshot,
  preserving the "cannot straddle a concurrent importer commit" guarantee for the
  drill-down tree. The shared `SoaInputs` / `GetStatementOfApplicabilityInputsAsync`
  is left unchanged, so evidence ingest and the JSON endpoint are untouched.
- Render the disclosure server-side. All four levels are present in the initial
  GET HTML. Alpine.js toggles nested-row visibility only, reusing the established
  `<tbody x-data="{ open: false }">` pattern from `Admin/CustomRoles.cshtml` and the
  org-selector `_Node.cshtml`. No new endpoints, no URL round-trip, no client data
  fetching. Default state is all collapsed.
- Keep the JSON endpoint `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
  unchanged. Drill-down is a page-only presentation enhancement.

This is additive drill-down over the existing projection, not a rewrite. The
resolution/inheritance rules are unchanged; only a new projection shape and the
page render are added.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `statement-of-applicability`: adds requirements for the hierarchical drill-down
  presentation on the `/compliance/statement-of-applicability` view page and for a
  projection carrying the requirement -> control -> check structure per in-scope
  node. The existing resolution, scoping, authorization-boundary, and JSON-endpoint
  requirements are unchanged.

## Impact

- Affected code (all MIT, in the web app and persistence layer):
  - `src/Freeboard/Compliance/StatementOfApplicability.cs` (new pure drill-down
    projection alongside the existing flat resolver).
  - `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml` and
    `.cshtml.cs` (hierarchical render; pass controls, collectors, templates).
  - `src/Freeboard.Persistence/ComplianceReadModels.cs` (new `SoaDrilldownInputs`
    read model), `IComplianceStore.cs` (new
    `GetStatementOfApplicabilityDrilldownInputsAsync`), `MySqlComplianceStore.cs`
    (new drill-down snapshot read).
  - `src/Freeboard/assets/js/app.js` unchanged (Alpine already registered).
- Affected tests: SoA drill-down projection unit tests, web page tests, the fake
  compliance store, and the Playwright E2E suite (assertions move from a flat table
  to nested disclosure rows). Existing flat-resolver unit tests are unchanged.
- No new runtime dependency. No EE code: the SoA is MIT (Core/Persistence/web).

## Non-goals

- No live evidence results. The "Check" leaf lists the collectors and
  attestation-templates configured to verify a control (the structural checks), not
  each collector run's pass/fail `EvidenceCheck` outcomes. `IEvidenceStore` is not
  touched. Rendering live evidence status belongs to a separate evidence/assessment
  view and is out of scope here.
- No attestation quiz answers. Attestation checks show configuration/metadata only;
  quiz answers are already redacted at the read-store boundary (`QuizItemView`) and
  MUST NOT be surfaced.
- Vendors are not involved in applicability. A collector's optional vendor is
  metadata only; vendor-scopes are not read for this projection.
- No change to the JSON endpoint, the resolution/inheritance rules, the scoping or
  authorization boundary, or the organisation selector.
- No lazy/AJAX loading, no new HTTP handlers, no URL `?open=` round-trip, no
  per-expand server round-trips.
- Not an EE feature; nothing moves into `src/Freeboard.Enterprise`.
