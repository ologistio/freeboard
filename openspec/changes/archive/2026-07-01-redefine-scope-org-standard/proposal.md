## Why

Freeboard's compliance domain has no notion of the organisation being assessed,
and its `Scope` is a hand-built bag of control ids. That cannot express the real
question users bring: "which organisation is in scope for which standard, and
what parts are in or out." A standard (Cyber Essentials Plus) is inert without an
organisation to apply to, and a scope boundary only means something relative to a
standard. We need to marry the two so the primary output - a Statement of
Applicability per organisation and org-unit - can be produced.

This is MIT (default). It is core compliance business logic, not an
enterprise-gated feature, so it lives in `Freeboard.Core`, `Freeboard.Persistence`,
and the web app - never in `Freeboard.Enterprise`.

## What Changes

- Introduce **Organisation** as a new root resource: a recursive tree
  (`parent` pointer) with a `kind` enum (`Company` | `Department`). Example:
  Ologist Products Ltd (Company) with an Engineering (Department) child.
- **BREAKING**: redefine **Scope** from a bag of control ids to a mapping
  `(organisation, standard, disposition)` where `disposition` is `In` or `Out`.
  Uniqueness is one disposition per organisation node per standard. The former
  `Scope.controls[]` list is removed.
- Scope dispositions are **sparse**: a node with no explicit row inherits its
  nearest ancestor's disposition; the tree root defaults to undetermined. Explicit
  rows override inheritance.
- Add a **Statement of Applicability** read view: a projection that walks the
  organisation tree against a standard and resolves in/out per node using the
  inheritance rule above. It is derived, not stored.
- Support persistence through **both** paths, gated by the existing global GitOps
  mode flag: the declarative GitOps config/import path (read-only when GitOps mode
  is on) and an app-managed write path (active when GitOps mode is off).
- `Standard` and `Control` are unchanged. A standard's control set remains the
  controls that `mapsTo` it.

## Capabilities

### New Capabilities

- `organisation-model`: the recursive organisation tree - identity, title,
  `kind` enum (`Company` | `Department`), and parent reference - across the config
  schema, persistence, and read surface.
- `statement-of-applicability`: the SoA projection that resolves the organisation
  tree against a standard into in/out per node, applying ancestor inheritance, and
  serves it as a read-only view.
- `compliance-write`: app-managed create/update/delete of organisations and scope
  dispositions via the API, active only when the instance is not in GitOps
  read-only mode.

### Modified Capabilities

- `gitops-config-format`: add the `Organisation` kind; redefine the `Scope` kind
  to `(organisation, standard, disposition)` and remove its `controls` list.
  **BREAKING** schema change.
- `compliance-persistence`: add an organisation table with a self-referential
  parent FK; redefine scope persistence to `(organisation, standard, disposition)`
  with a unique key on `(organisation, standard)`; extend the FK-safe import order
  to cover organisations. **BREAKING** store shape.
- `compliance-web-read`: expose organisation-tree and scope-disposition reads that
  the Statement of Applicability projection is built on.

## Non-goals

- No per-control applicability matrix, exclusion justifications, or free-text
  scope statements yet. The SoA v1 resolves to in/out per node only.
- No `scopingRules` on `Standard` and no change to `Control.mapsTo`. Deferred to
  the assets-era work.
- No Asset, AssetGroup, or "managed assets" materialised subview. Deferred.
- No ownership-percentage or legal-entity-relationship modeling. `kind` is the
  only organisation metadata for now.
- No change to the global GitOps mode mechanism itself; this change consumes the
  existing flag, it does not alter how read-only enforcement works.

## Impact

- `Freeboard.Core/GitOps/ConfigModel.cs`: new `Organisation` record and kind;
  redefined `Scope` record. `ConfigValidator` gains organisation and scope-mapping
  rules.
- `Freeboard.Persistence`: new organisation read model, redefined `ScopeRow`,
  extended `IComplianceStore` reads, extended GitOps importer, a new app-managed
  write store, and a forward-only migration for the schema change.
- `Freeboard` (web): new read endpoints for the organisation tree and the SoA
  projection; new write endpoints behind the GitOps mode gate; read view.
- `Freeboard.CLI`: `gitops validate`/`apply` cover the new kinds.
- Existing compliance data and any authored YAML that uses `Scope.controls[]` is
  broken by the schema change and must be re-authored. This is pre-1.0 business
  logic with no production data contract to preserve.
