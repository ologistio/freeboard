# compliance-write Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: App-managed writes for organisations and scope dispositions

When the instance is not in GitOps read-only mode, the web app SHALL allow creating,
updating, and deleting organisation assets, scope dispositions, and requirement-scope
dispositions through the `/api/v1/freeboard/` API, persisted through a write
abstraction over the same store the read path uses. An organisation is an `Asset` of
`type: Company` or `type: Department` with a scalar `parent` edge; these writes
persist it with `source: declared` and validate against the merged `assets` table
(filtered to the `Company`/`Department` subset), not a separate `organisations` table.

These app-managed writes are STRICTER than the gitops sync path, on purpose. The
gitops sync path tolerates a dangling or cyclic `parent` (and a dangling `owner`) as a
NON-BLOCKING warning, so one uncoordinated config writer cannot wedge a `sync`. The
app CRUD endpoints are the opposite contract: a human editing one node through the UI,
where a self-parent, a cycle, a dangling parent, or deleting a node that still has
children or scopes is an immediate authoring error to reject at the write, not a
tolerated warning to reconcile later. So these writes SHALL enforce AT WRITE TIME:
`type` in `Company`/`Department`; a `parent` that resolves to an existing
`Company`/`Department` asset; no self-parent; no cycle; scope and requirement-scope
references that resolve; `disposition` in `In`/`Out`; at most one scope per
`(organisation, standard)` pair; requirement-scope references (organisation and
requirement) that resolve; and at most one requirement-scope per
`(organisation, requirement)` pair. A requirement-scope write carries only
`organisation`, `requirement`, and `disposition`; it has no `standard` field, because
its standard is derived from the requirement. (Rejecting an unknown `standard` field
is a config-loader concern, not a write-API guarantee: the write DTO simply omits it.)
An invalid write SHALL be rejected with an RFC 7807 problem body and SHALL NOT modify
the store.

A write that REPAIRS an invalid node SHALL be allowed by the store guards, so the
strict app path never deadlocks on a state the tolerant gitops path produced. Setting
an organisation's `parent` to null (making it a root), or re-parenting to a resolvable
`Company`/`Department` asset that forms no cycle, always passes the cycle and
parent-exists guards; the guards reject only a write that would leave or create an
invalid state, never one that moves the node toward a valid one.

The repair is subject to the endpoint's authorization, which the store guards do not
supersede. Every organisation write requires `org.write` on the node itself. A
reparent additionally requires `system.admin` to set `parent` to null (move to root),
and `org.write` on BOTH the current (stored) parent and the new parent. So a caller
who can write the node is NOT guaranteed to be able to repair it: promoting it to root
needs `system.admin`, and re-homing it away from a stale (dangling) parent needs
`org.write` on that stale parent. The repair promise therefore holds for a caller
holding the required authorization - no store guard blocks breaking a gitops-created
dangling or cyclic parent - not for any node-writer unconditionally.

Deleting an organisation asset that still has a child organisation asset, a scope, or
a requirement-scope bound to it SHALL be rejected with a problem body and SHALL NOT
modify the store, so the underlying `ON DELETE RESTRICT` foreign key is never surfaced
as a raw database error. To delete such a node the author must first re-parent or
remove its children and detach its scopes.

#### Scenario: Create an organisation asset

- **WHEN** the instance is not in GitOps mode and a client posts a valid
  `Company`/`Department` organisation
- **THEN** it is persisted as an `Asset` of that `type` with `source: declared` and is
  readable through the read endpoints

#### Scenario: Set a scope disposition

- **WHEN** a client writes a scope disposition for an `(organisation, standard)` pair
  that has none, whose `organisation` resolves to a `Company`/`Department` asset
- **THEN** the disposition is persisted and appears in the Statement of Applicability
  projection

#### Scenario: Set a requirement-scope disposition

- **WHEN** a client writes a requirement-scope disposition for an
  `(organisation, requirement)` pair that has none, whose `organisation` resolves to a
  `Company`/`Department` asset
- **THEN** the disposition is persisted, readable through the requirement-scopes read
  endpoint, and applied in the Statement of Applicability projection when the
  organisation's standard resolves `In`

#### Scenario: Duplicate mapping rejected on write

- **WHEN** a client writes a second scope for an `(organisation, standard)` pair that
  already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Duplicate requirement-scope mapping rejected on write

- **WHEN** a client writes a second requirement-scope for an
  `(organisation, requirement)` pair that already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: Unresolved requirement-scope reference rejected on write

- **WHEN** a client writes a requirement-scope whose `organisation` does not resolve to
  a `Company`/`Department` asset or whose `requirement` does not resolve, or whose
  `disposition` is not `In` or `Out`
- **THEN** the write is rejected with a problem body and the store is unchanged. This
  matches the gitops sync path, which also rejects an unresolved requirement-scope
  `organisation` or `requirement` as a hard validation error. Only a dangling
  `Asset.parent`/`owner`, a `parent` cycle, and a missing required asset edge became
  non-blocking warnings; requirement-scope reference resolution stays a hard error on
  both paths.

#### Scenario: Invalid parent rejected on write

- **WHEN** a client writes an organisation asset whose `parent` does not resolve to a
  `Company`/`Department` asset, names itself, or forms a cycle
- **THEN** the write is rejected with a problem body and the store is unchanged, even
  though the gitops sync path would tolerate the same dangling or cyclic parent as a
  non-blocking warning - the app write path is strict

#### Scenario: Re-parenting a gitops-created invalid node to root is allowed for an authorized caller

- **WHEN** a gitops `sync` has left an organisation asset with a dangling or cyclic
  `parent`, and a client holding the required authorization writes that asset with
  `parent` set to null (requiring `system.admin`), or to a resolvable
  `Company`/`Department` asset that does not form a cycle (requiring `org.write` on
  both the stored parent and the new parent)
- **THEN** the write is accepted and the asset becomes a valid root (or child): no
  store guard blocks the repair, because setting `parent` to null always passes the
  cycle and parent-exists guards, so an authorized caller can always repair a state the
  tolerant gitops path produced

#### Scenario: Repair forbidden without the parent-side authorization

- **WHEN** a caller who can write an organisation asset but lacks `system.admin` tries
  to move it to root, or lacks `org.write` on its stale (dangling) stored parent tries
  to re-parent it
- **THEN** the endpoint rejects the write with a 403 problem body, because a reparent
  authorizes the parent side (`system.admin` for root, `org.write` on the stored and
  new parent) independently of the `org.write` on the node itself

#### Scenario: Delete organisation blocked while a child, scope, or requirement-scope references it

- **WHEN** a client deletes an organisation asset that still has a child asset, a
  scope, or a requirement-scope bound to it
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

