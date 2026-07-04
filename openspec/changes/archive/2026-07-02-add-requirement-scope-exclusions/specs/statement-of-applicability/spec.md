## ADDED Requirements

### Requirement: Requirement disposition resolves by nearest-ancestor inheritance under the standard

The system SHALL resolve an organisation node's disposition for a specific
`Requirement` (owned by a standard) in two layers. First it SHALL resolve the node's
disposition for the requirement's standard by the existing standard-level
nearest-ancestor rule. Then:

- If the standard resolves `Undetermined` at the node, the requirement resolves
  `Undetermined` (there is no in-scope standard to exclude a requirement from).
- If the standard resolves `Out` at the node, the requirement resolves `Out`, and
  requirement-scopes SHALL NOT be consulted: the whole standard, and thus every
  requirement, is out of scope.
- If the standard resolves `In` at the node, the system SHALL consult the
  requirement layer by nearest-ancestor inheritance keyed by `(organisation,
  requirement)`: if a `RequirementScope` exists for that node and requirement, its
  disposition is the resolved value and is `explicit`; otherwise the resolved value
  is the nearest ancestor's `RequirementScope` disposition for that requirement and
  is `inherited`; if no ancestor has a `RequirementScope` for that requirement, the
  requirement follows the standard and resolves `In`.

A requirement-level `In` SHALL NOT re-include a requirement whose standard resolves
`Out` or `Undetermined` at the node; the standard-level result dominates. Within an
`In` standard, a child node's explicit `RequirementScope` SHALL override an ancestor's
inherited one, so a department MAY re-include (`In`) a requirement its parent excluded
(`Out`) company-wide.

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

#### Scenario: Requirement is undetermined when the standard is undetermined

- **WHEN** a node resolves `Undetermined` for a standard
- **THEN** every requirement of that standard resolves `Undetermined` at the node,
  regardless of any `RequirementScope`

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

## MODIFIED Requirements

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the organisation tree, computed from the persisted organisations,
scopes, requirements, and requirement-scopes and stored nowhere. For the given
standard the projection SHALL include every organisation node with its resolved
standard disposition and whether that value is `explicit`, `inherited`, or
`Undetermined`. Each node whose standard resolves `In` SHALL additionally report its
per-requirement exclusions: the requirements of that standard whose
`(organisation, requirement)` nearest-ancestor resolution finds a `RequirementScope`,
each with its resolved disposition (`In` or `Out`) and whether that value is
`explicit` or `inherited`. A requirement of the standard that is not listed for a node
follows that node's standard disposition (`In`). A node whose standard resolves `Out`
or `Undetermined` SHALL report no per-requirement exclusions, because requirement
scopes are not applied there. The endpoint SHALL be GET-only and SHALL NOT be blocked
by GitOps read-only mode. Node output SHALL be deterministically ordered by `id`, and
each node's per-requirement list SHALL be ordered by requirement `id`.

Authentication precedes this read and shares the same backing store as the
compliance store. So the HTTP 503 unreachable-store response describes the case
where the request is authenticated and only the compliance store is unavailable to
it. A full database outage that also fails authentication surfaces first as an
authentication failure (HTTP 401 for the endpoint, a `/login` redirect for the page)
- the request never reaches the projection - not as this 503 response.

#### Scenario: Projection reflects the tree and dispositions

- **WHEN** an authenticated user requests the Statement of Applicability for a
  standard with a company marked `In` and a department left unstated
- **THEN** the response lists the company as `In` `explicit` and the department as
  `In` `inherited`, ordered by `id`

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

#### Scenario: Projection is served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user requests the
  Statement of Applicability
- **THEN** the request is served normally and is not rejected with the 409
  read-only response

#### Scenario: Unreachable store returns a problem response

- **WHEN** the store is unreachable and an authenticated user requests the Statement
  of Applicability
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception
