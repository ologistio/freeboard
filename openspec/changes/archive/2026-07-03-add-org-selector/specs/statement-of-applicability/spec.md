## MODIFIED Requirements

### Requirement: Statement of Applicability is a read-only projection

The web app SHALL serve a Statement of Applicability for a standard as a read-only
projection over the organisation tree, computed from the persisted organisations
and scopes and stored nowhere. For the given standard the projection SHALL include
every organisation node with its resolved disposition and whether that value is
`explicit`, `inherited`, or `Undetermined`. The endpoint SHALL be GET-only and
SHALL NOT be blocked by GitOps read-only mode. Node output SHALL be deterministically
ordered by `id`.

The projection SHALL always be computed over the full organisation tree so that
nearest-ancestor inheritance is correct. The `/compliance/statement-of-applicability`
view page SHALL then render only the nodes in scope for the active organisation
selection, bounded by the accessible set: when an organisation is selected, the
selected node and its descendants intersected with the accessible set; when the
selection is "All Organisations", every accessible node. Because the projection is
computed over the full tree before the view is scoped, a node whose disposition is
inherited from an ancestor above the selected node SHALL keep that inherited value.
The scoping SHALL be applied server-side so out-of-scope nodes are absent from the
rendered page. The page SHALL name the active organisation scope above the projection:
the selected organisation's title when one is selected, or "All Organisations" when
none is. The page SHALL derive its resolved selection from its own reads - its own
organisation list, its accessible set, and the selection cookie it reads itself - and
SHALL NOT take the resolved selection from the shared request-scoped selection
resolver, so a transient failure that degrades only the resolver's own read cannot
drop the page's scope to "All Organisations". The JSON endpoint
`GET /api/v1/freeboard/statement-of-applicability/{standardId}`
is unaffected and continues to return every node for the standard.

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

#### Scenario: Page scopes to the selected organisation subtree

- **WHEN** an organisation is the active selection and an authenticated user opens
  the Statement of Applicability page for a standard
- **THEN** the page renders only that organisation and its descendants and omits
  organisation nodes outside that subtree

#### Scenario: Inherited disposition from an ancestor above the selection is kept

- **WHEN** a company is marked `In` for a standard, its department has no scope, and
  the department is the active selection
- **THEN** the page renders the department as `In` `inherited`, resolved from the
  company that is outside the rendered subtree

#### Scenario: Page keeps its store-unreachable notice under an outage

- **WHEN** the store is unreachable and an authenticated user opens the Statement of
  Applicability page, which reads its standards, scopes, and organisation list
  directly from the store and derives its entire scope from its own reads - its
  accessible set from its own organisation read and its resolved selection by reading
  the selection cookie itself - consuming the shared server-side selection resolver for
  nothing
- **THEN** the page renders its store-unreachable notice rather than an empty table,
  driven by its own direct store reads failing, so the outage still surfaces on the
  page and is not mistaken for a healthy result with no organisations

#### Scenario: Organisations-only load failure still shows the notice

- **WHEN** the organisation load fails while the standards and scopes loads succeed
- **THEN** the page renders its store-unreachable notice rather than a healthy but
  empty table, because it reads its organisation list directly and does not take the
  selection resolver's degraded empty list

#### Scenario: All Organisations renders every node

- **WHEN** the active selection is "All Organisations" and an authenticated user
  opens the Statement of Applicability page for a standard
- **THEN** the page renders every accessible organisation node for the standard,
  ordered by `id`

#### Scenario: Page names the active scope

- **WHEN** an authenticated user opens the Statement of Applicability page with an
  organisation selected, and separately with no selection
- **THEN** the page names that organisation's title as the active scope above the
  projection in the first case, and names "All Organisations" in the second
