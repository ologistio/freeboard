## MODIFIED Requirements

### Requirement: App-managed writes for organisations and scope dispositions

When the instance is not in GitOps read-only mode, the web app SHALL allow creating,
updating, and deleting organisation assets, standard-level scope dispositions, and
requirement-level scope dispositions through the `/api/v1/freeboard/` API, persisted
through a write abstraction over the same store the read path uses. An organisation is an
`Asset` of `type: Company` or `type: Department` with a scalar `parent` edge; these writes
persist it with `source: declared` and validate against the merged `assets` table
(filtered to the `Company`/`Department` subset). Scope dispositions persist into the
unified `scopes` table: a standard-level write persists a scope row with the organisation
as `subject` and the standard as the target, and a requirement-level write persists a
scope row with the organisation as `subject` and the requirement as the target. Vendor
subjects remain gitops-write-only; there is no app-managed write for a vendor scope.

Because both write routes now operate on one `scopes` table by row `id`, each route SHALL
be confined to its own target kind so an unqualified `id` cannot cross the target boundary,
WITHOUT breaking the create path. On a PUT the route SHALL branch on the row's current
existence by its global `id`: when no `scopes` row has that `id`, it SHALL create a new row of
the route's own target kind (a `standard_id` row for the standard-level route, a
`requirement_id` row for the requirement-level route); when a row with that `id` already has
the route's own target column set, it SHALL update that row; and when a row with that `id` has
a DIFFERENT target column set (or is a control-target row), the route SHALL treat it as
not-found and SHALL NOT convert it from one target kind to another. A DELETE SHALL affect only
rows whose own target column is set (the standard-level route only `standard_id` rows, the
requirement-level route only `requirement_id` rows) and SHALL be not-found when the addressed
`id` is absent or is a wrong-kind row. Control-target rows are gitops-write-only and have no
app write path, so neither route may create, modify, or delete them. This keeps a caller
holding only one scope-write permission from reaching another target kind's row by its global
`id`, while an ordinary new-id PUT still creates.

The route's ENDPOINT SHALL apply the same target-column confinement to its stored-owner
authorization lookup, not just the store's write statement. A PUT's in-handler stored-owner
read and a DELETE's stored-owner authorization selector SHALL consider only a scope row whose
own target column matches the route (the standard-level route only a `standard_id` row, the
requirement-level route only a `requirement_id` row); a same-`id` row of a different target
kind SHALL be treated as absent (no stored owner) so the caller is NOT authorized - nor 403'd -
against that other-kind row's owning organisation. The write then reaches the not-found (404)
outcome, never a 403 against, or a silent authorization from, an unrelated target kind's row.

These app-managed writes are STRICTER than the gitops sync path, on purpose. The
gitops sync path tolerates a dangling or cyclic `parent` (and a dangling `owner` or a
dangling scope `subject`) as a NON-BLOCKING warning, so one uncoordinated config writer
cannot wedge a `sync`. The app CRUD endpoints are the opposite contract: a human editing
one node through the UI, where a self-parent, a cycle, a dangling parent, or deleting a
node that still has children or scopes is an immediate authoring error to reject at the
write, not a tolerated warning to reconcile later. So these writes SHALL enforce AT WRITE
TIME: `type` in `Company`/`Department`; a `parent` that resolves to an existing
`Company`/`Department` asset; no self-parent; no cycle; a scope `subject` that resolves to
an existing `Company`/`Department` asset; a scope target (standard or requirement) that
resolves; `disposition` in `In`/`Out`; a non-blank `justification` when the disposition is
`Out` (the generalized rule that every exclusion carries its rationale); at most one scope
per `(subject, standard)` pair for a standard-level write; and at most one scope per
`(subject, requirement)` pair for a requirement-level write. A requirement-level scope
write carries only `subject`, `requirement`, `disposition`, and (for `Out`)
`justification`; it has no `standard` field, because its standard is derived from the
requirement. An invalid write SHALL be rejected with an RFC 7807 problem body and SHALL NOT
modify the store.

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

Deleting an organisation asset that still has a child organisation asset or a scope bound
to it (a scope whose `subject` is that organisation) SHALL be rejected with a problem body
and SHALL NOT modify the store, so the underlying `ON DELETE RESTRICT` foreign key on the
scope target is never surfaced as a raw database error; the scope `subject` has no foreign
key, so this guard is enforced by the app counting referencing scopes, not by the
database. To delete such a node the author must first re-parent or remove its children and
detach its scopes.

That guard SHALL hold under concurrency, not only against references that already exist when
the delete starts. The delete and a concurrent app write that would create a reference to the
same organisation SHALL be SERIALIZED against each other, so AT MOST ONE of them takes effect.
Either the delete completes and the referencing write is then refused - because the
organisation no longer resolves, or as a retryable conflict - or the referencing write
completes and the delete is then rejected because a child or a scope now references the
organisation, or - when the store cannot order them - both are refused. A referencing write is
a scope write naming the organisation as `subject`, and an organisation write naming it as
`parent`. What is forbidden is the pair BOTH taking effect: the delete SHALL NOT remove an
organisation that a committed write references, and a referencing write SHALL NOT be accepted
against an organisation a committed delete removed.

The delete MAY be refused with a retryable conflict rather than an invalid-write rejection
when the store cannot complete the ordering, and a referencing write MAY be refused the same
way. A retryable conflict SHALL NOT be reported as a store failure, because the caller's
correct response is to retry, not to treat the store as unavailable.

The promise is bounded to writes that go through the app's write store. A writer reaching the
`assets` or `scopes` tables by another route is outside it: the scope `subject` carries no
foreign key by design, and the GitOps sync path deliberately TOLERATES a dangling subject as a
non-blocking warning. So a dangling subject remains a state the system handles rather than one
it declares impossible, and the compensating signals for it stay in place. What this
requirement adds is that no app-managed write is a way to produce one.

#### Scenario: Create an organisation asset

- **WHEN** the instance is not in GitOps mode and a client posts a valid
  `Company`/`Department` organisation
- **THEN** it is persisted as an `Asset` of that `type` with `source: declared` and is
  readable through the read endpoints

#### Scenario: Set a standard-level scope disposition

- **WHEN** a client writes a scope disposition for a `(subject, standard)` pair that has
  none, whose `subject` resolves to a `Company`/`Department` asset
- **THEN** the disposition is persisted as a unified scope row and appears in the
  Statement of Applicability projection

#### Scenario: Set a requirement-level scope disposition

- **WHEN** a client writes a scope disposition for a `(subject, requirement)` pair that
  has none, whose `subject` resolves to a `Company`/`Department` asset
- **THEN** the disposition is persisted as a unified scope row, readable through the
  `/scopes` read endpoint, and applied in the Statement of Applicability projection when
  the organisation's standard resolves `In`

#### Scenario: Out disposition without a justification is rejected on write

- **WHEN** a client writes a scope disposition of `Out` with no `justification` (or a
  whitespace-only one)
- **THEN** the write is rejected with a problem body and the store is unchanged, matching
  the gitops sync path, which also rejects an `Out` scope with no justification

#### Scenario: Duplicate mapping rejected on write

- **WHEN** a client writes a second scope for a `(subject, standard)` or `(subject,
  requirement)` pair that already has one
- **THEN** the write is rejected with a problem body and the store is unchanged

#### Scenario: A write route cannot reach another target kind's row by id

- **WHEN** a client addresses the id of a requirement-target scope row through
  `PUT`/`DELETE /scopes/{id}`, or the id of a standard-target row through
  `PUT`/`DELETE /requirement-scopes/{id}`, or a control-target row through either route
- **THEN** the row is treated as not-found (HTTP 404) and is neither deleted nor converted to
  the other target kind, because each route filters on its own target column
  (`standard_id`/`requirement_id`) and control-target rows have no app write path

#### Scenario: The write store's target-column filter is enforced in SQL

- **WHEN** the standard-route write store is asked, against the real store, to delete by the id
  of a requirement-target or control-target scope row, or to PUT (upsert) by that id (and
  symmetrically the requirement-route store by a standard-target or control-target id)
- **THEN** the DELETE's target-column-scoped statement (`... WHERE id=@Id AND standard_id IS
  NOT NULL` for the standard route, `requirement_id IS NOT NULL` for the requirement route)
  affects no row, and the PUT's global-id lookup finds the row is a wrong target kind and
  refuses to retarget it; both return the not-found result the endpoint maps to HTTP 404,
  mutating nothing

#### Scenario: A wrong-kind row owned by an unwritable org is not-found, not forbidden

- **WHEN** a caller holding only `compliance.scope.write` addresses, through
  `PUT`/`DELETE /scopes/{id}`, the id of a requirement-target scope row whose owning
  organisation the caller CANNOT write
- **THEN** the endpoint returns HTTP 404, NOT 403, because the standard route's stored-owner
  authorization lookup is filtered to `standard_id` rows and so finds no owner to authorize
  against; the requirement-target row's existence and its owning organisation are not disclosed,
  and the row is neither authorized against nor modified

#### Scenario: New-id PUT creates a scope of the route's target kind

- **WHEN** a client PUTs a scope disposition on `/scopes/{id}` (or `/requirement-scopes/{id}`)
  whose `id` names no existing `scopes` row
- **THEN** the write creates a new row of that route's own target kind (a `standard_id` row for
  `/scopes`, a `requirement_id` row for `/requirement-scopes`), rather than treating the absent
  id as not-found, so the target-column confinement does not regress ordinary scope creation
  into a 404

#### Scenario: Unresolved scope reference rejected on write

- **WHEN** a client writes a scope whose `subject` does not resolve to a
  `Company`/`Department` asset or whose target (standard or requirement) does not resolve,
  or whose `disposition` is not `In` or `Out`
- **THEN** the write is rejected with a problem body and the store is unchanged. The app
  write path is strict: unlike gitops sync, which tolerates a dangling scope `subject` as a
  non-blocking warning, the app write requires the subject to resolve to a
  `Company`/`Department` asset at write time

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

#### Scenario: Delete organisation blocked while a child or scope references it

- **WHEN** a client deletes an organisation asset that still has a child asset or a scope
  whose `subject` is that organisation
- **THEN** the write is rejected with a problem body and the store is unchanged, rather than
  leaving a child organisation whose `parent` no longer resolves, or a scope whose `subject`
  no longer resolves; both halves of the reference are guarded, not only the scope half

#### Scenario: A delete racing a scope write on the same subject leaves no orphan

- **WHEN** a client deletes an organisation asset while another client writes a scope naming
  that same organisation as `subject`, at any point during the delete
- **THEN** at most one of the two takes effect: either the organisation is deleted and the
  scope write is refused - because its subject no longer resolves, or as a retryable conflict
  when the store could not hold the write long enough to find that out - or the scope is
  written and the delete is rejected because a scope now references the organisation, or both
  are refused with a retryable conflict. No pairing exists in which both succeed, and no scope
  is left naming a subject the same operation removed

#### Scenario: A delete racing a child organisation write leaves no orphan

- **WHEN** a client deletes an organisation asset while another client writes an organisation
  naming that same organisation as `parent`, at any point during the delete
- **THEN** at most one of the two takes effect: either the organisation is deleted and the
  child write is refused - because its parent no longer resolves, or as a retryable conflict
  when the store could not hold the write long enough to find that out - or the child is
  written and the delete is rejected because a child organisation now references it, or both
  are refused with a retryable conflict. No pairing exists in which both succeed, and no asset
  is left naming a `parent` the same operation removed

#### Scenario: A write refused for ordering is a retryable conflict

- **WHEN** an organisation delete or a referencing write cannot be ordered against a
  concurrent write and the store abandons it
- **THEN** the caller receives the retryable conflict response, not the response that reports
  the store as unavailable, and the refused operation leaves no partial write of its own. The
  concurrent write it lost to MAY have changed the store, which is the point of refusing this
  one
