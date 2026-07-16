## ADDED Requirements

### Requirement: Dangling scope subject surfaces a non-blocking warning at resolution

The Statement of Applicability resolution SHALL treat a scope whose `subject` does not
resolve to a live asset - no asset row has that id, or the row is a discovered asset in the
`Retired` state - as a NON-BLOCKING condition: it SHALL surface a warning ("rule
targets a resource that does not currently exist", covering a retired asset and a
not-yet-discovered one) on the `/compliance/statement-of-applicability` view page and
SHALL NOT fail the projection. The unresolved-subject warning scan SHALL cover EVERY unified
scope regardless of its target kind - standard-target, requirement-target, AND
control-target - so no target kind is exempt from the warning. Only the disposition
RESOLUTION stays limited to standard-level and requirement-level org scopes: a control-target
org scope contributes no node disposition (control-level resolution is a non-goal), but a
control-target org scope whose subject is unresolved STILL surfaces the same generic warning
on the SoA page. The page notice SHALL be generic: it SHALL NOT disclose the
scope id or the unresolved subject id to an ordinary caller, because the unresolved subject
has no authorization anchor to check readability against; any detailed-id surface would be
system-admin-only and is out of scope for this change. A scope with a dangling subject does
not contribute a node disposition (no org node matches its subject) and is otherwise ignored
by the resolution. The JSON endpoint SHALL keep its shape; the warning is a page-level
notice. This warning concerns only a subject that resolves to no live asset at all; a scope
whose subject DOES resolve - a `Vendor` asset or any other non-organisation-tree subject - is
simply not consulted by the standard-level and requirement-level resolution and is not a
dangling-subject warning, because its subject exists.

#### Scenario: A scope with a vanished subject warns without failing

- **WHEN** the Statement of Applicability is resolved for a standard while a scope targets
  that standard (or one of its requirements) with a `subject` id that no asset defines
- **THEN** the projection is served, the affected node dispositions resolve as if that
  scope were absent, and the page shows a generic non-blocking warning ("rule targets a
  resource that does not currently exist") that does NOT name the scope id or the
  unresolved subject id to an ordinary caller (any detailed-id surface would be
  system-admin-only and is out of scope here)

#### Scenario: Control-target scope with a vanished subject also warns on the page

- **WHEN** the Statement of Applicability page is rendered while a control-target organisation
  scope has a `subject` id that no asset defines
- **THEN** the same generic non-blocking warning ("rule targets a resource that does not
  currently exist") is shown on the page, even though a control-target scope contributes no
  node disposition, because the warning scan is not exempted by target kind

#### Scenario: Dangling subject does not block a sync or the projection

- **WHEN** an asset that was a scope subject is removed so the scope's subject dangles
- **THEN** the sync succeeds (the subject has no foreign key), the Statement of
  Applicability still resolves, and the dangling subject surfaces only as a non-blocking
  warning at sync (CLI) and at resolution (page)

## MODIFIED Requirements

### Requirement: Scope disposition resolves by nearest-ancestor inheritance

The system SHALL resolve an organisation node's disposition for a standard as
follows: if a standard-level scope exists for that node (a `Scope` whose `subject` is that
organisation node and whose target is that standard), its disposition is the resolved
value and is `explicit`; otherwise, if an ancestor has such a scope for that standard, the
resolved value is the nearest such ancestor's disposition and is `inherited`; if the node
has no ancestor with a scope for that standard, the resolved value is `In` and is
`default`. Standards are in scope by default: a node with no scope on the path from it to
the root resolves `In`. A user opts a standard `Out` by authoring a `Scope` targeting that
standard with disposition `Out`; a descendant MAY override an opted-out ancestor by
authoring its own standard-level `Scope` `In`. The resolved standard disposition SHALL
always be `In` or `Out` and SHALL NOT be null; `default` is a provenance marker (no scope
on the path, so the node takes the default `In`) and is distinct from `explicit` and
`inherited`.

#### Scenario: Explicit disposition wins

- **WHEN** a node has a scope of disposition `Out` targeting a standard
- **THEN** the node resolves to `Out`, marked `explicit`, regardless of its
  ancestors

#### Scenario: Child inherits nearest ancestor

- **WHEN** a company has disposition `Out` for a standard and its department has no
  scope for that standard
- **THEN** the department resolves to `Out`, marked `inherited`

#### Scenario: No ancestor disposition defaults to In

- **WHEN** neither a node nor any ancestor has a scope for a standard
- **THEN** the node resolves to `In`, marked `default`, and the resolved disposition
  is not null

#### Scenario: Descendant overrides an opted-out ancestor

- **WHEN** a company has disposition `Out` for a standard and a department under it has
  its own scope `In` for that standard
- **THEN** the department resolves to `In`, marked `explicit`, overriding the ancestor's
  `Out`, while a sibling department with no scope resolves `Out`, marked `inherited`

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the organisation tree, computed from the persisted organisations,
scopes, and requirements and stored nowhere. Its scope inputs SHALL come from the unified
`scopes` table: a standard-level disposition is a scope targeting the standard, and a
requirement-level disposition is a scope targeting one of the standard's requirements. For
the given standard the projection SHALL include every organisation node with its resolved
standard disposition (always `In` or `Out`, never null) and whether that value is
`explicit`, `inherited`, or `default`. Each node whose standard resolves `In` SHALL
additionally report its per-requirement exclusions: the requirements of that standard
whose `(subject, requirement)` nearest-ancestor resolution finds a requirement-targeting
scope, each with its resolved disposition (`In` or `Out`) and whether that value is
`explicit` or `inherited`. A requirement of the standard that is not listed for a node
follows that node's standard disposition (`In`). A node whose standard resolves `Out` SHALL
report no per-requirement exclusions, because requirement scopes are not applied under an
out-of-scope standard. The endpoint SHALL be GET-only and SHALL NOT be blocked by GitOps
read-only mode. Node output SHALL be deterministically ordered by `id`, and each node's
per-requirement list SHALL be ordered by requirement `id`. A scope whose `subject` does
not resolve to any asset SHALL NOT fail the projection and SHALL surface a non-blocking
warning on the view page (see the dangling-subject requirement).

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
  for which no organisation node has a scope
- **THEN** every node resolves `In`, marked `default`, with a non-null disposition

#### Scenario: Projection reports per-requirement exclusions on in-scope nodes

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard where a company resolves `In` and marks one requirement `Out` company-wide
- **THEN** the company node lists that requirement with disposition `Out` marked
  `explicit`, and requirements it does not exclude are absent from the list (they
  follow the node's `In` standard disposition), the list ordered by requirement `id`

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
  requirement-targeting scopes SHALL NOT be consulted: the whole standard, and thus every
  requirement, is out of scope.
- If the standard resolves `In` at the node - whether `explicit`, `inherited`, or
  `default` - the system SHALL consult the requirement layer by nearest-ancestor
  inheritance keyed by `(subject, requirement)`: if a scope targeting that requirement
  exists for that node, its disposition is the resolved value and is `explicit`;
  otherwise the resolved value is the nearest ancestor's requirement-targeting scope
  disposition for that requirement and is `inherited`; if no ancestor has a scope for
  that requirement, the requirement follows the standard and resolves `In`.

A requirement-level `In` SHALL NOT re-include a requirement whose standard resolves
`Out` at the node; the standard-level result dominates. Within an `In` standard, a child
node's explicit requirement-targeting scope SHALL override an ancestor's inherited one, so
a department MAY re-include (`In`) a requirement its parent excluded (`Out`) company-wide.

#### Scenario: Company-wide exclusion is inherited by departments

- **WHEN** a company resolves `In` for a standard and marks a requirement `Out`
  company-wide, and a department under it has no scope targeting that requirement
- **THEN** the department resolves that requirement `Out`, marked `inherited`, while
  requirements it does not exclude follow the standard and resolve `In`

#### Scenario: Department re-includes a company-excluded requirement

- **WHEN** a company marks a requirement `Out` company-wide and a department under it
  marks the same requirement `In`
- **THEN** the department resolves that requirement `In`, marked `explicit`,
  overriding the inherited company `Out`

#### Scenario: Requirement scopes ignored when the standard is out

- **WHEN** a node resolves `Out` for a standard and a scope marks one of that standard's
  requirements `In` at or above the node
- **THEN** the requirement resolves `Out` (following the standard) and the `In`
  requirement scope is not applied

#### Scenario: Requirement scope of another standard is excluded from the projection

- **WHEN** the Statement of Applicability is resolved for one standard while a scope
  targets a requirement of a different standard
- **THEN** that scope does not appear in the requested standard's projection, because
  requirement-targeting scopes are filtered to the requested standard by their
  requirement's owning standard (`Requirement.standard`)

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
(`evidence_checks`) or vendor-subject scopes.

This projection SHALL be added alongside the existing flat resolver, which SHALL be
left unchanged. The controls, evidence-collectors, and attestation-templates that
populate the structure SHALL be read in the same repeatable-read snapshot as the
organisations, the unified scopes, and requirements (one unified `scopes` read, not a
separate requirement-scopes input), so the rendered tree cannot straddle a concurrent
importer commit.

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
  together with the organisations, the unified scopes, and requirements in
  one repeatable-read snapshot
