## 1. Core model

- [x] 1.1 Add `Organisation` record (id, title, kind, parent) and an `OrganisationKind` enum (`Company`, `Department`) to `Freeboard.Core/GitOps/ConfigModel.cs`; add `KindOrganisation` to `GitOpsSchema`.
- [x] 1.2 Redefine the `Scope` record to (id, title, organisation, standard, disposition); add a `ScopeDisposition` enum (`In`, `Out`); remove `Scope.Controls`.
- [x] 1.3 Add `Organisations` to `GitOpsConfig` aggregate.

## 2. Config load and validation

- [x] 2.1 Bind the `Organisation` and redefined `Scope` kinds in the loader; report unknown/missing kind including `Organisation`.
- [x] 2.2 Validate: kind in `Company`/`Department`; disposition in `In`/`Out`; `Organisation.parent`, `Scope.organisation`, `Scope.standard` resolve; organisation tree acyclic; unique `(organisation, standard)`; unknown-field rejection for the new kinds.
- [x] 2.3 Update GitOps config test fixtures to the new schema; remove `Scope.controls` usage.

## 3. Persistence schema and store

- [x] 3.1 Add a forward-only migration: create `organisations` (with nullable `parent_id` self-FK, `kind`, binary-collation id); add `organisation_id`, `standard_id`, `disposition` to `scopes` with FKs and a unique key on `(organisation_id, standard_id)`; drop the `scope`->`controls` relation table.
- [x] 3.2 Update read models: add an organisation row type; redefine `ScopeRow` to (id, title, organisation, standard, disposition); add organisations to `ComplianceCounts`.
- [x] 3.3 Extend `IComplianceStore` and the MySQL implementation to read organisations (resolved parent) and the redefined scopes, plus organisation counts.
- [x] 3.4 Extend the GitOps importer: FK-safe upsert order (standards, controls, organisations parent-before-child, scopes), whole-set `maps_to` replace, FK-safe delete order (scopes, organisations child-before-parent, controls, standards).

## 4. Web read and Statement of Applicability

- [x] 4.1 Add `GET /api/v1/freeboard/organisations`; update `GET /api/v1/freeboard/scopes` to the new shape; add organisations to the compliance status counts and the unreachable-store null summary.
- [x] 4.2 Implement the SoA projection: resolve per-node disposition by nearest-ancestor inheritance (explicit/inherited/Undetermined) over the org tree for a standard.
- [x] 4.3 Add the GET-only SoA endpoint (served in read-only mode, 503 on unreachable store) and a read-only SoA view.

## 5. App-managed writes

- [x] 5.1 Add a write abstraction over the store for organisations and scope dispositions; MySQL implementation enforces the same invariants as import.
- [x] 5.2 Add write endpoints under `/api/v1/freeboard/` for organisations and scope dispositions; reject invalid input with RFC 7807; confirm the read-only middleware 409s them in GitOps mode (not auth-exempt).

## 6. CLI

- [x] 6.1 Ensure `gitops validate` and `gitops apply` dry-run cover the `Organisation` kind and the redefined `Scope`; keep CLI community and cross-platform (no EE reference).

## 7. Tests and docs

- [x] 7.1 Core: loader/validator tests for the new kinds, cycle detection, duplicate mapping, disposition/kind enums.
- [x] 7.2 Persistence (MySQL, skippable): migration produces the expected tables/keys; importer FK ordering; unique-key enforcement.
- [x] 7.3 Web: read-endpoint shape/ordering with an `IComplianceStore` double; SoA projection inheritance cases; write endpoints allowed off-mode and 409 on-mode.
- [x] 7.4 Update any docs/sample YAML that reference the old `Scope.controls` schema.
