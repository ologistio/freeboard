## 1. Audit existing coverage (no expected code change)

- [x] 1.1 Confirm the four kinds (Vendor, VendorScope, EvidenceCollector,
      AttestationTemplate) against the design.md reference table and its per-kind
      load/validate/upsert/hard-remove/test inventory: loader routing, every
      cross-kind reference emitting a dangling-reference diagnostic that names the
      offending id, and FK-safe hard-remove ordering. No new artifact; the
      conclusions live in design.md.
- [x] 1.2 If, and only if, an audit step finds a concrete gap, close it with the
      smallest coherent change in the owning MIT project (`Freeboard.Core/GitOps`
      or `Freeboard.Persistence/GitOps`) and a targeted test. No gap is expected.

## 2. docs(gitops): document EvidenceCollector and AttestationTemplate

- [x] 2.1 Add EvidenceCollector and AttestationTemplate to the noun mapping table
      and the "This increment ships" list in `docs/gitops.md`.
- [x] 2.2 Add EvidenceCollector and AttestationTemplate to the `kind` enumeration
      in the Format section.
- [x] 2.3 Add per-kind sections (schema fields plus an example document) for both,
      matching the depth of the existing Vendor / VendorScope sections.
- [x] 2.4 Extend the Validation section with the new kinds' rules, including the
      dangling-reference diagnostics (EvidenceCollector control/vendor,
      AttestationTemplate control) and the control-evaluation-required rule.
- [x] 2.5 Extend the Persistence schema paragraph to mention `evidence_collectors`
      and `attestation_templates` and their RESTRICT foreign keys.
- [x] 2.6 Run `npx markdownlint-cli2 "docs/gitops.md"` and fix any findings.

## 3. test(gitops): pin the acceptance at the command surface

- [x] 3.1 In `tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs`, add one
      `gitops validate` case per new kind (representative dangling reference, not an
      exhaustive edge matrix): a VendorScope naming an unknown vendor, an
      EvidenceCollector naming an unknown control, and an AttestationTemplate naming
      an unknown control. Each asserts a non-zero exit and a stderr diagnostic
      naming the unknown id. The exhaustive per-edge matrix stays in the
      `Freeboard.Core` ConfigValidator unit tests.
- [x] 3.2 Build the two configs the drop test needs, inline in the test via a
      temp-dir writer (mirror `GitOpsCommandTests.WriteTempConfig`; the existing
      `SyncMySqlIntegrationTests` only reuses `FixtureDir("valid")`, which contains
      none of the new kinds, so inline configs are needed rather than new committed
      fixture dirs): a full config with standard/requirement/control plus vendors,
      vendor_scopes, evidence_collectors, and attestation_templates; and a second
      config that drops one vendor_scope (keeping its vendor and its
      requirement/control target), one evidence-collector (keeping its control and
      vendor), and one attestation-template (keeping its control), and keeps the
      retained rows. Dropping the vendor_scope exercises the whole-set-replace
      removal path (`ReplaceVendorScopes`); a bare Vendor drop is delegated to the
      Persistence `MySqlIntegrationTests.ResyncRemovesVendorThatHadVendorScope`
      test, so no unreferenced vendor is added to the fixture.
- [x] 3.3 In `tests/Freeboard.CLI.Tests/SyncMySqlIntegrationTests.cs` (gated on
      `FREEBOARD_TEST_DB`, skips cleanly when unset), add ONE round-trip-plus-drop
      case that exercises the command wiring (validate-then-import, migrate gate,
      exit code) and the drop path - not a per-kind CLI matrix. Run `gitops sync`
      on the full config, assert the new-kind rows persist, then re-sync the
      dropped config and assert the dropped rows are hard-removed while their FK
      targets and the other retained rows remain. This covers the "drop only the
      resource, keep its FK target" case, which the Persistence
      `MySqlIntegrationTests` resync-removal tests do not: each of them drops the FK
      target alongside the resource. A Persistence-level equivalent (for example
      `ResyncRemovesDroppedCollectorKeepingControl`) is an acceptable alternative
      home for the same coverage, but the coverage must exist somewhere.

## 4. Verify

- [x] 4.1 `dotnet build`.
- [x] 4.2 `dotnet test` (unit and web tests pass with no external dependencies;
      MySQL-gated cases skip cleanly when `FREEBOARD_TEST_DB` is unset).
- [x] 4.3 With a local MySQL up and `FREEBOARD_TEST_DB` set, run the new
      `gitops sync` integration case (task 3.3) and confirm round-trip and
      hard-remove.
- [x] 4.4 `npx markdownlint-cli2 "**/*.md"`.
