## Context

The SoA page (`/compliance/statement-of-applicability`) is a read-only,
server-rendered projection. `StatementOfApplicabilityModel.OnGetAsync` reads the
SoA inputs in one repeatable-read snapshot (`GetStatementOfApplicabilityInputsAsync`
returning `SoaInputs`), resolves `StatementOfApplicability.Resolve` over the full
organisation tree, filters to the accessible/selected subtree, and renders one flat
`<tr data-node-id>` per node with a "Requirement deviations" column. The resolver is
pure (no I/O) and unit-tested.

The structural chain already exists in the persisted model:

- Organisation node -> `SoaNode` (already resolved, one per org).
- Requirement -> `RequirementRow` (`Standard == standardId`); an org's in-scope
  requirements are those the two-layer resolution leaves `In`. Today `SoaNode`
  carries only the deviations (`Requirements`), not the full in-scope list.
- Control -> `ControlRow.MapsTo` lists the requirement ids a control implements
  (`GetControlsAsync`); `ControlRow.Evaluation` is an optional roll-up rule.
  Control-to-requirement is standard-global, not per-org.
- Check -> `EvidenceCollectorRow.Control` and `AttestationTemplateRow.Control` name
  the control a collector or template is attached to (`GetEvidenceCollectorsAsync`,
  `GetAttestationTemplatesAsync`). Attach-point is standard-global.

Constraints: SSR-first; progressive disclosure must be Playwright-testable;
code-as-liability (smallest coherent change, reuse existing patterns); MIT only
(Core/Persistence/web); plain ASCII. Two established patterns are reused.
`Admin/CustomRoles.cshtml` is the `<tbody x-data="{ open: false }">` disclosure
precedent: a clickable header `<tr @@click>` and a detail `<tr x-show="open" x-cloak>`
with a rotating chevron - but it uses a clickable row with no button and no
`aria-expanded`. The native-button + `aria-expanded` precedent is the org-selector
`Shared/Components/OrgSelector/_Node.cshtml` (a `<button type="button">` toggle with
`:aria-expanded`). The SoA toggles follow `_Node`: native buttons with `aria-expanded`.

## Sources reconciled (Plan A / Plan B)

Two independent plans were written for this task. This design folds both under the
binding mediator decisions below.

- From Plan A (kept): render all four levels server-side and toggle visibility with
  Alpine; keep the projection pure and unit-tested; eager one-snapshot load with no
  N+1. Plan A's "extend `SoaInputs`" store shape is NOT kept - see "Store/query
  resolution".
- From Plan B (folded in): a single bulk snapshot read to avoid N+1; explicit
  per-level applicability inheritance (Organisation = existing resolver; Requirement
  resolved per org, requirement-scopes apply only when the standard resolves In and
  a standard `Out` dominates; Control inherits from its parent requirement and shows
  its `Evaluation` roll-up as metadata; Check inherits from its parent control and
  shows source kind/metadata); a check leaf that includes attestation-templates as
  well as collectors, tagged by kind; never expose attestation quiz answers; vendors
  are metadata only and not part of applicability; a standard change clears stale
  expansion state; preserve the existing stable test hooks and add deeper-level
  hooks. Plan B's structural instinct - keep the flat resolver intact and add a
  separate pure drill-down projection alongside it - is adopted.

### Divergences and resolutions

1. Disclosure mechanism. Plan A: fully server-rendered rows, Alpine `x-show`
   toggles visibility. Plan B: full server round-trips with the URL (`?open=`
   tokens) as the source of truth, rendering only currently-expanded rows.
   Resolved per mediator decision 2 in favour of Plan A: Alpine `x-show` over
   fully server-rendered rows, no new endpoints and no URL round-trip. Plan B's
   scaling concern (DOM size) is acknowledged under "Performance" and left as a
   documented, deferred escape hatch, not built now.

2. Check leaf. Plan A: evidence-collectors only (with an open question about
   templates). Plan B: collectors plus attestation-templates, tagged by source.
   Resolved per mediator decision 3: both, each row tagged Collector or
   Attestation.

3. Store/query shape. Plan A: extend `SoaInputs` and the existing
   `GetStatementOfApplicabilityInputsAsync`. Plan B: a new `SoaDrilldownInputs`
   read model plus a new `GetStatementOfApplicabilityDrilldownInputsAsync` store
   method. Resolved in favour of Plan B's separate method (see "Store/query
   resolution").

4. Projection shape. Plan A: extend `Resolve` and `SoaNode` in place (adding
   `InScopeRequirements`). Plan B: keep the flat resolver and add a separate pure
   drill-down projection. Resolved in favour of Plan B's structure per the
   mediator instruction to keep the existing flat `SoaNode` resolver and its
   passing tests intact and add the drill-down projection alongside it.

## Key Decisions (binding, durable)

These four decisions were fixed by the mediator and override any conflicting choice
in either source. They are durable design decisions, not open questions.

### Decision 1: "Check" means configured checks, not live evidence

A check is a configured verification source attached to a control: an
`EvidenceCollectorRow` or an `AttestationTemplateRow`. The projection does NOT read
live `evidence_checks` / `EvidenceCheckRow` and does NOT touch `IEvidenceStore`.

Rationale: the SoA is a read-only structural, config-time projection ("computed
from persisted organisations, scopes, requirements, and requirement-scopes and
stored nowhere"). Listing configured verifications keeps it structural and
GitOps-sourced, consistent with the product's GitOps-write / UI-read model. Live
`EvidenceCheckRow` data is per `(organisation, requirement)` run state; surfacing it
here would add an evidence read into a scope view, risk an N+1, and blur "in scope"
with "currently passing" - a category error. Live evidence belongs to a separate
assessment view.

### Decision 2: Disclosure is server-rendered rows toggled by Alpine

All four levels render into the initial GET HTML. Alpine toggles nested-row
visibility only: an `x-data="{ open: false }"` scope per expandable node (the
`Admin/CustomRoles.cshtml` `<tbody>` disclosure precedent), a native
`<button type="button">` toggle with `@@click` and `:aria-expanded` (the org-selector
`_Node.cshtml` precedent), `x-show`/`x-cloak`, and a rotating `aria-hidden` chevron.
The SoA toggles are native buttons with `aria-expanded`, not `CustomRoles`' clickable
row. There are NO new HTTP endpoints and NO URL `?open=` round-trip; toggling never
issues a request.

`<details>`/`<summary>` is kept only as a documented graceful-degradation fallback
note (see Accessibility), not the primary mechanism.

Rationale: SSR-first and lowest-liability - no new endpoints, no client fetch, no
serialization format, no opaque row-key encoding. Alpine is already bundled and
started (`app.js`). The DOM is deterministic and present up front, which makes the
Playwright drill-down assertions straightforward.

### Decision 3: Check leaf shows both collectors and attestation-templates, tagged

Each check row carries a kind tag: Collector (from `EvidenceCollectorRow`) or
Attestation (from `AttestationTemplateRow`). Both attach to a control by its
`Control` field.

Attestation checks show configuration/metadata only. Quiz answers are already
redacted at the read-store boundary (`AttestationTemplateRow.Quiz` is
`QuizItemView`, which has no answer property); the projection and page MUST NOT
surface any quiz answer.

### Decision 4: Default expansion is all collapsed

On first load every disclosure is collapsed. The selected organisation is NOT
auto-expanded. Because expansion is ephemeral client state (`x-data`), selecting a
different standard re-renders the page via a full GET with fresh `open: false`
scopes, so stale expansion state cannot persist across a standard change.

## Store/query resolution: a separate drill-down snapshot method (not extend `SoaInputs`)

Chosen: add a drill-down-specific read model and store method, leaving `SoaInputs`
and `GetStatementOfApplicabilityInputsAsync` unchanged.

- New read model `SoaDrilldownInputs(Organisations, Scopes, Requirements,
  RequirementScopes, Controls, Collectors, Templates, Vendors)` in
  `src/Freeboard.Persistence/ComplianceReadModels.cs`. Vendors are carried so a
  collector's `vendor_id` maps to a vendor title for display.
- New interface method `GetStatementOfApplicabilityDrilldownInputsAsync(ct)` on
  `IComplianceStore`, implemented in `MySqlComplianceStore` inside one
  `RepeatableRead` transaction and in the web-tests `FakeComplianceStore`.
- The MySQL method assembles, in that one snapshot:
  - controls joined with their `control_requirements` links (the resolved `maps_to`
    requirement ids), the same assembly `GetControlsAsync` performs;
  - evidence-collectors with their per-type `config` map deserialized, as
    `GetEvidenceCollectorsAsync` does;
  - attestation-templates with their `fields` deserialized and their quiz projected
    to the already-redacted `QuizItemView` (which has no answers), as
    `GetAttestationTemplatesAsync` does;
  - vendors (id and title), as `GetVendorsAsync` does, so a collector's `vendor_id`
    resolves to a vendor title rather than a raw id.

Why a separate method over Plan A's extend-`SoaInputs`:

- `SoaInputs` is not consumed only by the SoA page. It is also read by evidence
  ingest (`src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`, to check an
  organisation/requirement resolves `In`) and by the JSON endpoint
  (`src/Freeboard/Compliance/ComplianceEndpoints.cs`). Extending `SoaInputs` would
  add controls, collectors, and templates reads to those two hot paths, which
  neither needs. A drill-down-specific snapshot keeps that cost on the drill-down
  page only.
- The new method is a bounded set of list reads (organisations, scopes,
  requirements, requirement-scopes, controls with their `control_requirements`
  links, collectors, templates) in one repeatable-read transaction. The maps are
  standard-global and bounded (see Performance), so there is no per-node or per-pair
  query and no N+1. Reading them together preserves the no-straddle guarantee - the
  drill-down tree cannot straddle a concurrent importer commit - for the drill-down
  tree specifically.

## Projection: add a drill-down projection alongside the flat resolver

The existing `StatementOfApplicability.Resolve` and `SoaNode` are left unchanged so
their passing tests are untouched. A new pure projection is added in the same file
(`src/Freeboard/Compliance/StatementOfApplicability.cs`, MIT, no new file):

- `SoaDrilldownNode(string Id, string Title, string Kind, string? Parent,
  string Disposition, SoaResolution Resolution,
  IReadOnlyList<SoaRequirementNode> Requirements)` - the org scalar fields
  (id/title/kind/parent/disposition/resolution) projected from the resolved `SoaNode`,
  plus the requirement children. `ResolveDrilldown` copies those scalars from the
  `SoaNode` it already computes per node. `SoaNode` is not nested, so the drill-down
  node carries a single `Requirements` collection (the full in-scope enumeration) and
  never exposes the flat node's differently-typed deviation list here.
- `SoaRequirementNode(string Id, string Title, string Disposition,
  SoaResolution Resolution, IReadOnlyList<SoaControlNode> Controls)`. An excluded
  (`Out`) requirement is a leaf: `Controls` is empty, so only an `In` requirement
  carries controls.
- `SoaControlNode(string Id, string Title, string? Evaluation,
  IReadOnlyList<SoaCheckNode> Checks)` - `Evaluation` is the control roll-up rule
  shown as metadata.
- `SoaCheckNode(string Id, string Title, SoaCheckKind Kind, string Type,
  string? Frequency, string? Vendor)` - one per collector or template. Collector
  rows carry `Type`, `Frequency`, and an optional `Vendor` display (the vendor's
  title, or the raw id when no vendor row matches); attestation rows carry `Type`
  with `Frequency` and `Vendor` left null. The optional fields are explicit so
  implementation and tests do not diverge. Quiz answers are never surfaced.
- `enum SoaCheckKind { Collector, Attestation }` with lowercase wire names
  (`collector`, `attestation`) following the existing `SoaResolutionNames` style.

New method
`ResolveDrilldown(organisations, scopes, requirements, requirementScopes,
controls, collectors, templates, vendors, standardId) : IReadOnlyList<SoaDrilldownNode>`:

- Reuses `Resolve(...)` for each node's org-level disposition and resolution; the
  "no duplicated logic" reuse applies only at the org level. The full in-scope
  requirement enumeration with explicit/inherited/default provenance is new logic:
  the existing private `ResolveRequirements` emits requirement-level deviations only
  and never yields a requirement-level `Default` provenance, so `ResolveDrilldown`
  needs its own (or a shared, extracted) requirement enumeration.
- For each node whose standard resolves `In`, enumerates every requirement of the
  standard for that node (the full set, not only deviations), each tagged with its
  resolved `Disposition` (`In` or `Out`) and provenance
  (`explicit`/`inherited`/`default`). A requirement that resolves `In` carries its
  controls; an excluded (`Out`) requirement is a leaf and carries no controls. A node
  whose standard resolves `Out` carries no requirement children.
- Builds a `requirementId -> controls (each with its checks)` catalogue once
  (control-to-requirement and check-to-control are org-independent) and references
  it per node, so subtrees are not recomputed per organisation.
- Ordering is deterministic: requirements by `Id`, controls by `Id`, checks by
  `(Kind, Id)` (collectors before attestations, each by id).

### Applicability inheritance per level

- Organisation: the existing resolver's nearest-ancestor disposition and provenance.
- Requirement: resolved per org. Requirement-scopes apply only where the org's
  standard resolves `In`; a standard `Out` dominates (no requirement children).
- Control: inherits applicability from its parent requirement (controls have no
  independent scope). `Evaluation` is shown as metadata only.
- Check: inherits applicability from its parent control. Shows source kind and
  metadata only (collector: `Type`, `Frequency`, optional `Vendor`; attestation:
  `Type`). Vendors are metadata only and never affect applicability;
  vendor-scopes are not read.

## Page render and test hooks

The flat table body is replaced by a nested disclosure that preserves the existing
stable hooks and adds deeper-level ones:

- `<table class="soa-nodes" data-standard="@Model.StandardId">` (kept).
- One `<tbody x-data="{ open: false }">` per organisation; a header
  `<tr data-node-id="...">` (kept) with the toggle button, and a detail
  `<tr x-show="open" x-cloak>` holding a nested `<ul>` of requirement rows.
- Requirement rows are `<li data-requirement-id="...">` (kept), each its own
  `x-data="{ open: false }"` scope, expanding to a nested `<ul>` of controls.
- Control rows are `<li data-control-id="...">` (new), each expanding to a nested
  `<ul>` of check rows.
- Check rows are `<li data-check-id="..." data-check-kind="collector|attestation">`
  (new).
- `[data-active-scope]` (kept) is unchanged.

Reusing `<li data-requirement-id>` (not a new `<tr>`) keeps the existing selector
working. The `.cshtml.cs` passes `Controls`, `Collectors`, `Templates`, and `Vendors` from the
snapshot into `ResolveDrilldown`.

## Accessibility

Contract for the four-level tree:

- Each disclosure toggle is a real `<button type="button">` with
  `:aria-expanded="open ? 'true' : 'false'"` and an `sr-only` text label naming the
  row it controls (e.g. "Toggle requirement REQ-1"). Native buttons are
  keyboard-operable (Enter/Space) and focusable by default; no custom key handling.
- The chevron `<svg>` is `aria-hidden="true"` (decorative), matching the
  org-selector and custom-roles patterns.
- Collapsed content stays in the DOM (`x-show`, not `x-if`) for SSR, tests, and the
  no-JS reveal. Both `x-show` and the project's `[x-cloak]` hide with `display:none`,
  which removes content from the accessibility tree, so collapsed content is exposed
  to assistive technology only when its section is expanded. `x-cloak` also prevents
  a flash before Alpine hydrates.
- Disclosure semantics: each open/closed section's control carries `aria-expanded`
  reflecting its state; nested lists group by level for a coherent reading order.
- No-JS fallback: a `<noscript>` block neutralizes `x-cloak` scoped to the SoA tree
  (`.soa-nodes [x-cloak]{display:revert !important}`) so the fully server-rendered
  tree is visible and reachable without JavaScript. The reveal is intentionally
  scoped to `.soa-nodes` so it does not also reveal layout chrome (the mobile-nav
  overlay and account menu) that uses `x-cloak`. If that reveal proves insufficient,
  `<details>`/`<summary>` with the same `aria-expanded` contract is the documented
  alternative; it is not built by default.
- Covered by the existing `web-accessibility` capability; no change to that spec.

## Performance

- One snapshot, a fixed small number of list reads; the projection is
  O(nodes x requirements) plus a one-time control/check catalogue build. No
  per-node or per-pair query, so no N+1.
- DOM size at scale: because all four levels render up front (Decision 2), the HTML
  and DOM grow with (in-scope nodes x requirements x controls x checks) even when
  collapsed. For a very large "All Organisations" tree this HTML can be large - this
  is the trade-off accepted for SSR simplicity and testability.
- Mitigations in this change: the view is already scoped to the selected/accessible
  subtree, and rows default collapsed (Decision 4) so the visible surface is small
  even when the DOM is large. Toggling is client-only, so expansion is instant.
- Deferred escape hatch: if DOM size becomes a real problem, lazy-load the
  control/check sub-levels per expand (a GET partial handler). Documented, not
  built, to avoid speculative machinery.

## Verification / test strategy

- Unit (pure `ResolveDrilldown`): full in-scope requirement enumeration per node
  (not just deviations); requirement provenance (explicit/inherited/default);
  standard `Out` dominates (no requirement children); controls attached by
  `MapsTo`; checks attached by collector/template `Control`; both check kinds
  present and tagged; deterministic ordering (requirements, controls, checks by
  `(Kind, Id)`); a requirement with no mapped control and a control with no check
  render empty child levels; vendor non-involvement (a collector vendor is metadata
  and does not change applicability). The existing flat-`Resolve` tests stay as-is.
- Web (`WebApplicationFactory`, fake store): initial render shows organisation rows
  only (all collapsed); nested requirement/control/check rows are present in the SSR
  HTML but hidden; the `data-node-id`, `data-requirement-id`, `data-control-id`,
  `data-check-id`, `data-check-kind` hooks render; both check kinds appear;
  out-of-scope nodes expose no requirement children; inaccessible nodes are not
  rendered; selecting a different standard resets to all-collapsed; existing
  scoping, auth, read-only, and store-unreachable behaviours still hold. Implement
  the new drill-down method in `FakeComplianceStore` and seed controls, collectors,
  and templates through it. `SoaInputs` is unchanged, so the `OrgSelectionTests`
  inline `SoaInputs` constructor is untouched. Rewrite the existing
  `StatementOfApplicabilityPageTests` assertions that read the flat "Requirement
  deviations" cell (the `data-requirement-id=req-a` deviation-format text) that the
  disclosure migration breaks.
- E2E (Playwright, gated): the full click-path drill-down - open organisation ->
  requirements, open requirement -> controls, open control -> checks; assert
  `aria-expanded` flips and collapse hides; both check kinds visible; plus an axe
  accessibility audit on a fully expanded page. Reuse `E2ETestBase` seeding.
- Persistence (MySQL integration, gated): extend
  `tests/Freeboard.Persistence.Tests/MySqlIntegrationTests.cs` to seed controls (with
  `control_requirements`), collectors, and templates and assert
  `GetStatementOfApplicabilityDrilldownInputsAsync` returns them in one snapshot with
  resolved `maps_to`, deserialized collector config, template fields, and an
  answer-free quiz.
- Preserve existing SoA tests; update only the ones that assert the flat table.

## Migration Plan

Pure additive UI/projection change; no data migration, no schema change, no config
format change. Rollback is reverting the change; the persisted model and JSON
endpoint are untouched.

## Open Questions

None. The four decisions above are fixed. A future evidence/assessment view may add
live `EvidenceCheck` status; that is a separate feature and out of scope here.
