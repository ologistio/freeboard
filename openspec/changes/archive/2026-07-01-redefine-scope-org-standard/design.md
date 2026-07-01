## Context

The compliance domain models `Standard`, `Control`, and `Scope` as GitOps config
kinds imported read-only into MySQL and served by read-only web endpoints. There
is no Organisation, and `Scope` is a flat list of Control ids picked by hand. That
cannot answer the operative question - which organisation is in scope for which
standard, and what is in or out per org-unit - nor produce a Statement of
Applicability. This is MIT core business logic in `Freeboard.Core`,
`Freeboard.Persistence`, and the web app; nothing here is EE.

Current constraints that shape the design:

- GitOps mode is a single global flag (`Freeboard:GitOps:ReadOnly`). When on, a
  middleware 409s every mutating request except marked auth endpoints. There is no
  per-record provenance and no app write path for the domain today.
- Identifier columns use binary collation; identity is `id`, never `title`.
- The importer replaces the whole persisted set in one transaction, FK-safe.

## Goals / Non-Goals

**Goals:**

- Add Organisation as a recursive tree (`kind`: Company | Department; `parent`).
- Redefine Scope to a mapping `(organisation, standard, disposition)` with
  disposition `In` or `Out`, unique per organisation node per standard.
- Resolve a Statement of Applicability as a read-time projection over the org tree
  against a standard, using nearest-ancestor inheritance for unstated nodes.
- Support both persistence paths behind the existing global mode flag: GitOps
  import (read-only) and app-managed writes (when not in GitOps mode).

**Non-Goals:**

- No per-control applicability, exclusion justifications, `scopingRules` on
  Standard, changes to `Control.mapsTo`, Assets, or ownership modeling. See the
  proposal Non-goals.
- No change to the GitOps read-only mechanism itself.

## Decisions

### Scope = (organisation, standard, disposition), keyed by id, unique on the pair

Scope keeps its own stable `id` (identity, upsert key, future attributes) and gains
a unique constraint on `(organisation_id, standard_id)`. `disposition` is an enum
`In | Out` stored as a short string. The former `controls[]` list and its relation
table are removed.

- Alternative - drop Scope's id and use the composite pair as the primary key:
  rejected. A stable id matches the rest of the domain, keeps references simple,
  and leaves room for later scope attributes without a key migration.
- Alternative - keep `controls[]` alongside the mapping: rejected. Control
  applicability derives from the standard's catalogue; a hand-maintained list is
  the SoA in disguise and would drift.

### Sparse dispositions with nearest-ancestor inheritance, resolved at read time

A Scope row is recorded only for org nodes that make an explicit statement. A node
with no row for a standard inherits the disposition of its nearest ancestor that
has one; a node with no such ancestor resolves to `Undetermined`.

```
resolve(node, standard) =
  explicit disposition on (node, standard)          if a Scope row exists
  else resolve(parent(node), standard)              if node has a parent
  else Undetermined
```

- Alternative - materialise a row for every node in the subtree: rejected. Rows
  explode, YAML gets verbose, and a newly added department silently gets a row
  nobody chose. Sparse + inherit keeps declarations minimal and intent explicit.

### Statement of Applicability is a projection, not a table

The SoA for a standard is computed by walking the org tree and applying the
resolve rule per node. It is served read-only and stored nowhere. Each node in the
result carries its resolved disposition and whether that value is `explicit` or
`inherited` (or `Undetermined`).

- Alternative - persist a materialised SoA: rejected for now. The projection is
  cheap and always consistent with the underlying rows. Materialised subviews
  (e.g. managed assets) are deferred.

### Organisation is a self-referential tree; cycles rejected in Core

`organisations` has a nullable `parent_id` self-FK and a `kind` column. A validated
config MUST be acyclic; the `Freeboard.Core` validator rejects a parent cycle and a
parent id that names no organisation. The importer upserts organisations
parent-before-child (topological by depth) so the self-FK holds mid-transaction.

- Alternative - two types (Organisation, OrgUnit): rejected. One recursive type
  models group -> company -> department uniformly; `kind` carries the distinction.

### Dual persistence via the existing global mode flag

The same tables serve both paths. GitOps import (existing `IGitOpsImporter`,
whole-set replace) is the writer when GitOps mode is on, and the UI is read-only by
the existing middleware. When GitOps mode is off, a new app-managed write store
performs create/update/delete of organisations and scope dispositions through the
API. No per-record provenance; the global flag decides which path is live.

- Alternative - per-record source flag and simultaneous merge: rejected. The global
  read-only mode is already built and enforced server-side; per-record provenance
  is new surface with no named consumer.

### Single forward-only migration

One migration adds the `organisations` table, adds `organisation_id`,
`standard_id`, and `disposition` to `scopes`, and drops the `scope`->`controls`
relation table. `control` -> `maps_to` relation is unchanged.

## Risks / Trade-offs

- Parent cycle or dangling parent in config -> Core validator rejects both before
  import; DB self-FK alone would not catch a cycle.
- Self-FK ordering during whole-set import -> importer upserts organisations
  topologically (parents first) and deletes children first; validated config is
  acyclic so a stable order exists.
- Breaking schema and data change -> pre-1.0 alpha with no production data
  contract. Existing YAML using `Scope.controls` must be re-authored; the migration
  drops the old relation. Stated in the proposal Impact.
- `Undetermined` nodes in the SoA could read as "out" -> the projection marks
  Undetermined distinctly from explicit/inherited Out.
- App-managed writes are new surface -> kept minimal (organisations and scope
  dispositions only) and reuse the existing store patterns and mode gate.

## Migration Plan

1. Apply the forward-only migration via the operator-run migrate command (not at
   web startup, per existing rule).
2. Re-author any GitOps YAML: add `Organisation` documents, convert `Scope`
   documents to `(organisation, standard, disposition)`.
3. Rollback: forward-only, so no down migration. Restore from backup if an applied
   migration fails; the runner records nothing on partial failure and is
   re-attemptable.

## Open Questions

- Exact JSON shape of the SoA endpoint (flat node list vs nested tree). Resolve in
  implementation; both carry disposition + explicit/inherited/undetermined.
- Whether app-managed writes ship in this change or a fast follow. Included here as
  a distinct capability so it can be deferred in review without reworking the model.
