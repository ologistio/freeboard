## Why

The object-model v2 work (asset unification, scope generalization, collector merge,
and the ratified `Integration` kind) landed the seven declared kinds end to end in
`Freeboard.Core` (loader plus validator) and `Freeboard.Persistence` (importer). The
`gitops` command surface did not keep up: `validate`, `apply --dry-run`, and `sync`
report six of the seven kinds. `Integration` is loaded, validated, upserted, and
hard-removed, yet it appears in no count, no planned-state section, and no synced
line. An operator can therefore delete every `integration_connections` row - the
rows that bind a Fleet instance to its collectors - with zero feedback before or
after the write.

The same gap runs through the coverage. The shipped example config that the repo
README tells operators to validate carries no `Collector` and no `Integration`
document, despite its own README claiming it "exercises every kind". The CLI
`fixtures/valid` config carries five of seven kinds. No command-level test asserts
that a connection persists, that dropping one hard-removes it, that a declared-only
sync leaves discovered machines alone, or that a declared id colliding with a
discovered id fails the command without writing.

## What Changes

- `gitops validate` counts integrations in its success summary, so the summary names
  every declared kind.
- `gitops apply --dry-run` prints an `Integrations` section in its planned state,
  alongside the six existing sections.
- `gitops sync` counts integrations on its `Synced:` line.
- Command-level coverage for the behaviour the acceptance criteria name: an
  integration round-trips and hard-removes through `sync`; a declared-only sync never
  touches a discovered asset, including on an emptied config; a declared id colliding
  with a discovered id exits `3` writing nothing.
- The asset-edge warnings are partly covered already and only the gap is added. The CLI
  suite proves today that an ownerless declared `Vendor` and a dangling scope `subject`
  each warn on stderr and still exit `0`, on both `validate` and `apply --dry-run`, and
  that a missing anchor warns without failing on `sync`. What no test covers is a `parent`
  or `owner` naming an id no document defines - on `sync` as much as on `validate` and
  `apply --dry-run` - a `Machine` with no `parent`, and the negative half of the rule, that
  a root `Company` with no `parent` draws no warning. The existing warnings fixture is
  extended to carry those cases rather than a third near-duplicate fixture being added,
  and the unknown-id case on `sync` is covered on the DB-less fake-importer path.
- The CLI `fixtures/valid` config becomes a seven-kind config, so the default
  validate/dry-run assertions exercise every kind rather than five.
- The retired v1 top-level kinds `Vendor`, `Organisation`, and `Machine` gain the
  unknown-kind coverage the other retired tokens already have. They are the likeliest
  stale-config mistake because the same words remain legal `Asset.type` values, and
  today nothing pins that `kind: Vendor` errors rather than loading.
- `docs/gitops.md`'s hard-removal warning gains the discovered-asset carve-out. As
  written it says `sync` hard-removes any persisted resource absent from the config,
  which for assets is untrue and would reasonably lead an operator to fear that a
  config with no `Machine` documents wipes their discovered inventory.
- `examples/gitops` gains `Collector` and `Integration` documents (and the
  `evaluation` rules the attached collectors make required), and its README layout
  table is corrected. A CI lint step runs `gitops validate` over the shipped example
  directories, so the operator-facing sample is proved rather than asserted.
- **No change** to `Freeboard.Core` or `Freeboard.Persistence`. Loading, validation,
  import, prune order, and discovered-asset protection for all seven kinds already
  exist and are covered by their own suites. This change is the command surface and
  its proof.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `gitops-cli`: the `validate` summary and the `apply --dry-run` planned state must
  cover every declared kind including `Integration`; the `sync` synced line must
  count integrations and its hard-remove coverage must include an integration at the
  command surface; the command-group documentation requirement gains the printed output
  shape and the hard-removal discovered-asset carve-out, so the two documentation fixes
  are pinned rather than left to drift again; and two command-surface behaviours gain
  requirements - the exit codes and stderr message `sync` maps the declared-only asset
  guarantee onto (`asset-model` owns the store-level guarantee itself), and
  dangling/missing asset edges as non-blocking warnings.
- `gitops-config-format`: the generic example config must contain at least one
  document of every supported kind, and every shipped example config root must validate
  clean in CI, so the worked sample matches the documented kind set.

## Impact

- `src/Freeboard.CLI/GitOpsCommands.cs` - the only source file changed.
- `tests/Freeboard.CLI.Tests/` - `fixtures/valid/config.yaml`, `GitOpsCommandTests.cs`,
  `SyncAndMigrateCommandTests.cs`, `SyncMySqlIntegrationTests.cs`.
- `tests/Freeboard.Core.Tests/ConfigLoaderTests.cs` - one theory for the retired v1
  asset kinds.
- `examples/gitops/` - new `integrations.yaml` and `collectors.yaml`; modified
  `controls.yaml` (an `evaluation` rule per control that gains a collector) and
  `README.md` (the layout table).
- `docs/gitops.md` - the command output shape and the hard-removal carve-out.
- `.github/workflows/test.yml` - one lint step validating the shipped example config
  roots (`examples/gitops` and `examples/fixture-corp`).
- No database migration, no schema change, no new dependency, and no HTTP API,
  database schema, or public C# API change. CLI stdout gains one count and one
  planned-state section; the commands and flags themselves are unchanged, so this is not
  a public API change under `CONTRIBUTING.md`'s definition.

## Licensing

MIT (the repo default). Everything here is in `Freeboard.CLI`, its tests, the
examples, and docs. `Freeboard.CLI` ships as a community component and must build and
run on Windows, Linux, and macOS; nothing in this change references
`Freeboard.Enterprise` or introduces platform-specific code.

## Non-goals

- Real reconciling `apply`. `apply` without `--dry-run` keeps exiting `2`; `sync`
  stays the write path. Changing that is a separate increment with its own diffing
  and safety story.
- A retired-kind migration hint. `kind: Vendor`, `Organisation`, `Machine`, `VendorScope`,
  `RequirementScope`, `EvidenceCollector`, and `AttestationTemplate` keep producing
  the generic unknown-kind diagnostic the ratified spec requires. `docs/gitops.md`
  already carries the hand-migration list.
- CLI read commands for compliance read-models. Delegated to issue #65; see
  `design.md` for the decision and the residual list.
- Any change to `Freeboard.Core` validation rules or `Freeboard.Persistence` import
  and prune behaviour.
- A `--prune=false` or confirmation gate on `sync`. The hard-remove semantics are
  ratified and documented; softening them is a separate proposal.
- Collectors or integrations in `examples/fixture-corp`. Its README does not claim to
  cover every kind, and widening the worked CE+ example is not needed to prove the
  command surface.
- Renaming the authored kind token. The original issue #67 prose used
  `IntegrationConnection`, but the ratified `gitops-config-format` and
  `integration-connection` specs fix the wire token as `Integration` and a Core test
  already pins `IntegrationConnection` as an unknown kind. The spec wins over the issue
  prose; see `design.md` Decision 7. No rename and no alias.
- An integration count on `ComplianceCounts` and `/compliance/status`. That payload's
  key set is ratified in `compliance-web-read`, no command in this change reads it, and
  no acceptance bullet names it; see `design.md` Decision 8.
