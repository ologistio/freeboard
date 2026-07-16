## 1. Spec deltas (docs: ratify the seventh declared kind)

Commit: `docs(gitops): ratify the Integration kind and rename its wire token`

- [x] 1.1 gitops-config-format delta: rename the authored `kind:` token and the kind
      name from `IntegrationConnection` to `Integration` in the schema, no-secret,
      loader-never-throws, EvidenceCollector authorship/validation, and documentation
      requirements; RENAME the `IntegrationConnection authorship`/`validation`
      requirements to `Integration ...`.
- [x] 1.2 gitops-config-format delta: assert that the one closed provider token set
      (`{ fleet }`) governs exactly two things - it validates `Integration.provider` and
      selects the runner for an integration `EvidenceCollector` that names the connection.
      State that a machine's `asset_source.source` is NOT validated against that set (any
      nonblank token up to 64 characters), and that the equality of an integration-produced
      observation's source with `Integration.provider` is a forward-looking runner contract,
      not membership validation.
- [x] 1.3 integration-connection: no delta file and no requirement delta. Leave the
      `IntegrationConnection persistence and read model` header verbatim - it names the
      persisted entity (the `integration_connections` table and "connection" read model),
      a KEEP surface. Do NOT hand-edit the base spec during implementation. With no
      integration-connection delta, `openspec archive` has nothing that rewrites the
      `## Purpose` prose, so the archive does NOT reconcile it automatically. In the same
      commit that runs `openspec archive` (the archive/spec-sync step), hand-edit
      `openspec/specs/integration-connection/spec.md` `## Purpose` to change the one
      authored-kind reference `an IntegrationConnection that an EvidenceCollector
      references` -> `an Integration that an EvidenceCollector references`. This manual edit
      to a folded spec is allowed because `openspec/specs/**` is OpenSpec-workflow-managed
      (the `.claude/rules/markdownlint.md` carve-out) and this change is its managing
      workflow. Then run `grep -rn IntegrationConnection openspec/specs/integration-connection/`
      and confirm only intended KEEP occurrences remain: the `IntegrationConnection
      persistence and read model` requirement header and the table/route/entity nouns
      (`integration_connections`, `integration-connections`, "connection"). Record this
      here so the archive step performs it.
- [x] 1.4 gitops-cli delta: rename the `IntegrationConnection` kind references to
      `Integration` in the referential-integrity and sync round-trip requirement bodies
      (MODIFIED; the headers do not name the kind).
- [x] 1.5 Run `openspec validate "ratify-integration-kind" --strict`; fix any errors.

## 2. Core loader token value (BREAKING wire change)

Commit: `feat!: rename the Integration kind wire token from IntegrationConnection`
(touches `Freeboard.Core` and the web empty-state copy)

- [x] 2.1 In `src/Freeboard.Core/GitOps/ConfigModel.cs`, change the value of
      `KindIntegrationConnection` from `"IntegrationConnection"` to `"Integration"`.
      Leave the constant name, the `IntegrationConnection` record, and the
      `IntegrationConnections` list unchanged.
- [x] 2.2 In `src/Freeboard.Core/GitOps/ConfigValidator.cs:910`, replace the hard-coded
      literal `IntegrationConnection` in the collector's dangling-connection diagnostic
      with `GitOpsSchema.KindIntegrationConnection` so the message follows the rename.
      `ConfigLoader.cs` needs no edit (it reads the constant).
- [x] 2.3 In `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml` (around
      line 22), change the empty-state authoring instruction from `<code>IntegrationConnection</code>`
      to `<code>Integration</code>`. Leave the page route, table class, heading, and the
      persisted "integration connections" noun unchanged. Task 4.5 extends the web test to
      assert this copy.
- [x] 2.4 Grep the solution for any remaining `IntegrationConnection` string literal and
      confirm each remaining occurrence is either an intended KEEP (constant name, record
      type, list, table, route, capability folder, persisted-entity header) or the one
      deliberately-retained legacy token in the negative test (task 4.3) and its
      `gitops-config-format` scenario "Retired IntegrationConnection kind is now unknown" -
      and not an authored or echoed kind token anywhere else. The negative test's fixture
      and the two diagnostic assertions that expect `Unknown kind 'IntegrationConnection'`
      are the only source occurrences of the old token that intentionally survive.
- [x] 2.5 Mark the commit BREAKING (`!` and a `BREAKING CHANGE:` footer): documents
      authoring `kind: IntegrationConnection` no longer validate.

## 3. Documentation

Commit: `docs(gitops): document the Integration kind in gitops.md`

- [x] 3.1 In `docs/gitops.md`, update the kind-enumeration list, the section heading,
      the `kind: IntegrationConnection` example, the prose reference at
      `docs/gitops.md:267` (`a connection (the id of an IntegrationConnection)` ->
      `... of an Integration`), and the other prose/validation bullets that name the
      authored kind to `Integration`. Leave the persisted `integration_connections`
      route and table references unchanged.
- [x] 3.2 Run `npx markdownlint-cli2 "docs/gitops.md"`; fix any issues.

## 4. Tests

Commit: `test: assert the Integration wire token across gitops tests`

- [x] 4.1 Update every POSITIVE (valid) YAML fixture authoring `kind: IntegrationConnection`
      to `kind: Integration`: `tests/Freeboard.Core.Tests/ConfigLoaderTests.cs`,
      `tests/Freeboard.Core.Tests/IntegrationConnectionValidationTests.cs`,
      `tests/Freeboard.CLI.Tests/SyncMySqlIntegrationTests.cs`. Do NOT re-token the one
      negative fixture task 4.3 adds: it intentionally authors `kind: IntegrationConnection`
      to prove the retired token is rejected, so it stays on the old token.
- [x] 4.2 In `IntegrationConnectionValidationTests.cs`, flip the three diagnostic-string
      assertions that expect `IntegrationConnection` to expect `Integration`: the
      unknown-kind valid-kinds enumeration (line ~140), `Duplicate IntegrationConnection
      id` (line ~291), and `unknown IntegrationConnection id` (line ~400). The
      unknown-connection assertion only passes once task 2.2 fixes the `ConfigValidator.cs:910`
      literal.
- [x] 4.3 In `IntegrationConnectionValidationTests.cs`, add a NEGATIVE test pinning the
      no-dual-token cutover: a config authoring `kind: IntegrationConnection` is rejected
      and loads no connection. The loader emits `Unknown kind '{kind}'. Expected one of:
      ...`, so the full message always echoes the input; assert it contains `Unknown kind
      'IntegrationConnection'`. Then inspect ONLY the portion after `Expected one of:` (the
      valid-kinds enumeration) and assert that portion lists `Integration` and does NOT
      contain the substring `IntegrationConnection`. A plain `Contains("Integration")` flip
      is insufficient because `Integration` is a substring of `IntegrationConnection`.
      Covers the `gitops-config-format` scenario "Retired IntegrationConnection kind is now
      unknown".
- [x] 4.4 Update stale authored-kind test comments and one method name; KEEP the class
      names and the internal `IntegrationConnection` type names. In
      `tests/Freeboard.Core.Tests/IntegrationConnectionValidationTests.cs` (around line 6)
      and `tests/Freeboard.Persistence.Tests/IntegrationConnectionIntegrationTests.cs`
      (around line 11), fix the comments that call `IntegrationConnection` "the kind" so
      they name the persisted entity, not the authored kind (the authored kind is now
      `Integration`). Rename the test method `UnknownKindMessageIncludesIntegrationConnection`
      (around line 130) to reflect the `Integration` token (for example
      `UnknownKindMessageListsIntegration`).
- [x] 4.5 Extend the empty-state web test
      `tests/Freeboard.Web.Tests/IntegrationConnectionsTests.cs` (around line 160) to
      assert the empty-state authoring copy contains `Integration` and does NOT contain
      `IntegrationConnection` (currently it checks only the `data-empty` marker). This
      guards the task 2.3 empty-state copy change.

## 5. Verification

- [x] 5.1 `dotnet build`.
- [x] 5.2 `dotnet test` (unit/web tiers; MySQL/E2E-gated tests skip without their env
      vars).
- [x] 5.3 Re-run `openspec validate "ratify-integration-kind" --strict`.
