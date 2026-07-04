## MODIFIED Requirements

### Requirement: App-managed writes for organisations and scope dispositions

When the instance is not in GitOps read-only mode, the web app SHALL allow
creating, updating, and deleting organisations, scope dispositions, and
requirement-scope dispositions through the `/api/v1/freeboard/` API, persisted
through a write abstraction over the same store the read path uses. Writes SHALL
enforce the same domain invariants as import: `kind` in `Company`/`Department`,
acyclic resolvable parents, scope references that resolve, `disposition` in
`In`/`Out`, at most one scope per `(organisation, standard)` pair, requirement-scope
references (organisation and requirement) that resolve, and at most one
requirement-scope per `(organisation, requirement)` pair. A requirement-scope write
carries only `organisation`, `requirement`, and `disposition`; it has no `standard`
field, because its standard is derived from the requirement. (Rejecting an unknown
`standard` field is a config-loader concern, not a write-API guarantee: the write DTO
simply omits it.) An invalid write SHALL be rejected with an RFC 7807 problem body and
SHALL NOT modify the
store. Deleting an organisation that is still referenced by a requirement-scope (as
well as by a child organisation or a scope) SHALL be rejected with a problem body and
SHALL NOT modify the store, so the underlying `ON DELETE RESTRICT` foreign key is never
surfaced as a raw database error.

#### Scenario: Create an organisation

- **WHEN** the instance is not in GitOps mode and a client posts a valid
  organisation
- **THEN** the organisation is persisted and readable through the read endpoints

#### Scenario: Set a scope disposition

- **WHEN** a client writes a scope disposition for an `(organisation, standard)`
  pair that has none
- **THEN** the disposition is persisted and appears in the Statement of
  Applicability projection

#### Scenario: Set a requirement-scope disposition

- **WHEN** a client writes a requirement-scope disposition for an
  `(organisation, requirement)` pair that has none
- **THEN** the disposition is persisted, readable through the requirement-scopes
  read endpoint, and applied in the Statement of Applicability projection when the
  organisation's standard resolves `In`

#### Scenario: Duplicate mapping rejected on write

- **WHEN** a client writes a second scope for an `(organisation, standard)` pair
  that already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Duplicate requirement-scope mapping rejected on write

- **WHEN** a client writes a second requirement-scope for an
  `(organisation, requirement)` pair that already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Unresolved requirement-scope reference rejected on write

- **WHEN** a client writes a requirement-scope whose `organisation` or `requirement`
  does not resolve, or whose `disposition` is not `In` or `Out`
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Invalid parent rejected on write

- **WHEN** a client writes an organisation whose `parent` does not resolve or forms
  a cycle
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Delete organisation blocked while a requirement-scope references it

- **WHEN** a client deletes an organisation that still has a requirement-scope bound
  to it
- **THEN** the write is rejected with a problem body and the store is unchanged, rather
  than surfacing the RESTRICT foreign-key error

### Requirement: Writes are blocked in GitOps read-only mode

When the instance is in GitOps read-only mode the compliance write endpoints SHALL
be rejected by the existing read-only middleware with HTTP 409 and its problem
body, exactly as other mutating routes are. This SHALL cover the organisation, scope,
and requirement-scope write endpoints. The write endpoints SHALL NOT be marked as
auth endpoints and SHALL NOT be exempt.

#### Scenario: Write blocked in read-only mode

- **WHEN** GitOps read-only mode is on and a client posts to a compliance write
  endpoint (organisation, scope, or requirement-scope)
- **THEN** the request is rejected with HTTP 409 and the read-only problem body, and
  the store is not changed
