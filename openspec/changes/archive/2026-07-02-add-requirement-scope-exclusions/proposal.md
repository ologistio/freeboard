## Why

Scope today is all-or-nothing per standard: a `Scope` marks an organisation node
`In` or `Out` for a whole `Standard`, resolved down the organisation tree by
nearest-ancestor inheritance. Since the last change a `Standard` now decomposes
into `Requirement` kinds, but there is no way to say "this standard applies to us,
except these specific requirements". Real Statements of Applicability are
per-requirement: an organisation adopts a standard company-wide yet carves out
individual requirements that do not apply, and a department may carve out (or
re-include) requirements differently from its parent. This change adds
requirement-level scoping, layered under the existing standard-level scope and
resolved with the same nearest-ancestor inheritance.

## What Changes

- Add a new config kind `RequirementScope`: a mapping from one `Organisation` to
  one `Requirement` carrying a `disposition` (`In` or `Out`), authored exactly like
  `Scope` (id, title, organisation, requirement, disposition). It is a scope at
  requirement granularity. The owning `Standard` is derived from the requirement
  (`Requirement.standard`), so a `RequirementScope` does not restate the standard;
  its uniqueness key is `(organisation, requirement)`.
- Resolve a requirement's disposition for an organisation node by nearest-ancestor
  inheritance over `RequirementScope`, identical to standard-level resolution but
  keyed by `(organisation, requirement)`, and layered UNDER the standard result: a
  requirement-level scope is only consulted when the standard resolves `In` at that
  node; when the standard resolves `Out` or `Undetermined` the requirement follows
  the standard (see design D3). Disposition is symmetric `In`/`Out` so a department
  can re-include (`In`) a requirement its parent excluded company-wide (`Out`).
- Extend the Statement of Applicability projection
  (`GET /api/v1/freeboard/statement-of-applicability/{standardId}` and the
  `/compliance/statement-of-applicability` page) so each organisation node whose
  standard resolves `In` also reports its per-requirement exclusions (the
  requirements resolved `explicit`/`inherited` `Out` or re-included `In`), keeping
  the payload proportional to the number of exclusions, not nodes x requirements.
- Add the JSON read surface: `GET /api/v1/freeboard/requirement-scopes` (the raw
  persisted rows, mirroring `GET /scopes`) and a `requirementScopes` count in
  `GET /api/v1/freeboard/compliance/status`.
- Add app-managed writes (`PUT`/`DELETE /api/v1/freeboard/requirement-scopes/{id}`)
  mirroring the existing scope write endpoints, enforcing the same invariants as
  import.
- Extend loader, validator, persistence (new `requirement_scopes` table plus
  migration `009`), the GitOps importer, the read store, the web read surface, and
  the CLI GitOps summary to carry the new kind.
- Author example fixtures: an `examples/gitops/requirement-scopes.yaml` with a
  company-wide exclusion and a department re-include, plus a CE+ standard-level
  scope so the exclusions have an `In` standard to sit under.

This is pre-1.0 (`freeboard.io/v1alpha1`). Migration `009` is purely additive: a new
`requirement_scopes` table with foreign keys to `organisations` and `requirements`.
No existing table or column changes. No wire field is removed or renamed; the
`requirementScopes` count and the per-node requirement resolutions are additive.

## Capabilities

### New Capabilities

None. This follows the repo's layer-oriented capability convention exactly as the
Organisation, Scope, and Requirement kinds did: the new kind extends the same layer
capabilities rather than introducing a feature-oriented `requirement-scope`
capability. A dedicated capability would duplicate the config-format, persistence,
read, write, and resolution layers with no distinct owner, against
code-as-liability.

### Modified Capabilities

- `organisation-model`: adds a `RequirementScope` model requirement (binds one
  organisation to one requirement with a disposition, unique per
  `(organisation, requirement)`, references resolve), mirroring the existing Scope
  model requirement. The org tree remains the inheritance backbone.
- `gitops-config-format`: adds the `RequirementScope` kind to the schema, the kind
  enumerations, the loader routing, and the validation rules.
- `statement-of-applicability`: adds requirement-level nearest-ancestor resolution
  under the standard, and extends the read-only projection so each in-scope node
  reports its per-requirement exclusions.
- `compliance-persistence`: adds a `requirement_scopes` table (FKs to
  `organisations` and `requirements`, unique `(organisation_id, requirement_id)`)
  via migration `009`, extends the read store, importer order, read models, and
  counts.
- `compliance-web-read`: adds the `requirement-scopes` read endpoint and the
  `requirementScopes` count in the status summary.
- `compliance-write`: adds app-managed create/update/delete of requirement-scope
  dispositions, enforcing the same invariants as import.

## Impact

- Code: `Freeboard.Core/GitOps` (`ConfigModel`, `ConfigLoader`, `ConfigValidator`);
  `Freeboard.Persistence` (`ComplianceReadModels`, `MySqlComplianceStore`,
  `IComplianceStore`, `IComplianceWriteStore`, `MySqlComplianceWriteStore`,
  `GitOps/ImportPlan`, `GitOps/MySqlGitOpsImporter`,
  `Migrations/009_requirement_scopes.sql`); `Freeboard`
  (`Compliance/StatementOfApplicability`, `Compliance/ComplianceEndpoints`,
  `Compliance/ComplianceWriteEndpoints`); `Freeboard.CLI` (`GitOpsCommands` summary
  and planned-state output); web test doubles (`FakeComplianceStore`, fake write
  store).
- Data: new `requirement_scopes` table applied via `freeboard system migrate` (the
  web app never auto-migrates). No existing table is altered.
- Licensing: MIT. This is core compliance business logic and lives only in
  `Freeboard.Core`, `Freeboard.Persistence`, and the web app. Nothing goes in
  `Freeboard.Enterprise`; `Freeboard.Agent` and `Freeboard.CLI` stay EE-free.
- Fixtures: `examples/gitops/` gains `requirement-scopes.yaml` and a CE+
  standard-level scope; test fixtures under `tests/` gain the same set for
  loader/validator, persistence integration, and web double tests.

## Non-goals

- No re-inclusion of a requirement whose standard is out of scope. A
  `RequirementScope` is consulted only when the standard resolves `In` at the node;
  a requirement-level `In` cannot pull a requirement into scope while its standard
  is `Out` (design D3).
- No `standard` field on `RequirementScope`. The standard is derived from the
  requirement, so the kind cannot name a standard the requirement does not belong
  to (design D2).
- No per-requirement control-coverage or gap analysis. This change resolves
  applicability, not whether an in-scope requirement is met by a control.
- No new rendered HTML screen dedicated to exclusions. The existing Statement of
  Applicability page and JSON reads carry the data; a bespoke exclusions editor is
  deferred.
- No change to standard-level `Scope` semantics, the `Requirement` kind, or
  `Control.maps_to`.
