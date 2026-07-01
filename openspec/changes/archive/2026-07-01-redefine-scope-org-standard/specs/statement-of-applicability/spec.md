## ADDED Requirements

### Requirement: Scope disposition resolves by nearest-ancestor inheritance

The system SHALL resolve an organisation node's disposition for a standard as
follows: if a Scope exists for that `(organisation, standard)` pair, its
disposition is the resolved value and is `explicit`; otherwise the resolved value
is the parent's resolved value and is `inherited`; if the node has no ancestor with
a Scope for that standard, the resolved value is `Undetermined`. `Undetermined`
SHALL be distinct from an explicit or inherited `Out`.

#### Scenario: Explicit disposition wins

- **WHEN** a node has a Scope of disposition `Out` for a standard
- **THEN** the node resolves to `Out`, marked `explicit`, regardless of its
  ancestors

#### Scenario: Child inherits nearest ancestor

- **WHEN** a company has disposition `In` for a standard and its department has no
  Scope for that standard
- **THEN** the department resolves to `In`, marked `inherited`

#### Scenario: No ancestor disposition is undetermined

- **WHEN** neither a node nor any ancestor has a Scope for a standard
- **THEN** the node resolves to `Undetermined`, distinct from `Out`

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
projection over the organisation tree, computed from the persisted organisations
and scopes and stored nowhere. For the given standard the projection SHALL include
every organisation node with its resolved disposition and whether that value is
`explicit`, `inherited`, or `Undetermined`. The endpoint SHALL be GET-only and
SHALL NOT be blocked by GitOps read-only mode. Node output SHALL be deterministically
ordered by `id`.

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
