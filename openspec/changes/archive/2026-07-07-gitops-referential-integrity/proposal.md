## Why

Issue #50 asks to extend the GitOps loader, validator, and sync path to the four
newer config kinds (Vendor, VendorScope, EvidenceCollector, AttestationTemplate),
add cross-reference (referential integrity) checks, and preserve `gitops sync`
hard-remove semantics. Investigation shows the production behaviour already
exists: it was delivered piecemeal by the register changes (vendor-gitops-kinds,
evidence-collector-kind, attestation-template-kind) and their persistence work.

- `Freeboard.Core/GitOps/ConfigLoader.cs` already loads all four kinds.
- `Freeboard.Core/GitOps/ConfigValidator.cs` already resolves every cross-kind
  reference (VendorScope to vendor/requirement/control, EvidenceCollector to
  control/vendor, AttestationTemplate to control) and emits a dangling-reference
  diagnostic when the target id is absent.
- `Freeboard.Persistence/GitOps/ImportPlan.cs` and `MySqlGitOpsImporter.cs`
  already upsert all four kinds in FK-safe order and hard-remove dropped rows.
- `compliance-persistence` and `gitops-config-format` specs already carry the
  per-kind persistence and validation requirements; unit and MySQL integration
  tests cover round-trip and resync-removes for each kind.

Two real gaps remain against the issue's intent:

1. `docs/gitops.md` documents kinds only through VendorScope. EvidenceCollector
   and AttestationTemplate (their schema, examples, referential-integrity rules,
   and persistence) are undocumented, so the shipped GitOps surface is
   under-documented.
2. The two acceptance criteria are phrased at the `gitops validate` / `gitops
   sync` command surface, but no test binds them there for the new kinds. They
   are proven only at the `Freeboard.Core` and `Freeboard.Persistence` layers.
   `gitops-cli` has no requirement pinning cross-kind referential integrity or
   the sync round-trip / hard-remove for these kinds at the command boundary.

This change closes the documentation gap and pins the acceptance at the command
surface. It adds no new production validation or import logic unless the audit
task uncovers a concrete gap, in which case the fix is the smallest coherent
change.

## What Changes

- Document EvidenceCollector and AttestationTemplate in `docs/gitops.md`: add
  them to the noun mapping table, the supported-kinds list, per-kind sections
  with examples, the validation-rules list (including dangling-reference
  diagnostics), and the persistence schema paragraph.
- Add CLI-level acceptance coverage in `Freeboard.CLI.Tests`:
  - `gitops validate` rejects a dangling cross-kind reference (for example an
    EvidenceCollector naming an unknown control) with a clear diagnostic and a
    non-zero exit, exercised through the command entry point.
  - `gitops sync` round-trips vendors, vendor-scopes, evidence-collectors, and
    attestation-templates and hard-removes dropped ones, exercised through the
    command against MySQL (gated on `FREEBOARD_TEST_DB`, skips cleanly when
    unset).
- Audit-and-confirm task: verify the loader, validator, and importer cover all
  four kinds and every reference they hold; close any concrete gap minimally if
  found. No gap is expected.

No breaking changes. No schema or migration changes. No new dependency.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `gitops-config-format`: add a requirement that the config-format documentation
  enumerates every supported kind, including EvidenceCollector and
  AttestationTemplate, with their referential-integrity rules.
- `gitops-cli`: add a requirement that `gitops validate` and `gitops sync`
  enforce referential integrity for every config kind at the command surface,
  round-tripping and hard-removing the new kinds.

## Impact

- Docs: `docs/gitops.md` (MIT).
- Tests: `tests/Freeboard.CLI.Tests` (new command-level acceptance cases, MIT).
- Production code: no change expected. Any minimal fix would land in
  `Freeboard.Core/GitOps` (validation) or `Freeboard.Persistence/GitOps`
  (import), both MIT.
- No change to `Freeboard.Enterprise`. The CLI remains free of any EE reference
  and cross-platform.
