## Context

The GitOps pipeline is three stages, all reached from the `gitops` CLI command
group (`src/Freeboard.CLI/GitOpsCommands.cs`):

1. Load: `Freeboard.Core/GitOps/ConfigLoader.cs` reads a directory of YAML into
   the typed `GitOpsConfig` (`ConfigModel.cs`). It never throws and returns
   problems as `Diagnostic` data.
2. Validate: `Freeboard.Core/GitOps/ConfigValidator.cs` checks required fields,
   apiVersion, unique ids, and cross-kind references. `LoadAndValidate` combines
   loader and validator diagnostics.
3. Import: `Freeboard.Persistence/GitOps/ImportPlan.cs` flattens the validated
   config to id-keyed rows; `MySqlGitOpsImporter.cs` upserts them in FK-safe
   order and hard-removes rows whose id is absent, in one DML transaction.

All three stages already handle the four kinds named in issue #50. The loader
routes every kind (`ConfigLoader.SchemaKeys` and the `LoadDocument` switch). The
validator resolves every reference each kind holds. The importer upserts and
hard-removes each kind in an FK-safe order. `compliance-persistence` and
`gitops-config-format` specs and the unit / MySQL integration tests cover this.

The remaining work is documentation completeness and pinning the acceptance
criteria at the command surface. This design records the referential-integrity
rules and hard-remove ordering as they exist in code, so the documentation and
the command-level tests describe real behaviour rather than a guess.

## Goals / Non-Goals

**Goals:**

- Document EvidenceCollector and AttestationTemplate in `docs/gitops.md` to the
  same depth as the existing kinds, including their referential-integrity rules.
- Bind issue #50's acceptance to the `gitops validate` and `gitops sync`
  commands with tests that cover the four kinds end to end.
- Confirm, by audit, that the loader, validator, and importer already cover
  every reference each kind holds; fix minimally only if a concrete gap exists.

**Non-Goals:**

- No new config kinds.
- No soft-delete. Hard removal on drop is the current, intended behaviour.
- No schema or migration change; the tables and FKs already exist.
- No web UI or read-model change (the register pages already read these kinds).
- No re-implementation of validation or import logic that already exists.

## Decisions

### Decision: Treat issue #50 as documentation-completion plus acceptance-binding, not re-implementation

The loader, validator, and importer already implement the requested behaviour,
and the per-kind requirements already live in `gitops-config-format` and
`compliance-persistence`. Rewriting or duplicating that logic would add code
without adding capability. The lowest-liability path is to (a) complete the
docs, (b) add command-level acceptance tests that were missing, and (c) audit
the existing logic against the reference inventory below.

Alternative considered: add a fresh consolidated referential-integrity module in
`Freeboard.Core`. Rejected: it would duplicate `ConfigValidator`'s existing
per-kind checks and create two systems doing the same job.

### Decision: Per-kind cross-reference rules are exactly those in ConfigValidator

Grounded in `ConfigValidator.Validate`, which validates in a fixed phase order so
each kind's reference targets are known before it runs
(`standardIds`, `requirementIds`, `controlIds`, `organisationIds`, `vendorIds`).

| Kind | Field (model) | Resolves against | Validator location |
| --- | --- | --- | --- |
| Vendor | none | n/a | `ValidateVendors` (identity only) |
| VendorScope | `Vendor` | vendor id set | `ValidateVendorScopes` |
| VendorScope | `Requirement` (when set) | requirement id set | `ValidateVendorScopes` |
| VendorScope | `Control` (when set) | control id set | `ValidateVendorScopes` |
| EvidenceCollector | `Control` (required) | control id set | `ValidateEvidenceCollectors` |
| EvidenceCollector | `Vendor` (optional) | vendor id set | `ValidateEvidenceCollectors` |
| AttestationTemplate | `Control` (required) | control id set | `ValidateAttestationTemplates` |

Supporting integrity rules already enforced and to be documented:

- VendorScope must name exactly one of `requirement` or `control`
  (`hasRequirement == hasControl` is rejected), and `justification` is required
  when `disposition` is `Out`.
- A control with at least one attached, resolved EvidenceCollector must declare
  an `evaluation` rule; the check iterates real controls and tests membership in
  the attached set, so an unresolved control reference cannot raise a spurious
  missing-evaluation diagnostic.
- AttestationTemplate references only its attach-point `control`. It does not
  reference a requirement or framework directly (correcting the issue's
  illustrative guess); its standard is reached through the control's mapped
  requirements.

Note the "unknown control/vendor" diagnostics for EvidenceCollector and
AttestationTemplate, and the "unknown vendor/requirement/control" diagnostics for
VendorScope, are the dangling-reference rejections the acceptance criteria call
for. They already exist and already read as clear messages naming the offending
id.

### Decision: Preserve hard-remove semantics exactly as the importer already implements them

Grounded in `MySqlGitOpsImporter.ImportAsync`. Hard removal is a single
`DELETE FROM <table> WHERE id NOT IN @KeepIds` per table (`DeleteAbsentAsync`),
or a full `DELETE` when the keep-set is empty. Ordering keeps the RESTRICT FKs
safe:

- vendor_scopes are replaced as a whole set (delete-all then insert) before any
  vendor, requirement, or control delete, so a dropped target has no referencing
  vendor-scope when it is deleted.
- Absent evidence_collectors are pruned before absent controls and vendors (both
  FKs are RESTRICT).
- Absent attestation_templates are pruned before absent controls (control_id FK
  is RESTRICT).
- Absent vendors are pruned after vendor_scopes are gone.

The change does not touch this ordering. The new `gitops sync` acceptance test
asserts the observable outcome (dropped rows are removed, kept rows remain) so a
future regression in the ordering is caught at the command surface.

### Decision: Command-level acceptance tests live in Freeboard.CLI.Tests

`gitops validate` is a pure, no-network command, so its dangling-reference case
runs as an in-memory test with a temp config directory (mirroring
`GitOpsCommandTests`). The `gitops sync` round-trip / hard-remove case needs a
real database and runs as a MySQL integration test gated on `FREEBOARD_TEST_DB`
(mirroring `SyncMySqlIntegrationTests`), so it skips cleanly when the var is
unset. Both stay in the MIT CLI test project; neither adds an EE reference.

The `gitops sync` case builds its two configs inline via a temp-dir writer
(mirroring `GitOpsCommandTests.WriteTempConfig`) rather than committing new
fixture directories: the existing `SyncMySqlIntegrationTests` reuse only
`FixtureDir("valid")`, which carries none of the new kinds, and the drop test
needs a distinct second config, so inline configs are the smaller change.

This `gitops sync` case is not redundant with the Persistence
`MySqlIntegrationTests`. Every existing resync-removal test there
(`ResyncRemovesControlAttachedByEvidenceCollector`,
`ResyncRemovesVendorNamedByEvidenceCollector`,
`ResyncRemovesControlAttachedByAttestationTemplate`) drops the FK target
alongside the resource, so the acceptance criterion's "drop only the resource,
keep its FK target" case is currently uncovered. The `gitops sync` case drops one
of each command-surface new kind while keeping its FK target: a vendor_scope
(keeping its vendor and requirement/control target), a collector
(drop-collector-keep-control, drop-collector-keep-vendor), and a template
(drop-template-keep-control). Dropping the vendor_scope exercises the
whole-set-replace removal path (`ReplaceVendorScopes`), distinct from the
DeleteAbsent path the collector and template drops use, so it is worth covering at
the command surface. A Persistence-level equivalent (for example
`ResyncRemovesDroppedCollectorKeepingControl` in `MySqlIntegrationTests`) is an
acceptable alternative home for that same coverage, but the coverage must exist
somewhere.

A bare Vendor's hard-remove uses the same DeleteAbsent path and is already
covered by `MySqlIntegrationTests.ResyncRemovesVendorThatHadVendorScope`, so the
CLI case delegates it rather than adding an unreferenced vendor to the fixture.
The vendor referenced by the fixture's vendor_scope and evidence_collector is
retained across both syncs.

## Risks / Trade-offs

- Risk: the new `gitops sync` test overlaps the existing Persistence integration
  tests. Mitigation: it exercises a distinct surface (the command wiring:
  validate-then-import, migrate-first gate, exit code), not the importer in
  isolation, so it is acceptance coverage rather than duplication. Keep it to one
  round-trip-plus-drop case, not a per-kind matrix.
- Risk: the audit finds a genuine gap in the existing loader/validator/importer.
  Mitigation: fix it minimally in the owning MIT project and add a targeted test;
  do not broaden scope.
- Trade-off: documenting behaviour that already ships means docs and code must be
  kept in sync. Mitigation: the docs describe rules that are also asserted by
  tests, so drift surfaces as a test failure.

## Migration Plan

No runtime migration. Documentation and tests only (plus any minimal audit fix).
Rollback is reverting the change; no data or schema is affected.

## Open Questions

None.
