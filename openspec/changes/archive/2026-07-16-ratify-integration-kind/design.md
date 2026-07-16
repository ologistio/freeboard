## Context

Change 119 shipped a GitOps kind whose authored discriminator is
`kind: IntegrationConnection`. The object model of record recognises six declared
kinds (Standard, Requirement, Control, Asset, Scope, Collector); this connection is a
genuine seventh declared object - one base URL plus one out-of-band secret that drives
discovery and backs many per-control collectors - but it was never ratified and its
two-word wire token breaks the single-word format the other six share.

The wire token flows mostly from a single source. `GitOpsSchema.KindIntegrationConnection`
(value `"IntegrationConnection"`) is the routing key in `ConfigLoader.SchemaKeys`, the
`switch` discriminator, the token interpolated into the loader's unknown-kind
diagnostic, and the kind name interpolated into most `ConfigValidator` diagnostics
about a connection (duplicate id, unknown provider, unknown vendor, unsafe id). So the
constant's value change cascades through routing and those diagnostics automatically.

Two sites do not read the constant and are edited by hand. `ConfigValidator.cs`
(`src/Freeboard.Core/GitOps/ConfigValidator.cs:910`) hard-codes the literal
`IntegrationConnection` in the collector's dangling-connection diagnostic
(`references unknown IntegrationConnection id '<id>'`) instead of the constant; it is
switched to `GitOpsSchema.KindIntegrationConnection` so the message follows the rename.
The web empty-state (`src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml:22`)
tells authors to write an `IntegrationConnection` in config; that authoring instruction
is the observable kind name and changes to `Integration`.

The feature is pre-production and one increment old; no committed config in the wild
authors the token.

## Goals / Non-Goals

**Goals:**

- Ratify the kind as the seventh declared kind under the single-word name `Integration`.
- Rename the observable `kind:` wire token from `IntegrationConnection` to `Integration`
  across the loader value, every diagnostic, the specs, `docs/gitops.md`, and the tests.
- Make explicit in the spec that one closed provider token set (`{ fleet }`) governs
  exactly two things: it validates `Integration.provider` and selects an integration
  collector's runner. A machine's `asset_source.source` is not validated against that set;
  the equality of an integration-produced observation's source with `Integration.provider`
  is a forward-looking runner contract, not membership validation.

**Non-Goals:**

- Renaming internal C# symbols (`IntegrationConnection` record, `KindIntegrationConnection`
  constant name, `IntegrationConnections` list), the `integration-connection` spec folder,
  the `integration_connections` table, the token configuration key, or the HTTP route.
- Any `apiVersion`, schema-field, persistence, migration, or behaviour change.
- Any new provider token, or any backwards-compatible acceptance of the old token.

## Decisions

### Rename depth: wire token only

The rename changes exactly one thing that a user or a stored artifact can observe: the
authored `kind:` scalar value and every string that echoes it (diagnostics, docs, spec
prose that names the kind). Internal identifiers stay, because they are cost, not
capability: the mediator's binding constraint keeps the C# symbols and the capability
folder name, and `code-as-liability.md` forbids churn without user-facing payoff. The
persisted `integration_connections` table, the `Freeboard:Integrations:<id>:ApiToken`
key, and the `/integration-connections` route are storage/transport identifiers, not the
authored kind, and are out of scope.

Alternative considered: rename the C# symbols too for internal consistency. Rejected -
it touches Core, Persistence, Web, and CLI for zero observable benefit and enlarges the
diff and review surface.

### Rename surface

Wire token (CHANGES) - authors or asserts `IntegrationConnection` as the `kind:` value
or the kind's name:

- `src/Freeboard.Core/GitOps/ConfigModel.cs`: value of `KindIntegrationConnection`
  becomes `"Integration"`. Constant name unchanged.
- `src/Freeboard.Core/GitOps/ConfigLoader.cs`: no edit. The `SchemaKeys` key, the
  `switch` case, and the unknown-kind diagnostic all read the constant.
- `src/Freeboard.Core/GitOps/ConfigValidator.cs:910`: one edit. This one collector
  diagnostic hard-codes the literal `IntegrationConnection`; it becomes
  `GitOpsSchema.KindIntegrationConnection` so its printed kind name follows the rename.
  Every other connection diagnostic already reads the constant and needs no edit.
- `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml:22`: the empty-state
  copy tells authors to write an `IntegrationConnection` in config; that authored kind
  name becomes `Integration`. The page route (`/settings/integration-connections`), the
  `integration-connections` table class, the `Integration connections` heading, and the
  persisted "connection" noun stay - they are not the authored kind.
- `docs/gitops.md`: the kind-enumeration list, the `### IntegrationConnection` section
  heading, the `kind: IntegrationConnection` example, and prose/validation bullets that
  name the authored kind become `Integration`.
- Tests that author `kind: IntegrationConnection` in YAML: `ConfigLoaderTests.cs`,
  `IntegrationConnectionValidationTests.cs`, `SyncMySqlIntegrationTests.cs`.
- Tests that assert the kind name inside a diagnostic string:
  `IntegrationConnectionValidationTests.cs` asserts `IntegrationConnection` for the
  unknown-kind message (valid-kinds enumeration), the duplicate-id message
  (`Duplicate IntegrationConnection id ...`), and the collector's unknown-connection
  message (`unknown IntegrationConnection id ...`). After the constant change and the
  `ConfigValidator.cs:910` edit those messages say `Integration`, so the three
  assertions flip to their `Integration` form. The unknown-connection flip depends on
  the line-910 edit: without it that message would keep the old literal and the flipped
  assertion would fail.
- Specs (delta files in this change): `gitops-config-format` and `gitops-cli` only.
  The `integration-connection` capability has NO delta file: it is touched solely by a
  base-spec `## Purpose` prose reconciliation applied during archive/spec-sync (see the
  Purpose reconciliation note below).

Internal symbol (STAYS) - not the observable kind:

- The `IntegrationConnection` record type, the `KindIntegrationConnection` constant
  name, and the `GitOpsConfig.IntegrationConnections` list.
- The `integration_connections` table and its columns; the read model and web/CLI read
  surfaces (`IntegrationConnections.cshtml.cs`, `ConnectionCommands.cs`,
  `ComplianceReadModels.cs`, and friends) that carry the persisted-entity name.
- The `integration-connection` capability folder, the token configuration key, and the
  `/api/v1/freeboard/integration-connections` route.

### No example/fixture config authors the token

`examples/` contains no document with `kind: IntegrationConnection` (verified by grep);
the word "integration" there refers to SaaS vendor integrations, not the kind. So no
example or fixture YAML file changes. The only authored occurrences are in tests and
`docs/gitops.md`.

### Negative test pins the hard cutover (no dual token)

Flipping the existing diagnostic assertions from `Contains("IntegrationConnection")` to
`Contains("Integration")` is not enough on its own: `"Integration"` is a substring of
`"IntegrationConnection"`, so a lingering old token would still satisfy the flipped
assertion and go undetected. The full diagnostic is not a clean guard either: the loader
emits `Unknown kind '{kind}'. Expected one of: ...` (`ConfigLoader.cs:177`), so the whole
message ALWAYS echoes the input and therefore always contains `IntegrationConnection`.
To pin the no-dual-token decision, add one negative test: a config authoring
`kind: IntegrationConnection` SHALL be rejected and load no connection; the diagnostic
SHALL contain `Unknown kind 'IntegrationConnection'` (the echoed input); and the portion
of the message AFTER `Expected one of:` (the valid-kinds enumeration) SHALL list
`Integration` and SHALL NOT contain the substring `IntegrationConnection`. Inspecting only
the enumeration is what would fail if any dual-token acceptance or a stale literal survived
the rename. It maps to the `gitops-config-format` loader scenario
"Retired IntegrationConnection kind is now unknown".

`IntegrationProvider.Tokens` (`{ "fleet" }`) is the one closed provider vocabulary. It
governs exactly two things: `ConfigValidator` validates `Integration.provider` against it,
and an integration `EvidenceCollector` selects its runner through the `provider` of the
`Integration` it names - the collector has no `provider` field of its own. There is no
second set. A machine's `asset_source.source` is NOT one of the governed things: it is
validated only as nonblank (up to 64 characters) and is never checked against
`IntegrationProvider.Tokens` (verified at `MySqlAssetWriteStore.cs:37` and `:54`). The
forward-looking contract is that the future integration runner writes the exact
`Integration.provider` token as the `asset_source.source` of an observation it discovers -
an equality the runner will honour, not a membership rule the current runtime enforces.
Task 3 is therefore documentation only: the `gitops-config-format` `Integration authorship`
and `EvidenceCollector authorship` requirements are edited to state this explicitly. No
code changes.

### Requirement-header renames: authored-kind headers rename, persisted-entity header stays

Three established requirement headers name the kind. The decision splits on whether a
header names the authored kind (renames) or the persisted/internal entity (stays):

- `IntegrationConnection authorship` and `IntegrationConnection validation`
  (gitops-config-format) name the authored kind - how a `kind: Integration` document is
  authored and validated. Since the ratified kind name is `Integration`, each is renamed
  to its `Integration ...` form via a RENAMED delta (FROM/TO). Both also change body
  text, so they additionally appear under MODIFIED with their new header; this
  RENAMED-plus-MODIFIED pairing passes `openspec validate --strict`.
- `IntegrationConnection persistence and read model` (integration-connection) names the
  persisted entity: its body is entirely about the `integration_connections` table, the
  importer, and the "integration-connections"/"connection" read model - all KEEP
  surfaces under the mediator's binding constraint. Renaming the header to `Integration`
  while every noun in the body stays `integration-connections`/`connection` would create
  header/body dissonance and mislabel a persistence requirement as an authored-kind one.
  So this header stays verbatim, consistent with keeping the capability folder, the
  table, the route, and the C# symbols. The integration-connection capability therefore
  has no requirement delta and no delta file. Its `## Purpose` prose does name the
  authored kind (`an IntegrationConnection that an EvidenceCollector references`);
  OpenSpec deltas are requirement-scoped and cannot fold a `## Purpose` edit, so that one
  prose reference is reconciled by an explicit manual edit made in the same commit that
  runs `openspec archive` (see the Purpose reconciliation note below), not by an arbitrary
  implementation-time base-spec edit.

### Purpose reconciliation is an explicit manual edit at archive, not automatic

The folded specs under `openspec/specs/` are managed through the OpenSpec workflow, not
hand-edited as ordinary repo docs (see the `.claude/rules/markdownlint.md` carve-out that
ignores `openspec/specs/**` for this reason). The one authored-kind reference in
`openspec/specs/integration-connection/spec.md` `## Purpose`
(`an IntegrationConnection that an EvidenceCollector references` -> `an Integration that
an EvidenceCollector references`) is a base-spec prose fix that no requirement delta can
carry.

Because this change ships no integration-connection delta, `openspec archive` has nothing
that rewrites that `## Purpose` line, so the archive does NOT reconcile it automatically.
The reconciliation is therefore an explicit MANUAL edit made in the same commit that runs
`openspec archive` (the archive/spec-sync step): hand-edit the `## Purpose` line to the
`Integration` form, then run
`grep -rn IntegrationConnection openspec/specs/integration-connection/` to confirm only
the intended KEEP occurrences remain (the `IntegrationConnection persistence and read
model` requirement header and the `integration_connections`/`integration-connections`/
"connection" table, route, and entity nouns). Editing a folded spec by hand is allowed
here because this change is the managing OpenSpec workflow for that spec. It is not an ad
hoc base-spec edit made during task implementation, and it is not something the archive
applies on its own.

### Divergence resolutions (two independent plans reconciled)

Two plans were merged. Each contested point was verified against the code before deciding.

- **Hard-coded validator literal.** Plan A claimed no production code holds the literal
  `IntegrationConnection` except the constant and that `ConfigValidator.cs` needs no
  edit. Verified false: `ConfigValidator.cs:910` interpolates the bare literal
  `IntegrationConnection` into the collector's dangling-connection diagnostic while every
  sibling diagnostic uses the constant. Resolution: take the second plan - switch that
  literal to `GitOpsSchema.KindIntegrationConnection`. Added to the rename surface, a
  task, and Impact. Without it the message would drift from the renamed kind and the
  flipped `unknown Integration id` test assertion (`IntegrationConnectionValidationTests.cs:400`)
  would fail.
- **Empty-state authoring copy.** Verified `IntegrationConnections.cshtml:22` instructs
  authors to "author an `IntegrationConnection` in your Freeboard config". That is the
  observable authored kind name, so it changes to `Integration`. The page route, table
  class, `Integration connections` heading, and persisted "connection" noun stay. The
  web test currently asserts only the `data-empty` marker
  (`IntegrationConnectionsTests.cs:160`); it is extended to assert the authoring copy
  contains `Integration` and not `IntegrationConnection`, guarding the copy change.
- **Requirement headers.** See the split decision above: authored-kind headers rename,
  the persisted-entity header stays verbatim. The two RENAMED FROM headers match the
  established spec character-for-character (`gitops-config-format/spec.md:807,845`).
- **Provider sharing / asset_source.source.** Verified the closed set governs only
  `Integration.provider` and an integration collector's runner selection, and does NOT
  govern `asset_source.source`. `IntegrationProvider.Tokens` (`{ fleet }`,
  `IntegrationProvider.cs:13`) is the sole provider vocabulary; `ConfigValidator.cs:743`
  validates `Integration.provider` against it. A machine's `asset_source.source` is
  validated only as nonblank (up to 64 characters) and is never checked against that set
  (`MySqlAssetWriteStore.cs:37`, `:54`). The design invariant (documented at
  `IntegrationProvider.cs:7` and `ConfigModel.cs:230`) is forward-looking: when the future
  integration runner writes a machine observation discovered through a connection, it
  writes the exact `Integration.provider` token as that observation's `asset_source.source`.
  The spec therefore states that equality as a runner contract, not that
  `asset_source.source` is validated against the set or that the set aligns/governs it.

### Seven declared kinds vs the loader's ten tokens

The ratified object model has seven declared kinds - `Standard`, `Requirement`,
`Control`, `Asset`, `Scope`, `Collector`, `Integration` - stated in the `Integration
authorship` requirement. These are distinct from the ten `kind:` tokens the loader
accepts: `Standard`, `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`,
`VendorScope`, `Integration`, `EvidenceCollector`, `AttestationTemplate` (the
`Declarative compliance config schema` requirement). The model name `Collector` maps to
the wire token `EvidenceCollector` (unchanged); `RequirementScope`, `VendorScope`, and
`AttestationTemplate` remain auxiliary loader tokens with no separate model-kind status.
This rename touches only the `IntegrationConnection` -> `Integration` token; no other
loader token changes and none is removed.

### Collector has no provider field

An `EvidenceCollector` declares no `provider`. The provider that runs an integration
collector is the `provider` of the `Integration` it names via `connection`, validated
against the one `IntegrationProvider.Tokens` set. The effective selector for a running
collector is the `(integration, provider)` pair, not a collector-local vocabulary. The
`EvidenceCollector authorship` requirement states this.

## Risks / Trade-offs

- [Breaking wire change: existing `kind: IntegrationConnection` documents stop
  validating] -> Pre-production, one increment old, no committed config authors the
  token, and hard cutover matches the asset-unification precedent. The loader already
  emits a clear unknown-kind diagnostic listing `Integration`, so a stale document fails
  loudly, not silently.
- [A hidden literal `IntegrationConnection` string is missed and drifts from the
  renamed token] -> Grep found two hand sites that do not read the constant
  (`ConfigValidator.cs:910` and `IntegrationConnections.cshtml:22`); both are in the
  rename surface. A repo-wide grep for `IntegrationConnection` after the change confirms
  every remaining occurrence is either an intended KEEP (constant name, record type, list,
  table, route, capability folder, or the persisted-entity header) or the one deliberately
  retained legacy token in the negative test and its `gitops-config-format` scenario
  ("Retired IntegrationConnection kind is now unknown"), which prove the old token is
  rejected. The Core/CLI test suites assert the diagnostics and round-trips, so a missed
  authored site fails a test.
- [The integration-connection spec `## Purpose` prose still names `IntegrationConnection`
  and is not delta-addressable] -> Reconciled by an explicit manual edit in the same commit
  that runs `openspec archive`; the archive does not rewrite it on its own because this
  change ships no integration-connection delta. Not an implementation-time base-spec edit.
  See the Purpose reconciliation note above.

## Migration Plan

No data migration. The `integration_connections` table, its rows, and the token key are
untouched; only the authored `kind:` token changes. Deploy is a normal build. Rollback
is reverting the constant value and the doc/spec/test edits. Any operator with a
draft config authoring `kind: IntegrationConnection` re-tokens it to `kind: Integration`.
