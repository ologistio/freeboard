# compliance-write Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: App-managed writes for organisations and scope dispositions

When the instance is not in GitOps read-only mode, the web app SHALL allow
creating, updating, and deleting organisations and scope dispositions through the
`/api/v1/freeboard/` API, persisted through a write abstraction over the same store
the read path uses. Writes SHALL enforce the same domain invariants as import:
`kind` in `Company`/`Department`, acyclic resolvable parents, scope references that
resolve, `disposition` in `In`/`Out`, and at most one scope per
`(organisation, standard)` pair. An invalid write SHALL be rejected with an RFC
7807 problem body and SHALL NOT modify the store.

#### Scenario: Create an organisation

- **WHEN** the instance is not in GitOps mode and a client posts a valid
  organisation
- **THEN** the organisation is persisted and readable through the read endpoints

#### Scenario: Set a scope disposition

- **WHEN** a client writes a scope disposition for an `(organisation, standard)`
  pair that has none
- **THEN** the disposition is persisted and appears in the Statement of
  Applicability projection

#### Scenario: Duplicate mapping rejected on write

- **WHEN** a client writes a second scope for an `(organisation, standard)` pair
  that already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Invalid parent rejected on write

- **WHEN** a client writes an organisation whose `parent` does not resolve or forms
  a cycle
- **THEN** the write is rejected with a problem body and the store is unchanged

### Requirement: Writes are blocked in GitOps read-only mode

When the instance is in GitOps read-only mode the compliance write endpoints SHALL
be rejected by the existing read-only middleware with HTTP 409 and its problem
body, exactly as other mutating routes are. The write endpoints SHALL NOT be marked
as auth endpoints and SHALL NOT be exempt.

#### Scenario: Write blocked in read-only mode

- **WHEN** GitOps read-only mode is on and a client posts to a compliance write
  endpoint
- **THEN** the request is rejected with HTTP 409 and the read-only problem body, and
  the store is not changed

