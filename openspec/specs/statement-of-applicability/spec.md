# statement-of-applicability Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Scope disposition resolves by nearest-ancestor inheritance

The system SHALL resolve an organisation node's disposition for a standard as
follows: if a Scope exists for that `(organisation, standard)` pair, its
disposition is the resolved value and is `explicit`; otherwise, if an ancestor has a
Scope for that standard, the resolved value is the nearest such ancestor's disposition
and is `inherited`; if the node has no ancestor with a Scope for that standard, the
resolved value is `In` and is `default`. Standards are in scope by default: a node with
no Scope on the path from it to the root resolves `In`. A user opts a standard `Out` by
authoring a Scope with disposition `Out`; a descendant MAY override an opted-out ancestor
by authoring its own Scope `In`. The resolved standard disposition SHALL always be `In`
or `Out` and SHALL NOT be null; `default` is a provenance marker (no Scope on the path,
so the node takes the default `In`) and is distinct from `explicit` and `inherited`.

#### Scenario: Explicit disposition wins

- **WHEN** a node has a Scope of disposition `Out` for a standard
- **THEN** the node resolves to `Out`, marked `explicit`, regardless of its
  ancestors

#### Scenario: Child inherits nearest ancestor

- **WHEN** a company has disposition `Out` for a standard and its department has no
  Scope for that standard
- **THEN** the department resolves to `Out`, marked `inherited`

#### Scenario: No ancestor disposition defaults to In

- **WHEN** neither a node nor any ancestor has a Scope for a standard
- **THEN** the node resolves to `In`, marked `default`, and the resolved disposition
  is not null

#### Scenario: Descendant overrides an opted-out ancestor

- **WHEN** a company has disposition `Out` for a standard and a department under it has
  its own Scope `In` for that standard
- **THEN** the department resolves to `In`, marked `explicit`, overriding the ancestor's
  `Out`, while a sibling department with no Scope resolves `Out`, marked `inherited`

### Requirement: Statement of Applicability requires an authenticated user

The Statement of Applicability SHALL require an authenticated user, both on the
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` endpoint and on the
`/compliance/statement-of-applicability` read-only view page. Authentication (any
logged-in user) is sufficient; neither SHALL require the admin role. An anonymous
request to the endpoint SHALL return HTTP 401; an anonymous browser GET to the page
SHALL be redirected to `/login` rather than rendering the view. Authentication is
orthogonal to the GitOps read-only gate: both the endpoint and the page SHALL still
be served to an authenticated user when the instance is in read-only mode, and both
remain GET-only.

#### Scenario: Anonymous request to the endpoint is rejected

- **WHEN** an anonymous client requests
  `GET /api/v1/freeboard/statement-of-applicability/{standardId}`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Anonymous request to the page redirects to login

- **WHEN** an anonymous browser requests `/compliance/statement-of-applicability`
- **THEN** the response redirects to `/login` rather than rendering the view

#### Scenario: Authenticated user is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability endpoint or view page
- **THEN** the request is served normally and is not rejected with the 409
  read-only response

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the organisation tree, computed from the persisted organisations,
scopes, requirements, and requirement-scopes and stored nowhere. For the given
standard the projection SHALL include every organisation node with its resolved
standard disposition (always `In` or `Out`, never null) and whether that value is
`explicit`, `inherited`, or `default`. Each node whose standard resolves `In` SHALL
additionally report its per-requirement exclusions: the requirements of that standard
whose `(organisation, requirement)` nearest-ancestor resolution finds a
`RequirementScope`, each with its resolved disposition (`In` or `Out`) and whether that
value is `explicit` or `inherited`. A requirement of the standard that is not listed for
a node follows that node's standard disposition (`In`). A node whose standard resolves
`Out` SHALL report no per-requirement exclusions, because requirement scopes are not
applied under an out-of-scope standard. The endpoint SHALL be GET-only and SHALL NOT be
blocked by GitOps read-only mode. Node output SHALL be deterministically ordered by
`id`, and each node's per-requirement list SHALL be ordered by requirement `id`.

The projection SHALL always be computed over the full organisation tree so that
nearest-ancestor inheritance is correct, then filtered to the caller's accessible
set so a node whose disposition is inherited from an ancestor above the accessible
subtree keeps that inherited value. The `/compliance/statement-of-applicability`
view page SHALL render only the nodes in scope for the active organisation
selection, bounded by the accessible set: when an organisation is selected, the
selected node and its descendants intersected with the accessible set; when the
selection is "All Organisations", every accessible node. The scoping SHALL be
applied server-side so out-of-scope nodes are absent from the rendered page. The
page SHALL name the active organisation scope above the projection: the selected
organisation's title when one is selected, or "All Organisations" when none is. The
page SHALL derive its resolved selection from its own reads - its own organisation
list, its accessible set, and the selection cookie it reads itself - and SHALL NOT
take the resolved selection from the shared request-scoped selection resolver, so a
transient failure that degrades only the resolver's own read cannot drop the page's
scope to "All Organisations". The JSON endpoint
`GET /api/v1/freeboard/statement-of-applicability/{standardId}` SHALL likewise
resolve over the full tree and then return only the nodes in the caller's accessible
set, so the endpoint and the page apply the same authorization boundary.

Authentication precedes this read and shares the same backing store as the
compliance store. So the HTTP 503 unreachable-store response describes the case
where the request is authenticated and only the compliance store is unavailable to
it. A full database outage that also fails authentication surfaces first as an
authentication failure (HTTP 401 for the endpoint, a `/login` redirect for the page)
- the request never reaches the projection - not as this 503 response.

#### Scenario: Projection reflects the tree and dispositions

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard with a company marked `Out` and a department left unstated
- **THEN** the response lists the company as `Out` `explicit` and the department as
  `Out` `inherited`, ordered by `id`

#### Scenario: Unscoped node defaults to In

- **WHEN** an authenticated user requests the Statement of Applicability for a standard
  for which no organisation node has a Scope
- **THEN** every node resolves `In`, marked `default`, with a non-null disposition

#### Scenario: Projection reports per-requirement exclusions on in-scope nodes

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard where a company resolves `In` and marks one requirement `Out` company-wide
- **THEN** the company node lists that requirement with disposition `Out` marked
  `explicit`, and requirements it does not exclude are absent from the list (they
  follow the node's `In` standard disposition), the list ordered by requirement `id`

#### Scenario: Defaulted-in node reports per-requirement exclusions

- **WHEN** an authenticated user requests the Statement of Applicability for a standard
  where a company has no Scope (so it resolves `In` marked `default`) but marks one
  requirement `Out`
- **THEN** the company node resolves `In` `default` and lists that requirement with
  disposition `Out`, because requirement scopes are applied under any `In` standard
  regardless of whether the `In` is explicit, inherited, or default

#### Scenario: Out-of-scope node reports no per-requirement exclusions

- **WHEN** a node resolves `Out` for the standard
- **THEN** the node reports no per-requirement exclusions, because requirement scopes
  are not applied under an out-of-scope standard

#### Scenario: JSON endpoint returns only accessible nodes with inheritance preserved

- **WHEN** a caller whose accessible set is a strict subset of organisations
  requests the Statement of Applicability JSON endpoint for a standard
- **THEN** the response contains only the nodes in the accessible set, each keeping
  a disposition inherited from an ancestor above the accessible subtree

#### Scenario: Projection is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability
- **THEN** the request is served normally and is not rejected with the 409

### Requirement: Requirement disposition resolves by nearest-ancestor inheritance under the standard

The system SHALL resolve an organisation node's disposition for a specific
`Requirement` (owned by a standard) in two layers. First it SHALL resolve the node's
disposition for the requirement's standard by the standard-level nearest-ancestor rule,
which defaults `In`. Then:

- If the standard resolves `Out` at the node, the requirement resolves `Out`, and
  requirement-scopes SHALL NOT be consulted: the whole standard, and thus every
  requirement, is out of scope.
- If the standard resolves `In` at the node - whether `explicit`, `inherited`, or
  `default` - the system SHALL consult the requirement layer by nearest-ancestor
  inheritance keyed by `(organisation, requirement)`: if a `RequirementScope` exists for
  that node and requirement, its disposition is the resolved value and is `explicit`;
  otherwise the resolved value is the nearest ancestor's `RequirementScope` disposition
  for that requirement and is `inherited`; if no ancestor has a `RequirementScope` for
  that requirement, the requirement follows the standard and resolves `In`.

A requirement-level `In` SHALL NOT re-include a requirement whose standard resolves
`Out` at the node; the standard-level result dominates. Within an `In` standard, a child
node's explicit `RequirementScope` SHALL override an ancestor's inherited one, so a
department MAY re-include (`In`) a requirement its parent excluded (`Out`) company-wide.

#### Scenario: Company-wide exclusion is inherited by departments

- **WHEN** a company resolves `In` for a standard and marks a requirement `Out`
  company-wide, and a department under it has no `RequirementScope` for that
  requirement
- **THEN** the department resolves that requirement `Out`, marked `inherited`, while
  requirements it does not exclude follow the standard and resolve `In`

#### Scenario: Department re-includes a company-excluded requirement

- **WHEN** a company marks a requirement `Out` company-wide and a department under it
  marks the same requirement `In`
- **THEN** the department resolves that requirement `In`, marked `explicit`,
  overriding the inherited company `Out`

#### Scenario: Requirement scopes ignored when the standard is out

- **WHEN** a node resolves `Out` for a standard and a `RequirementScope` marks one of
  that standard's requirements `In` at or above the node
- **THEN** the requirement resolves `Out` (following the standard) and the `In`
  requirement-scope is not applied

#### Scenario: Requirement-scope of another standard is excluded from the projection

- **WHEN** the Statement of Applicability is resolved for one standard while a
  `RequirementScope` binds an organisation to a requirement of a different standard
- **THEN** that requirement-scope does not appear in the requested standard's
  projection, because requirement-scopes are filtered to the requested standard by
  their requirement's owning standard (`Requirement.standard`)

#### Scenario: Child re-including the standard inherits the parent's requirement-scope

- **WHEN** a parent node resolves the standard `Out`, a child under it explicitly
  scopes the standard `In`, and the parent carries a `RequirementScope` marking one of
  the standard's requirements `Out`
- **THEN** the parent reports no per-requirement exclusions (requirement-scopes are not
  applied under its `Out` standard), while the child, whose standard now resolves `In`,
  resolves that requirement `Out` marked `inherited` from the parent's requirement-scope
  (the requirement-layer nearest-ancestor walk ignores the intermediate standard `Out`
  at the parent)

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

### Requirement: Statement of Applicability surfaces per-collector evidence status

The `/compliance/statement-of-applicability` view page SHALL show, for each collector
check under a control, that collector's derived evidence status for the organisation node
it appears under. The status SHALL be read from the evidence read store
(`IEvidenceStore`) as a per-collector status keyed by `(organisation, requirement,
collector)`, and the page SHALL batch-read the statuses for all visible organisation
nodes in one call rather than issuing a read per node. The evidence read MAY use a
separate snapshot from the drill-down projection read: the status is advisory display and
is not part of the config-tree consistency guarantee.

The page SHALL render `Stale` distinctly from `Unknown`: `Stale` (the collector's latest
evidence is older than its cadence window plus grace) SHALL be shown as a "collection
stopped" state, and `Unknown` SHALL be shown as a separate "not collected" state. A
collector check that has no status from the store (an expected collector that never
produced evidence for that organisation and requirement) SHALL be derived as `Unknown` by
the page. `HardFailure`, `SoftFailure`, and `Passing` SHALL each render distinctly from
`Stale` and `Unknown`. Only collector checks SHALL carry a status; attestation checks
carry no evidence status.

The status surfacing SHALL NOT change the drill-down projection or its inputs: the
existing resolution, scoping, authorization-boundary, read-only, store-unreachable, and
JSON-endpoint behaviours of the page SHALL continue to hold, and the JSON endpoint SHALL
remain free of live evidence status.

#### Scenario: A stale collector renders as collection stopped

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is older than its cadence window plus grace
- **THEN** the page shows that collector check with a "collection stopped" status,
  distinct from a "not collected" status

#### Scenario: A never-collected collector renders as unknown

- **WHEN** a collector check is configured on a control for an in-scope requirement but
  has produced no evidence for the organisation
- **THEN** the page shows that collector check with an `Unknown` "not collected" status,
  distinct from "collection stopped"

#### Scenario: A fresh passing collector renders as passing

- **WHEN** a collector check's latest evidence for an in-scope organisation and
  requirement is within its cadence window plus grace and has no failing check
- **THEN** the page shows that collector check with a `Passing` status

#### Scenario: Visible organisation statuses are batch-read

- **WHEN** the page renders collector checks across several in-scope organisation nodes
- **THEN** the per-collector statuses for those organisations are read from the evidence
  store in a single batch call rather than one read per organisation

