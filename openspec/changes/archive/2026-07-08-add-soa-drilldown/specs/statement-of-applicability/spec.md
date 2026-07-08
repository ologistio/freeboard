## ADDED Requirements

### Requirement: Statement of Applicability view supports hierarchical drill-down

The `/compliance/statement-of-applicability` view page SHALL present the projection
as a hierarchical, progressively disclosed tree with four levels in the order
Organisation -> Requirement -> Control -> Check: an organisation node, its in-scope
requirements, the controls mapped to each requirement, and the checks configured on
each control. Each level SHALL be collapsible and expandable independently of the
others, and SHALL default to collapsed on first load; the page SHALL NOT
auto-expand the selected organisation.

The disclosure SHALL be rendered server-side: the HTML returned by the initial GET
SHALL contain all four levels for the rendered nodes, and client script SHALL only
toggle the visibility of nested rows, never fetch additional data. Toggling a level
SHALL NOT issue a new request to the server. Selecting a different standard SHALL
re-render the page with all levels collapsed, so no stale expansion state carries
across a standard change.

The disclosure SHALL be keyboard operable and accessible: each expand/collapse
control SHALL be a native button element carrying an `aria-expanded` state that
reflects whether its section is open, and a text label identifying the row it
controls. Nested content SHALL remain in the DOM while collapsed (hidden, not
removed) for server-side rendering, tests, and the no-JavaScript reveal; because it
is hidden with `display:none`, it is exposed to assistive technology only when its
section is expanded. With JavaScript disabled the nested levels SHALL be reachable
rather than permanently hidden.

The JSON endpoint `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
SHALL be unchanged by this drill-down; the hierarchy is a page-only presentation.
All existing scoping, authorization-boundary, read-only, and store-unreachable
behaviours of the page SHALL continue to hold.

#### Scenario: Initial load shows organisation rows collapsed

- **WHEN** an authenticated user first views the Statement of Applicability for a
  chosen standard
- **THEN** the page shows the organisation nodes with every disclosure collapsed,
  including the selected organisation

#### Scenario: Organisation row expands to its requirements

- **WHEN** the user expands an in-scope organisation node
- **THEN** the page reveals that organisation's requirements, each tagged with its
  resolved disposition (`In` or `Out`) and provenance, without issuing a new server
  request

#### Scenario: Requirement row expands to its controls

- **WHEN** the user expands an in-scope requirement that has controls mapped to it
- **THEN** the page reveals the controls mapped to that requirement

#### Scenario: Control row expands to its checks

- **WHEN** the user expands a control that has checks configured on it
- **THEN** the page reveals the checks configured on that control, each tagged as a
  collector or an attestation

#### Scenario: Disclosure is server-rendered and present without JavaScript

- **WHEN** the initial page GET response is inspected before any client script runs
- **THEN** the HTML already contains the requirement, control, and check rows for
  the rendered nodes, and they are hidden rather than absent

#### Scenario: Toggle exposes accessible expanded state

- **WHEN** the user activates an expand/collapse control with the keyboard
- **THEN** the control is a focusable button whose `aria-expanded` value reflects
  the open or closed state of the section it controls

#### Scenario: Standard change clears stale expansion

- **WHEN** the user has expanded some nodes and then selects a different standard
- **THEN** the page re-renders with all disclosures collapsed

#### Scenario: Out-of-scope organisation node has no requirement children

- **WHEN** an organisation node resolves `Out` for the standard
- **THEN** the node exposes no in-scope requirement children to drill into, because
  no requirement of an out-of-scope standard is in scope

### Requirement: Statement of Applicability projection carries the requirement-control-check structure per in-scope node

For a chosen standard, a projection that backs the view page SHALL attach to each
organisation node whose standard resolves `In` the full list of that standard's
requirements (not only the node's requirement-level deviations), each with its
resolved disposition (`In` or `Out`) and provenance (`explicit`, `inherited`, or
`default`), including requirements excluded (`Out`) at the node. Each requirement that
resolves `In` SHALL carry the controls whose mapping (`maps_to`) includes that
requirement; each control SHALL carry the checks configured on it and its optional
evaluation roll-up as metadata; and each check SHALL be an evidence-collector or an
attestation-template attached to that control, tagged by kind. An excluded (`Out`)
requirement SHALL be a leaf: it carries no controls. A node whose standard resolves
`Out` SHALL carry no requirements at all. Requirements SHALL be ordered by requirement
`id`, controls by control `id`, and checks by kind then `id`.

A check SHALL expose configuration and metadata only. Attestation quiz answers SHALL
NOT be surfaced. Vendors SHALL NOT affect applicability; a collector's optional
vendor is metadata only, and this projection SHALL NOT read live evidence
(`evidence_checks`) or vendor-scopes.

This projection SHALL be added alongside the existing flat resolver, which SHALL be
left unchanged. The controls, evidence-collectors, and attestation-templates that
populate the structure SHALL be read in the same repeatable-read snapshot as the
organisations, scopes, requirements, and requirement-scopes, so the rendered tree
cannot straddle a concurrent importer commit.

#### Scenario: In-scope node lists every requirement tagged In or Out

- **WHEN** the projection is computed for a standard at a node that resolves the
  standard `In` and excludes one requirement `Out`
- **THEN** the node lists every requirement of the standard with its resolved
  disposition (`In` or `Out`) and provenance, ordered by requirement `id`, including
  the excluded one tagged `Out`

#### Scenario: Excluded requirement is a leaf with no controls

- **WHEN** a requirement resolves `Out` at an in-scope node and a control maps to it
- **THEN** the requirement appears tagged `Out` and carries no controls, so it renders
  as a leaf with no control children

#### Scenario: Requirement carries its mapped controls

- **WHEN** a control's `maps_to` includes a requirement of the standard that resolves
  `In`
- **THEN** that control appears under that requirement in the projection, carrying
  its evaluation roll-up as metadata

#### Scenario: Control carries its configured checks of both kinds

- **WHEN** an evidence-collector and an attestation-template each name a control as
  their attach-point and that control is mapped to an in-scope requirement
- **THEN** both appear as checks under that control in the projection, the collector
  tagged as a collector and the template tagged as an attestation

#### Scenario: Attestation check hides quiz answers

- **WHEN** an attestation-template check with quiz items is projected
- **THEN** the check carries the template's configuration and metadata but no quiz
  answer

#### Scenario: Requirement without controls and control without checks render empty child levels

- **WHEN** an in-scope requirement has no control mapped to it, or a control has no
  check configured on it
- **THEN** the projection carries that requirement or control with an empty child
  level rather than omitting it

#### Scenario: Structure inputs share the projection snapshot

- **WHEN** the page reads the inputs needed to build the drill-down
- **THEN** the controls, evidence-collectors, and attestation-templates are read
  together with the organisations, scopes, requirements, and requirement-scopes in
  one repeatable-read snapshot
