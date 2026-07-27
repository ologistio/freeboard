## MODIFIED Requirements

### Requirement: An upsert PUT authorizes both the stored and the requested organisation

A PUT on an existing scope (a standard-level or a requirement-level disposition) SHALL
authorize the write permission on BOTH the stored row's current owning organisation AND the
requested new organisation, because the upsert's `ON DUPLICATE KEY UPDATE` can change the
row's owning subject (or an organisation asset's `parent`); authorizing only the requested
organisation would let a caller with write on one organisation overwrite or MOVE a row
currently owned by another. The owning organisation of a scope row is read from the unified
`scopes` table's `subject_id` column (the former `scopes.organisation_id` and
`requirement_scopes.organisation_id`, now one column holding the organisation asset id),
because the two per-target tables merged into one `scopes` table.

Because both app-managed write routes now target that one `scopes` table, each route SHALL
be confined to its own target kind so an id cannot cross the target boundary, WITHOUT breaking
creation of a new scope. A PUT SHALL branch on the addressed `id`'s current existence: an
absent `id` creates a row of the route's own target kind, an `id` whose existing row is the
route's own target kind is updated, and an `id` whose existing row is a DIFFERENT target kind
is treated as not-found and SHALL NOT be converted. A DELETE SHALL affect only rows whose own
target column is set (the standard-level route `PUT`/`DELETE /scopes/{id}`,
`compliance.scope.write`, only `standard_id` rows; the requirement-level route
`PUT`/`DELETE /requirement-scopes/{id}`, `compliance.requirement-scope.write`, only
`requirement_id` rows). A caller holding one write permission therefore cannot read,
overwrite, or delete the other route's target-kind row by id, while an ordinary new-id PUT
still creates. Control-target scope rows are gitops-write-only and have NO app write path, so
neither route may create, modify, or delete them.

The stored owning organisation the PUT and DELETE authorize is read from that one `scopes`
table by `id`, so this same target-column confinement SHALL bind the stored-owner authorization
lookup itself: each route SHALL read the stored owner only from a row whose own target column
matches the route, treating a same-`id` row of a different target kind as absent. A wrong-kind
row is therefore never authorized against - the caller is neither granted the write nor 403'd
against that other-kind row's owning organisation; the write resolves to not-found (404), and
the other-kind row's owner is not disclosed through an authorization decision.

For an organisation PUT: creating a root organisation (no parent) SHALL require
`system.admin`; creating a child organisation SHALL authorize `org.write` on the parent;
reparenting an existing organisation SHALL authorize `org.write` on BOTH the current parent
and the new parent.

#### Scenario: Cross-org move is denied

- **WHEN** a caller with write on organisation A issues a PUT that would move an
  existing row from organisation B to A, and the caller lacks write on B
- **THEN** the write is denied and the row's owning organisation is unchanged

#### Scenario: Reparent authorizes both parents

- **WHEN** a caller reparents an organisation from parent P1 to parent P2
- **THEN** the write is permitted only if the caller holds `org.write` on both P1
  and P2 (or is a super-admin)

#### Scenario: One write permission cannot reach the other route's target-kind row

- **WHEN** a caller holding only `compliance.scope.write` addresses the id of a
  requirement-target scope row through `PUT`/`DELETE /scopes/{id}`, or a caller holding only
  `compliance.requirement-scope.write` addresses a standard-target row through
  `PUT`/`DELETE /requirement-scopes/{id}`
- **THEN** the row is treated as not-found and is neither deleted nor converted to the other
  target kind, because each route is confined by its own target column and a control-target
  row is reachable by neither route

### Requirement: Compliance reads narrow to the caller's authorized subtree

The org-scoped compliance reads SHALL be narrowed to the caller's accessible
organisation set through the `IOrgAccess` seam, whose default implementation
resolves that set by the rollout mode (see the rollout-mode requirement) instead
of narrowing unconditionally. Under `Observe` reads SHALL NOT be narrowed: the
accessible set SHALL be all persisted organisations for every caller regardless of
grants. Under `Compat` the accessible set SHALL be the union of organisation
subtrees on which the caller holds a read-granting role (all organisations for a
super-admin), and a caller with no grants SHALL reach the audited full read
fallback. Under `Enforce` the accessible set SHALL be that same subtree union (all
organisations for a super-admin) and a caller with no read-granting role SHALL have
an empty accessible set. The narrowed reads SHALL be `GET /organisations` (filtered
by id), the unified `GET /scopes` (filtered by subject readability - an organisation
subject through the accessible-organisation set, a vendor subject through its `owner`
edge, and a machine (or other parent-anchored) subject through its `parent` ancestry into
that set; the separate `GET /requirement-scopes` and `GET /vendor-scopes` reads are removed
and served by this one endpoint), and the Statement of Applicability JSON endpoint and
page (nodes resolved over the full tree then filtered to the accessible subtree so
inherited dispositions survive). The non-tenant catalog reads `GET /standards`,
`GET /controls`, and `GET /requirements` SHALL remain authenticated-only and unnarrowed.

#### Scenario: Org-scoped list is narrowed

- **WHEN** a caller whose accessible set is a strict subset of organisations reads
  `/organisations` or the unified `/scopes`
- **THEN** the response contains only rows in or whose subject is readable within the
  accessible set

#### Scenario: Super-admin sees the whole readable domain

- **WHEN** a super-admin reads any org-scoped compliance endpoint
- **THEN** the response contains the whole READABLE persisted set - every scope whose subject
  resolves and is anchored in the super-admin's whole-tree accessible set, including a scope
  with a live `Machine` subject (reached through the subject's `parent` ancestry into that
  whole-tree set) - while a scope whose subject is unresolved (no asset row, or a retired
  discovered asset) or unsupported (a deferred group subject) is still OMITTED even for the
  super-admin, consistent with the `compliance-web-read` fail-closed rule; such a scope surfaces
  only through the generic dangling-subject warning, never as a `/scopes` row

#### Scenario: Catalog reads stay global

- **WHEN** an authenticated caller reads `/standards`, `/controls`, or
  `/requirements`
- **THEN** the full catalog is returned regardless of organisation grants
