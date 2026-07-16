## Why

Change 119 landed a GitOps kind whose observable wire token is
`kind: IntegrationConnection`: one base URL plus one out-of-band secret that drives
discovery and backs many per-control collectors. It is genuinely a declared object,
not a per-control `Collector`. The object model of record recognises six declared
kinds (Standard, Requirement, Control, Asset, Scope, Collector); this connection is a
seventh that has not yet been ratified, and its wire token is a two-word name that
breaks the single-word format the other six share. Ratifying now is cheap: 119 shipped
one increment ago, the feature is pre-production, and no config in the wild authors the
token yet, so the migration cost of the rename is near-zero. Deferring only grows the
authored surface that would later have to be migrated.

## What Changes

- Ratify the connection as the seventh declared kind of the object model under the
  single-word name `Integration`. The declared-kinds set becomes Standard, Requirement,
  Control, Asset, Scope, Collector, Integration.
- **BREAKING** Rename the observable `kind:` wire token from `IntegrationConnection`
  to `Integration`. Config documents that author `kind: IntegrationConnection` stop
  validating; authors re-token to `kind: Integration`. This is the sole runtime-visible
  change and is a hard cutover with no dual-token compatibility, consistent with how the
  asset-unification change handled its breaking wire change while the feature is
  pre-production.
- Change is confined to the observable wire string: the value of the `GitOpsSchema`
  kind constant, the loader's unknown-kind diagnostic, every validator diagnostic that
  embeds the kind name (all but one derive from that one constant; one collector
  diagnostic hard-codes the literal and is switched to the constant), the web
  empty-state authoring instruction, the config-format spec, the gitops-cli spec, the
  integration-connection spec `## Purpose` prose, `docs/gitops.md`, and every test that
  authors or asserts the token.
- Make the provider-token vocabulary explicit in the spec: the one closed token set
  (`{ fleet }` in this increment) governs exactly two things - it validates
  `Integration.provider`, and it selects the runner for an integration `EvidenceCollector`
  that names the connection. There is no second provider vocabulary. A machine's
  `asset_source.source` is NOT governed by that set: it accepts any nonblank token (up to
  64 characters) and is not validated against `IntegrationProvider.Tokens`. The rule that
  an integration-produced observation writes the exact `Integration.provider` token as its
  source is a forward-looking contract for the future integration runner, not current
  runtime behaviour and not membership validation. This is a documentation/assertion, not
  a new mechanism.

## Capabilities

### New Capabilities

- None. No new runtime capability beyond what 119 delivered.

### Modified Capabilities

- `gitops-config-format`: rename the authored `kind:` token and the kind's name
  throughout the schema, documentation, authorship, and validation requirements from
  `IntegrationConnection` to `Integration`; assert the single shared provider token set.
- `gitops-cli`: rename the `IntegrationConnection` kind references in the
  referential-integrity and sync round-trip requirements to `Integration`.

The `integration-connection` capability has no requirement delta. Its
`IntegrationConnection persistence and read model` requirement names the persisted
entity (the `integration_connections` table and its "connection" read model), a KEEP
surface, so its header stays verbatim. The one authored-kind reference in its
non-normative `## Purpose` prose (`an IntegrationConnection that an EvidenceCollector
references` -> `an Integration ...`) is not a requirement delta; because this change ships
no integration-connection delta, `openspec archive` does not rewrite it automatically, so
it is reconciled by an explicit manual edit made in the same commit that runs
`openspec archive` (see design.md). The persisted table, the entity noun "connection", and
the out-of-band token key are unchanged.

## Impact

- Code: `src/Freeboard.Core/GitOps/ConfigModel.cs` (value of the
  `KindIntegrationConnection` constant only). The loader's `SchemaKeys` map and kind
  dispatch, the unknown-kind diagnostic, and all but one `ConfigValidator` diagnostic
  read that constant, so they follow automatically. Two further edits are needed:
  `src/Freeboard.Core/GitOps/ConfigValidator.cs` (one collector dangling-connection
  diagnostic hard-codes the literal `IntegrationConnection`; switch it to
  `GitOpsSchema.KindIntegrationConnection`), and
  `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml` (the empty-state copy
  instructs authors to write `kind: IntegrationConnection`; change the authored kind name
  to `Integration`). The page route, table class, heading, and "integration connections"
  noun stay.
- Docs: `docs/gitops.md`.
- Tests: the Core and CLI tests that author `kind: IntegrationConnection` in YAML
  fixtures (`ConfigLoaderTests.cs`, `IntegrationConnectionValidationTests.cs`,
  `SyncMySqlIntegrationTests.cs`) or assert the kind name in a diagnostic string
  (`IntegrationConnectionValidationTests.cs`). A new negative test in
  `IntegrationConnectionValidationTests.cs` asserts `kind: IntegrationConnection` is now
  rejected and loads no connection, that the diagnostic contains `Unknown kind
  'IntegrationConnection'` (the loader always echoes the input), and that the portion of
  the message after `Expected one of:` lists `Integration` and does not contain the
  substring `IntegrationConnection`, pinning the hard cutover. The web empty-state test (`IntegrationConnectionsTests.cs`) is extended to
  assert the authoring copy contains `Integration` and not `IntegrationConnection`. Stale
  comments that call `IntegrationConnection` "the kind" (in
  `IntegrationConnectionValidationTests.cs` and `IntegrationConnectionIntegrationTests.cs`)
  and the `UnknownKindMessageIncludesIntegrationConnection` method name are updated; the
  test class names and internal `IntegrationConnection` type names stay.
- No API-version change (stays `freeboard.dev/v1alpha1`), no schema-field change, no
  persistence or migration change, no new dependency.
- MIT. Every touched file (Core, CLI, Persistence, web read pages, docs, tests) is MIT;
  nothing here is an Enterprise carve-out and no code moves across the EE boundary.

## Non-goals

- Renaming internal C# symbols: the `IntegrationConnection` record type, the
  `KindIntegrationConnection` constant name, and the `IntegrationConnections` list
  members stay. They are not the observable kind; renaming them is churn without
  user-facing payoff.
- Renaming the `integration-connection` capability/spec folder, the persisted
  `integration_connections` table, the `Freeboard:Integrations:<id>:ApiToken`
  configuration key, or the `/api/v1/freeboard/integration-connections` route.
- Changing `apiVersion`, any schema field, or any runtime behaviour.
- Introducing a second provider token set or any provider value beyond `fleet`.
- Any dual-token or backwards-compatible acceptance of the old `IntegrationConnection`
  token.
