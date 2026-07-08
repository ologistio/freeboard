## MODIFIED Requirements

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
