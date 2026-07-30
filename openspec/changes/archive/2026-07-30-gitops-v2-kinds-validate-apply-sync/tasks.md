## 1. Report integrations on every gitops command surface

Commit: `feat(cli): report integrations in the gitops validate, dry-run, and sync output`

Not breaking: the public API is the CLI's commands and flags (`CONTRIBUTING.md`), and no
command, argument, or flag changes. The `validate` summary, the `apply --dry-run` planned
state, and the `sync` success line gain a count and a section; stdout text is not part of
the declared public API, so this is a MINOR `feat`, not a MAJOR change.

- [x] 1.1 In `src/Freeboard.CLI/GitOpsCommands.cs`, add an integration count to
  `PrintSummary`'s `OK:` line, after the collector count, reading
  `config.IntegrationConnections.Count` and pluralised as `integration(s)` to match the
  existing count style. Do not fold it into the collector count.
- [x] 1.2 In the same file, add the same integration count to the `Synced:` line in
  `Sync`, in the same position and wording, so the two lines report the same kind set.
- [x] 1.3 In `PrintPlannedState`, add an `Integrations ({count}):` section after
  `Collectors`, printing one line per connection: id, title, provider, base URL,
  discovery cadence, and the vendor reference (`-` when absent), following the existing
  per-resource line style. Print no token value and no token-health field - `apply
  --dry-run` makes no network call and reads no out-of-band token configuration.
- [x] 1.4 Extend `tests/Freeboard.CLI.Tests/fixtures/valid/config.yaml` from five kinds to
  all seven: add one `Integration` (id, title, `provider: fleet`, absolute `base_url`,
  a `discovery_cadence`) and one `Collector` of `type: integration` naming that
  connection, the same `provider`, an existing control, a `frequency`, and the
  `config.checks` list its `(integration, fleet)` schema requires. Add `evaluation` to the
  control the collector attaches to - a control with an attached collector of any type
  requires it.
- [x] 1.5 Add the per-kind count assertions that do not exist yet to
  `ValidateValidConfigExitsZeroAndPrintsCounts` and
  `ApplyDryRunExitsZeroAndPrintsPlannedState` in
  `tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs`. Today the first asserts only
  `Contains("standard(s)", stdout)` and the second only `"Planned config state"` and
  `"std-a"`, so both stay green through the fixture change in 1.4 and neither would
  notice a missing kind. This task is what makes the seven-kind fixture load-bearing:
  assert a count for each of the seven kinds on the summary line (including
  `integration(s)`), and assert the planned state carries a section per kind including an
  `Integrations` section listing the connection by id and provider.
- [x] 1.6 In `tests/Freeboard.CLI.Tests/SyncAndMigrateCommandTests.cs`, extend
  `SyncValidConfigImportsOnceAndExitsZero` in place - it already syncs `FixtureDir("valid")`,
  which task 1.4 gives an `Integration`, and it currently discards stdout. Capture stdout
  and assert the `Synced:` line carries the integration count. This uses the existing fake
  importer, so it needs no database.

## 2. Prove the seven-kind sync and warning guarantees at the command surface

Commit: `test(cli): cover integration prune, the discovered-asset guarantees, and the asset-edge warnings`

Tasks 2.1 to 2.5 go in `tests/Freeboard.CLI.Tests/SyncMySqlIntegrationTests.cs`, which is
gated on `FREEBOARD_TEST_DB` and skips cleanly when it is unset, so `dotnet test` still
passes with no external dependency. Tasks 2.6 to 2.8 go in
`tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs` and task 2.9 in
`tests/Freeboard.CLI.Tests/SyncAndMigrateCommandTests.cs`; those four need no database.
Keep each test to command wiring - the exhaustive per-edge matrices stay in the Core and
persistence suites.

The store-level discovered-asset invariants are ALREADY proven by
`tests/Freeboard.Persistence.Tests/AssetUnificationIntegrationTests.cs`
(`DeclaredOnlySyncReconcilesDeclaredAndLeavesDiscoveredUntouched` and
`DeclaredIdCollidingWithDiscoveredFailsSyncWithNoMutation`). Tasks 2.3 to 2.5 add only
what those cannot see: the exit code and the stderr message the command maps them to.
Do not restate the importer's assertions.

- [x] 2.1 Add `integration_connections` and `collectors` to the table list asserted by
  `SyncWithMigrateOnEmptyDbBootstrapsMigratesImportsExitsZero`, and assert their row
  counts, so the bootstrap test covers all seven persisted kinds. This depends on task
  1.4: the test syncs `FixtureDir("valid")`, and those two tables are non-empty only once
  the fixture carries the `Collector` and `Integration` documents 1.4 adds. Rewrite the
  test's leading comment while editing it: it says "schema_migrations + six tables", which
  the wider table list makes false, and it opens with an `IF-3 (b)` marker that names no
  concept a reader of the code can resolve - state what the test proves instead. The same
  marker style on the other tests in the file is left alone; this change edits only this
  one.
- [x] 2.2 Edit `SyncRoundTripThenDropRemovesDroppedNewKindRowsKeepingTargets` in place -
  not a sibling test, because one round-trip-and-drop case per surface is the discipline
  Decision 6 sets and a second copy would drift. Today `conn-a` appears in both
  `FullConfig` and `DroppedConfig`, so the connection prune is never exercised at the
  command surface. Drop one connection while retaining a second:
  - In `FullConfig`, add `vendor: vendor-a` to `conn-a` (nothing in the suite authors the
    optional vendor reference today), and add a second `Integration` `conn-drop`
    (`provider: fleet`, an absolute `base_url`, a `discovery_cadence`, no `vendor`) plus a
    `Collector` `ec-conn-drop` of `type: integration` naming it, with `provider: fleet`,
    `control: ctrl-a`, a `frequency`, and the `config.checks` its pair requires.
  - In `DroppedConfig`, omit `conn-drop` and `ec-conn-drop`. It keeps `conn-a` and
    `ec-keep`, so a retained collector-and-connection pair still exists in the store.
  - After the first sync, assert `COUNT(*) FROM integration_connections` is `2`, and read
    `conn-a`'s `provider`, `base_url`, `discovery_cadence`, and `vendor_id` back as
    authored - the same round-trip readback the test already does for `ec-keep`'s `type`
    and `config`. Assert `integration_connections` has no column whose name contains
    `token`: the table carries none by construction, and that is how "no token value is
    stored" holds.
  - After the drop sync, assert exit `0`, `COUNT(*) FROM integration_connections` is `1`,
    `conn-drop` is gone, `conn-a` remains, and `ec-conn-drop` is gone.
  - Existing assertions that must change: the post-full-sync `COUNT(*) FROM collectors`
    goes from `4` to `5`, and the post-drop `collectors WHERE id IN ('ec-drop', 'at-drop')`
    list gains `'ec-conn-drop'`. The post-drop `COUNT(*) FROM collectors` is still `2` and
    `collectors WHERE id IN ('ec-keep', 'at-keep')` is still `2`; leave both as written.
    Rewrite the test's leading comment to state what the test now proves. Two of its
    clauses become false: it says the narrowed config drops one scope and two collectors,
    and it ends with "exercises both removal paths: the whole-set scope replace and the
    DeleteAbsent collector prune" - the drop now also removes a connection, so the
    `DeleteAbsent` prune of `integration_connections` is a third removal path.
  - Rewrite the leading comments on both edited fixture constants, which the same edits
    falsify. `FullConfig`'s says "Standard/requirement/control plus a vendor, two
    vendor-subject scopes, and four collectors - two data sources and two attestations":
    adding `ec-conn-drop` makes it five collectors, three of them data sources, so both the
    count and the breakdown go false, and neither names the second connection. `DroppedConfig`'s
    says "The full config with vs-drop, ec-drop, and at-drop removed; every FK target is
    retained": the enumeration is incomplete once `conn-drop` and `ec-conn-drop` are also
    omitted, and the retained-FK-target claim is now wrong in the way that matters - `conn-drop`
    is `ec-conn-drop`'s FK target and is deliberately dropped with it, which is the point of
    the new case.

  What this proves: collector-before-connection prune order through the command - dropping
  `ec-conn-drop` and `conn-drop` in the wrong order raises the `RESTRICT` foreign key.
  Connection-before-asset order is NOT proved here and is not this task's job; it is owned
  by `tests/Freeboard.Persistence.Tests/IntegrationConnectionIntegrationTests.cs`
  `HardRemoveDropsCollectorThenConnectionWithoutFkViolation` (line 189), which drops a
  connection together with the vendor asset it referenced. Retaining `conn-a` and `ec-keep`
  also keeps a config satisfying the ratified "Dropping a collector hard-removes it on the
  next sync" scenario, whose WHEN keeps the omitted collector's control, vendor asset, and
  connection.
- [x] 2.3 Add a test that a `gitops sync` whose config declares no `Machine` assets leaves
  a pre-inserted discovered machine row untouched: seed a discovered asset directly,
  sync a declared-only config, assert exit `0` and that the discovered row is still
  present with `source = 'discovered'`. The point of this test is the exit code - that
  the command treats absent machines as out of its remit rather than as an error or a
  removal. Keep it to that; the row-level survival matrix is the persistence suite's.
- [x] 2.4 Add a test for the zero-asset config against a mixed store, which 2.3 does not
  cover: seed a discovered machine, sync a config declaring assets, then sync a config
  with zero assets of any kind. Assert exit `0`, that the previously declared asset rows
  are gone, and that the discovered machine row remains. This is the case an operator
  most fears - an emptied config - and it is the second half of the discovered-asset
  guarantee, so it needs its own command-level case rather than riding on 2.3.
- [x] 2.5 Add a test that a `gitops sync` declaring an asset whose id equals a
  pre-inserted discovered asset's id exits `3`, and that stderr names the colliding id and
  states nothing was written. Assert one row the sync would otherwise have created is
  absent, to prove the command aborted rather than partially applied. This is the
  exit-code and message mapping the importer test cannot observe. Scope the store
  assertions to that: the whole-store no-mutation guarantee belongs to `asset-model` and is
  proven by `DeclaredIdCollidingWithDiscoveredFailsSyncWithNoMutation`.
- [x] 2.6 Extend the existing `WarningsOnlyConfig` fixture in
  `tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs` rather than adding a third
  near-duplicate warnings fixture. It is today a lone ownerless declared `Vendor`, which
  covers the missing-owner edge only. Add: a root `Company` with no `parent` (which must
  draw no warning), a `Department` whose `parent` names an id no document defines, a
  `Vendor` whose `owner` names an id no document defines, and a `Machine` with no
  `parent`. Keep the existing ownerless `vendor-a` document so the two tests that
  already consume the fixture (`ValidatePrintsWarningsOnValidPathAndExitsZero` and
  `ApplyDryRunPrintsWarningsOnValidPathAndExitsZero`) keep asserting what they assert.
- [x] 2.7 Extend `ValidatePrintsWarningsOnValidPathAndExitsZero` for the two unknown-id
  edges the suite does not cover: assert the warning for the `Department`'s unknown
  `parent` and the warning for the `Vendor`'s unknown `owner` each reach stderr naming
  the asset and the unknown id, that the success summary still prints to stdout, that the
  exit code is `0`, and that the root `Company` draws no warning.
- [x] 2.8 Extend `ApplyDryRunPrintsWarningsOnValidPathAndExitsZero` for the
  missing-required-edge case on the DB-less write-preview surface: assert warnings for
  the ownerless `Vendor` and the parentless `Machine`, no warning for the root `Company`,
  that the planned state still prints, and exit `0`.
- [x] 2.9 In `tests/Freeboard.CLI.Tests/SyncAndMigrateCommandTests.cs`, add a test
  alongside `SyncPrintsNonScopeCoreWarning` covering the unknown-id edge on the `sync`
  path, which no test covers today: a config whose asset names a `parent` id no document
  defines prints the warning naming the asset and the unknown id to stderr, imports, and
  exits `0`. The missing-anchor half of the rule is already covered on `sync` by
  `SyncPrintsNonScopeCoreWarning` here and by
  `SyncMySqlIntegrationTests.SyncPrintsNonBlockingWarningToStderrAndExitsZero`. Use the
  existing fake importer, so this needs no database.

## 3. Pin the retired v1 asset kinds as unknown kinds

Commit: `test(core): cover the retired v1 top-level asset kinds as unknown kinds`

Its own commit because it edits `tests/Freeboard.Core.Tests` while group 2 edits
`tests/Freeboard.CLI.Tests`, so each commit's scope names one affected area.

- [x] 3.1 In `tests/Freeboard.Core.Tests/ConfigLoaderTests.cs`, add a theory alongside
  `RetiredCollectorKindsAreNowUnknown` covering the retired v1 top-level kinds `Vendor`,
  `Organisation`, and `Machine` with an asset-shaped document body. Assert each yields
  `Unknown kind '<token>'`, loads no asset, and that the diagnostic's valid-kinds
  enumeration (the text after `Expected one of:`) contains `Asset` and does not contain
  the retired token. These three are the only retired tokens with no negative coverage;
  `EvidenceCollector`, `AttestationTemplate`, `VendorScope`, and `RequirementScope`
  already have it in `ConfigLoaderTests` and `ScopeValidationTests`, so do not duplicate
  them. No spec delta: `gitops-config-format`'s unknown-kind requirement already
  enumerates the seven legal kinds and governs these.

## 4. Make the shipped example config a true seven-kind sample

Commit: `docs(gitops): complete the shipped example and correct the command and hard-removal docs`

- [x] 4.1 Add `examples/gitops/integrations.yaml` with one `Integration` (a Fleet
  connection: `provider: fleet`, an absolute `base_url`, a `discovery_cadence`, and the
  optional `vendor` pointing at the existing Fleet vendor asset in `vendors.yaml`).
  Author no token field - the token is resolved out-of-band by connection id.
- [x] 4.2 Add `examples/gitops/collectors.yaml` covering more than one collector type so
  the sample shows the typed `config` contract: one `integration` collector naming the
  connection from 4.1 with a matching `provider` and its required `config.checks`, one
  `manual` collector, and one `training` collector with its required `config.pass_mark`
  and `config.quiz`. Attach them to controls that exist in `controls.yaml`.
- [x] 4.3 Add an `evaluation` rule (`all`, `any`, or `manual`) to each control in
  `examples/gitops/controls.yaml` that gains an attached collector. Validation requires
  it once a control has one; without this the example will not validate.
- [x] 4.4 Update the layout table and prose in `examples/gitops/README.md`: add rows for
  the two new files, and make the "exercises every kind" claim true rather than
  aspirational. Keep the existing note that kinds may be mixed across files.
- [x] 4.5 Update `docs/gitops.md` to state what the commands print: in `## Commands`, that
  `validate` prints one count per declared kind and `apply --dry-run` one planned-state
  section per declared kind; and where `sync` is documented (today only its exit codes),
  that its success line carries one count per declared kind. Keep it to the shape; do not
  restate the per-kind field lists the document already carries.
- [x] 4.6 Correct the `### Hard removal warning` section of `docs/gitops.md`. It states
  that `sync` hard-removes any persisted resource whose `id` is absent from the config,
  with no carve-out; for assets that is untrue, because the importer filters deletes on
  `source = 'declared'` and never removes a discovered machine. Add one sentence naming
  the carve-out. Do not restate the declared/discovered model the `### Asset` section
  already covers.
- [x] 4.7 Run `freeboard gitops validate examples/gitops` and
  `freeboard gitops apply examples/gitops --dry-run` locally and confirm exit `0` and that
  the collectors and integrations appear in both outputs.

## 5. Gate the shipped example config directories in CI

Commit: `ci: validate the shipped example configs in the lint job`

- [x] 5.1 Add a step to the `Lint` job in `.github/workflows/test.yml`, after the build
  step, that runs `dotnet run --project src/Freeboard.CLI -c Release --no-build --
  gitops validate <dir>` for `examples/gitops` and `examples/fixture-corp`, failing the
  job on a non-zero exit. Those are the two config roots an operator is pointed at.
  For `examples/gitops`, also assert completeness: capture the summary line and fail
  unless it reports a non-zero count for each of the seven declared kinds. Validity
  alone would still pass with a kind's documents deleted, which is what the example's
  "exercises every kind" claim needs pinned. `examples/fixture-corp` makes no such
  claim and authors no collector or integration, so validity is its whole contract.
  `examples/shared` is deliberately not a third target: it holds only the
  `cyber-essentials-plus.yaml` catalog fragment that both roots symlink in, so the step
  already loads it through each of them, and validating it standalone would assert a
  contract the fragment does not have. Place the step in `Lint` rather than
  `Unit Tests` so it does not depend on the test tooling and reads as the doc/example
  conformance gate it is.
- [x] 5.2 Confirm the step's flags match how the Lint job already builds (Release,
  `--no-restore`/`--no-build` as appropriate) so it adds no second build.

## 6. Verification

- [x] 6.1 `dotnet build Freeboard.slnx --configuration Release -warnaserror` - clean.
- [x] 6.2 `dotnet format Freeboard.slnx --verify-no-changes` - clean.
- [x] 6.3 `dotnet test` with `FREEBOARD_TEST_DB` unset - passes, with the new MySQL sync
  tests skipping cleanly.
- [x] 6.4 Bring up the test services
  (`docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d`),
  export `FREEBOARD_TEST_DB`, and run `dotnet test tests/Freeboard.CLI.Tests` - the new
  integration-prune, discovered-asset, empty-config, and collision tests pass against
  real MySQL.
- [x] 6.5 `npx markdownlint-cli2 "**/*.md"` - clean (covers `docs/gitops.md` and
  `examples/gitops/README.md`).
- [x] 6.6 Sweep for retired authored tokens creeping back:
  `rg -n "kind: (Vendor|Organisation|Machine|EvidenceCollector|AttestationTemplate|VendorScope|RequirementScope|IntegrationConnection)" src tests docs examples openspec/specs`.
  Every hit must be a negative test, the documented migration list in `docs/gitops.md`,
  or ratified spec prose recording what a token used to mean. A hit in an example, a
  fixture, or a spec's authored YAML is a defect. The C# method names
  `UpsertRequirementScopeDispositionAsync`/`DeleteRequirementScopeAsync` are code
  identifiers, not authored tokens, and are out of scope.
- [x] 6.7 Re-read the change against `.claude/rules/`: ASCII punctuation in every file
  touched, no process references in code, comments, or commit messages, and no
  `Freeboard.Enterprise` reference reachable from `Freeboard.CLI`.
- [x] 6.8 Re-read every comment on a test, fixture constant, or config document this change
  edits and confirm each still states the truth - counts, enumerations, and claims about what
  the test proves - fixing or deleting any the edits falsified (`.claude/rules/comment-etiquette.md`:
  a wrong comment is worse than none).
- [x] 6.9 `openspec validate "gitops-v2-kinds-validate-apply-sync" --strict` - passes.
