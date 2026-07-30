## Context

Issue #67 was written against the pre-v2 model, where `validate`/`apply`/`sync` had
to grow four new kinds (`Vendor`, `VendorScope`, `EvidenceCollector`,
`AttestationTemplate`). Object model v2 collapsed that surface. The declared set is
now seven kinds: `Standard`, `Requirement`, `Control`, `Asset`, `Scope`, `Collector`,
`Integration` (the `IntegrationConnection` model type; the authored `kind` token is
`Integration`).

What is already on disk, verified file by file:

- `Freeboard.Core` `ConfigLoader` dispatches exactly those seven tokens from
  `GitOpsSchema`, rejects anything else with an unknown-kind error naming the
  expected set, and closes the `Collector.config` key set per `(type, provider)`
  against `CollectorConfigSchema`.
- `Freeboard.Core` `ConfigValidator` enforces every v2 rule the acceptance criteria
  name: the two-edge asset rules (mutual exclusion, carrier-type and target-type
  rules), exactly-one scope target, justification-required-on-`Out`, required
  collector config keys per pair, and collector-provider versus connection-provider
  agreement. Dangling `parent`, dangling `owner`, parent cycles, missing required
  edges, and a dangling scope `subject` are all `Severity.Warning`, so they do not
  fail a command.
- `Freeboard.Persistence` `MySqlGitOpsImporter` persists all seven kinds, prunes in
  FK-safe order (collectors, then integration connections, then declared assets, then
  controls, requirements, standards; the unified scope set is replaced wholesale
  first), guards every declared-asset delete with `source = 'declared'`, and fails the
  whole transaction before any write when a declared id collides with an existing
  discovered asset.

The residual is the command surface. `GitOpsCommands.PrintSummary`, the `Sync`
`Synced:` line, and `PrintPlannedState` each enumerate six kinds and never read
`config.IntegrationConnections`. Around that, the proof is thin: the CLI
`fixtures/valid` config has five kinds, the CLI sync round-trip test keeps its
`Integration` document in both the full and the dropped config (so the connection
prune is never exercised at the command surface), and neither shipped example
directory contains a `Collector` or an `Integration` document.

The shape this design settles on, stated once so the decisions below do not restate it:
parsing and validation stay in `Freeboard.Core` with the CLI calling the loader rather
than duplicating it; reconciliation stays in `Freeboard.Persistence`; `apply` stays
dry-run-only with `sync` as the write path; the integration kind gets its own count and
its own planned-state section printing identity, provider, base URL, cadence, and vendor
and no secret; CLI read parity is delegated to issue #65; and the specs, docs, and
examples move in the same change as the code.

## Goals / Non-Goals

**Goals:**

- `validate`, `apply --dry-run`, and `sync` each report all seven declared kinds.
- Prove at the command surface, not only in Core and persistence, that: an
  integration round-trips and hard-removes; a declared-only sync leaves discovered
  assets untouched; a declared/discovered id collision fails with nothing written;
  dangling `parent`/`owner` warn without failing.
- Make the shipped example config an honest seven-kind sample and pin it in CI.

**Non-Goals:**

- Real (non-dry-run) `apply`.
- Any change to Core validation rules or persistence import/prune behaviour.
- A retired-kind migration hint in the loader diagnostic.
- General CLI read parity with the web read-models (see Decision 2).
- Widening `examples/fixture-corp`.

## Decisions

### Decision 1: Change the CLI presentation layer only; add nothing to Core or Persistence

The seven-kind loading, validation, and import already exist and carry their own
exhaustive suites (`CollectorValidationTests` ~78 cases,
`IntegrationConnectionValidationTests` 25, `ScopeValidationTests` 29,
`AssetValidationTests` 21, `ImportPlanTests` 26, plus the MySQL integration suites).
Re-implementing or wrapping any of it here would be a second system doing the first
system's job.

Concretely, the source diff is one file: `GitOpsCommands.cs` gains one count in
`PrintSummary`, one count on the `Sync` line, and one section in
`PrintPlannedState`.

_Alternative considered:_ introduce a per-kind renderer abstraction so a future
eighth kind is one registration rather than three edits. Rejected as a speculative
abstraction with one caller; three literal edits in one file are smaller and read
without explanation.

### Decision 2: CLI read parity is delegated to issue #65, and the residual is enumerated

Issue #67's last acceptance bullet reads "CLI read parity with web (see #65)". Issue
#65 ("CLI read commands for compliance read-models", still OPEN) scopes itself to
"scores, status, rollups, per-vendor breakdown, and evidence history" through the
HTTP API. The bullet is a pointer to that ticket, not a second copy of it.

Read parity for the kinds this change touches is already met where the CLI has a
command: `vendor list` reads `/vendors` plus `/scopes` and prints each vendor asset
with its scope exceptions and justifications; `collector list` reads `/controls` plus
`/collectors` and prints one merged collector listing; `connections list` reads
`/integration-connections` and prints provider, base URL, cadence, and token health.
All three go through the HTTP API, never the database, per the established rule.

The residual read gaps, recorded here so #65 has a concrete backlog rather than a
restated goal, are the web endpoints with no CLI counterpart:
`GET /standards`, `GET /requirements`, `GET /organisations` (the Company/Department
asset tree), `GET /statement-of-applicability/{standardId}`, and
`GET /compliance/status`.

One further residual, unclaimed by either ticket: `/compliance/status` reports counts
for seven kinds and has no integration count, because `ComplianceCounts` predates the
`Integration` kind. See Decision 8 for why closing that here would be scope with no
consumer.

Building those here would fork ownership with an open ticket and pull this change
from a one-file presentation fix into a new command group with its own API-client
methods, response records, and fakes - liability #67 does not need to discharge to
meet its own first three acceptance bullets.

_Alternative considered:_ implement the five missing read surfaces here and close #65
as duplicate. Rejected: it triples the change's blast radius, and the read-model
shape (in particular the SoA projection and the status rollup) is the substance of
#65's own design. The narrower variant - add only the read and status pieces needed to
avoid seven-kind blind spots - collapses into this decision plus the integration count,
which is the only GitOps-surface blind spot that exists.

### Decision 3: Integration is its own count and its own planned-state section

An `Integration` is a distinct kind with a distinct prune step and a distinct blast
radius: `collectors.connection_id` is `ON DELETE RESTRICT`, so a collector that names a
connection must go first - which is why the importer prunes collectors before
connections, and why a config that drops a connection while keeping a collector that
references it fails validation rather than removing either. Its id is also the key that
resolves the out-of-band API token. Folding it into the collector count would hide exactly the
row an operator most needs to see before a narrowing sync deletes it.

The planned-state section keeps the same discipline as the collector section: print
identity, provider, base URL, cadence, and the optional vendor reference. There is no
secret to omit - the connection carries no token field by construction - but the
section stays a one-line-per-resource summary for consistency with every other
section.

### Decision 4: Prove the example config in CI with a lint step, not with a test that walks to the repo root

`examples/gitops/README.md` claims the sample "exercises every kind" and the repo
README tells operators to run `freeboard gitops validate examples/gitops`. Nothing
checks either claim today.

The check belongs in `.github/workflows/test.yml`'s existing Lint job as a
`dotnet run --project src/Freeboard.CLI -- gitops validate <dir>` step over
`examples/gitops` and `examples/fixture-corp`. That is configuration rather than
code, it exercises the real command rather than a library call, and it fails loudly
when someone edits an example into an invalid state.

Validity is not the whole claim, though, so the step does two things rather than one.
A `validate` that only has to exit `0` would still pass with `examples/gitops`'
`integrations.yaml` or `collectors.yaml` deleted - the README's "exercises every kind"
claim would silently go false again. The step therefore captures the `examples/gitops`
summary line, which already carries one count per declared kind, and fails unless every
one of the seven counts is non-zero. That is a few lines of shell in the step that
already runs the command, not a second gate. `examples/fixture-corp` gets the validity
check only: it makes no completeness claim and authors no collector or integration.

Those two are the whole target set, and the third directory under `examples/` is
deliberately excluded. `examples/shared/` holds exactly one file,
`cyber-essentials-plus.yaml` - the CE+ standard plus its 35 requirements - and both other
directories symlink it in (`examples/gitops/cyber-essentials-plus.yaml` and
`examples/fixture-corp/standards/cyber-essentials-plus.yaml`). It is a catalog fragment
authored to be composed with a company's own documents, not a config root an operator points
the command at, so validating it standalone would assert a completeness contract it does not
have - while adding no coverage, because each root that symlinks it already loads it. The
`gitops-config-format` delta therefore names the roots it governs and records the exclusion
and its reason, rather than saying "every shipped example directory" and leaving a reader to
find the mismatch with the CI step.

_Alternative considered:_ a `Freeboard.Core.Tests` case that resolves the repo root
from `AppContext.BaseDirectory` and loads `examples/gitops`. Rejected: path-walking
from a test bin directory is brittle, it duplicates what the CLI already does, and it
would not catch a break in the command itself.

### Decision 5: Extend the existing CLI `fixtures/valid` config rather than add a second fixture

The fixture is already copied to the test output and drives
`ValidateValidConfigExitsZeroAndPrintsCounts` and
`ApplyDryRunExitsZeroAndPrintsPlannedState`. Growing it to seven kinds puts every kind
through the default happy path.

Growing it does not by itself buy an assertion, and it is worth being exact about why.
Neither test asserts a count today: `GitOpsCommandTests.cs:64-71` asserts only
`Contains("standard(s)", stdout)` and `:91-98` asserts only `"Planned config state"` and
`"std-a"`. Both therefore stay green through the fixture change, and would stay green if a
kind vanished from the output - which is the defect this change exists to fix. So the
fixture growth is not load-bearing on its own; the per-kind count and per-section
assertions, which do not exist yet and must be added, are what make it load-bearing. That
is task 1.5, and it lands in the same commit as the fixture.

The fixture needs a `Collector` and an `Integration`. Adding a collector makes
`evaluation` required on the control it attaches to, so the fixture's control gains
one - the same edit operators must make, which is worth having in a fixture.

_Alternative considered:_ a second `fixtures/seven-kinds` directory. Rejected: two
"valid" fixtures invite drift over which one is canonical.

### Decision 6: Command-surface tests prove wiring; the exhaustive matrices stay where they are

The ratified `gitops-cli` spec states this division for one family of rules:
`openspec/specs/gitops-cli/spec.md:137-146` scopes the command surface to a representative
dangling reference per kind and assigns the exhaustive per-edge matrix to the
`Freeboard.Core` ConfigValidator unit tests. That clause is about referential integrity
only.

The other v2 rules the issue's acceptance names - the two-edge asset rules, exactly-one
scope target, and justification-required-on-`Out` - have no command-surface case today, and
this change adds none. Being plain about it: they are delegated to the Core suites by
analogy with that clause, not by an existing clause that covers them. The analogy holds
because those rules are DB-less validator predicates with a single code path into the CLI:
`ConfigValidator.LoadAndValidate` returns error diagnostics and the command prints them and
exits `1`. That path is already proven at the command surface by the referential-integrity
cases, so a per-rule command test would assert the same plumbing a different way. If a rule
ever gained its own command-level presentation, it would need its own case.

The new CLI tests follow the same discipline - one integration round-trip-and-drop, one
discovered-asset survival, one emptied-config survival, one collision failure, and the
missing asset-edge warnings - rather than mirroring `AssetUnificationIntegrationTests` or
`IntegrationConnectionIntegrationTests` at the command level.

The MySQL-backed additions go in `SyncMySqlIntegrationTests`, which is already gated
on `FREEBOARD_TEST_DB` and skips cleanly when it is unset, so `dotnet test` still
passes out of the box.

### Decision 7: The authored kind token stays `Integration`; no rename, no alias

Considered and rejected: renaming the authored token to `IntegrationConnection` across
specs, docs, fixtures, code, and tests, on the grounds that issue #67's prose uses that
name.

The ratified spec, not the issue prose, is the source of truth
(`.claude/rules/code-review.md`). Three independent places fix the authored token as
`Integration`:

- `openspec/specs/gitops-config-format/spec.md` lines 20, 71, 228, and 582-586 author
  and enumerate `Integration`.
- `openspec/specs/integration-connection/spec.md:5` reads "Model a provider integration
  as a first-class GitOps kind - an `Integration`".
- `src/Freeboard.Core/GitOps/ConfigModel.cs:18` is
  `public const string KindIntegrationConnection = "Integration";` - the C# constant
  name is the domain concept (`IntegrationConnection` is the model type and the
  `integration_connections` table), and its value is the authored wire token. That split
  is deliberate, not an inconsistency.

The token is also pinned negatively:
`tests/Freeboard.Core.Tests/IntegrationConnectionValidationTests.cs`
`RetiredIntegrationConnectionKindIsNowUnknown` (line 148, token assertions at 166-176)
asserts that `kind: IntegrationConnection` produces `Unknown kind 'IntegrationConnection'`
and that the valid-kinds enumeration contains `Integration` but not
`IntegrationConnection`. Renaming would invert a ratified, deliberately written test.

So the issue prose was stale against a later ratification. No rename, and no alias:
accepting both tokens would create two authoring contracts and the ratified spec allows
one. Putting `IntegrationConnection` on the wire would be a breaking config-format change
needing its own proposal against `gitops-config-format` and `integration-connection`, not
a side-effect of a CLI output fix.

### Decision 8: No `ComplianceCounts` or `/compliance/status` change here

Considered and rejected: adding integration connections to `ComplianceCounts` and the
`/compliance/status` payload as part of this change.

`openspec/specs/compliance-web-read/spec.md:219-247` ratifies the
exact key set - standards, controls, requirements, organisations, scopes, vendors,
collectors - with a literal JSON example and a scenario naming those keys. Adding an
eighth key is an observable API change to a capability this change does not otherwise
touch, so it needs a delta on `compliance-web-read` and would pull in
`src/Freeboard.Persistence/ComplianceReadModels.cs:164`,
`src/Freeboard.Persistence/MySqlComplianceStore.cs:231-249`,
`src/Freeboard/Compliance/ComplianceEndpoints.cs:259`, both web test fakes
(`tests/Freeboard.Web.Tests/FakeComplianceStore.cs:83`,
`tests/Freeboard.Web.Tests/OrgSelectionTests.cs:165`), and the positional assertion at
`tests/Freeboard.Persistence.Tests/MySqlIntegrationTests.cs:541`.

Against `code-as-liability.md` that is new public API surface with no named consumer in
#67: none of the issue's acceptance bullets concern the web status payload, and no CLI
command in this change reads it. The capability #67 must deliver - an operator sees the
integration set before and after a sync - is delivered by the `validate`/`apply
--dry-run`/`sync` output this change adds.

Being precise about ownership: this is *not* already #65's. Issue #65 scopes itself to
scores, status, rollups, per-vendor breakdown, and evidence history over the HTTP API,
which is a CLI-read concern, not a change to what `/compliance/status` returns. So the
honest disposition is that an integration count on the web status payload is an
unclaimed follow-up, recorded in Decision 2's residual list rather than silently
assigned to a ticket that does not cover it.

### Decision 9: The discovered-asset invariant is already proven in persistence; the CLI tests prove only the command mapping

Considered: MySQL integration tests proving that `sync` never removes or overwrites
discovered assets and that a declared/discovered id collision aborts without mutation.
Checked, and the store-level invariant is already covered:

- `tests/Freeboard.Persistence.Tests/AssetUnificationIntegrationTests.cs`
  `DeclaredOnlySyncReconcilesDeclaredAndLeavesDiscoveredUntouched` (line 149)
  seeds a discovered machine, imports a declared config, narrows it, then imports an
  empty config, asserting every declared row is removed and the discovered row survives
  each time.
- The same file's `DeclaredIdCollidingWithDiscoveredFailsSyncWithNoMutation` (line 183)
  asserts the import throws and that neither the discovered row's `source` nor the
  previously declared row changed.

So tasks 2.3 to 2.5 are deliberately NOT a second copy of those. What is unproven is the
command surface: that `gitops sync` maps the importer's collision failure to exit `3` with
a message naming the id and stating nothing was written, and that the declared-only path -
including the emptied config, which an operator most fears - exits `0` rather than being
reported as an error. Exit-code and stderr mapping is CLI behaviour that the persistence
suite cannot see, and the issue acceptance bullet is written about the command. The tasks
are therefore scoped to exit code, message, and one surviving-row assertion each, and must
not restate the importer matrix.

The `gitops-cli` requirement is scoped the same way, and for the same reason Decision 10
gives for adding no retired-kind spec text: the store-level substance is already ratified,
so restating it would add spec prose without adding a rule. `asset-model`'s "Declared
assets are synced by config; discovered assets are owned by ingest" (spec lines 357-425)
already SHALLs that a sync never writes a discovered asset, that a zero-asset config never
wipes discovered inventory, and that a collision mutates nothing. The requirement here
therefore claims only the command mapping - exit `0` on the declared-only and zero-asset
paths, exit `3` plus a stderr message naming the colliding id - and cites `asset-model` as
the owner of the rest. That keeps every clause discharged by a task in this change: the
whole-store no-mutation claim is proven where it is specced, in
`AssetUnificationIntegrationTests`.

### Decision 10: Retired-kind negative coverage - only the v1 asset kinds are a real gap

Considered: negative tests asserting each retired v1 token is rejected as an unknown kind.
Checked each:

- `EvidenceCollector`, `AttestationTemplate` - already covered twice, in
  `tests/Freeboard.Core.Tests/ConfigLoaderTests.cs` `RetiredCollectorKindsAreNowUnknown`
  (around line 418, which also asserts the valid-kinds enumeration excludes them) and at
  the command surface in `tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs`
  `ValidateRejectsARetiredCollectorKind` (around line 499).
- `RequirementScope`, `VendorScope` - already covered in
  `tests/Freeboard.Core.Tests/ScopeValidationTests.cs` by
  `RetiredRequirementScopeKindIsUnknownKind` (line 632) and
  `RetiredVendorScopeKindIsUnknownKind` (line 651).
- `Vendor`, `Organisation`, `Machine` - **no coverage anywhere**. These are the v1
  top-level kinds that object model v2 folded into `Asset.type`, and they are the most
  likely stale-config mistake precisely because the words still exist as legal `type`
  values. Nothing pins that `kind: Vendor` errors rather than loading.

That gap is real and cheap to close, so it becomes a task. It closes against an existing
ratified rule rather than a new one: `gitops-config-format`'s "Unknown or missing kind
reported by the loader" scenario (spec lines 225-230) already enumerates the seven legal
kinds, so `Vendor`, `Organisation`, and `Machine` are governed by it today. A third
near-identical retired-kind scenario would add spec text without adding a rule, so this
change adds the test and no spec delta.

### Decision 11: `docs/gitops.md` is current on the seven kinds; two narrower gaps are real

Considered: a blanket kind update to `docs/gitops.md`. Checked, and it does not need one.
The `## Format` section (lines 42-44) already enumerates all seven kinds including
`Integration`, `### Integration` (lines 384-411) documents the kind with its
out-of-band token rule, and lines 363-383 carry the collector-merge migration notes.

Two narrower gaps are real:

- Output shape is documented nowhere. `## Commands` (lines 501-514) and the `sync` exit
  codes (lines 585 and 594) say what the commands return, never what they print, so nothing
  in the docs would go stale when a kind is missing from the output - the failure this whole
  change exists to fix. The fix covers all three commands, `sync` included, because its
  `Synced:` line is the count that task 1.2 changes.
- `### Hard removal warning` (lines 607-613) says `sync` "HARD-REMOVES any persisted
  resource whose `id` is absent from the config" with no carve-out. For assets that is
  simply untrue: the importer filters deletes on `source = 'declared'`, and a discovered
  machine is never removed by a sync. An operator reading that paragraph would
  reasonably fear that syncing a config with no `Machine` documents wipes their
  inventory. Correcting it is the documentation half of the same acceptance bullet
  tasks 2.3 and 2.4 prove in code.

Both gaps are pinned by a requirement rather than left as a bare task, because an
unspecced doc fix is exactly what let the drift happen. The ratified project does specify
documentation - `gitops-config-format`'s "Config-format documentation covers every
supported kind" (spec line 399) and `gitops-cli`'s "Command-group documentation reflects
the write path" (spec line 106) - but the first governs the per-kind schema sections and
the second governs only the no-network/no-writes claim, so neither covers the printed
output shape or the hard-removal carve-out. This change therefore carries a MODIFIED delta
on the `gitops-cli` documentation requirement adding both: one count per declared kind on
the `validate` summary and the `sync` success line, one planned-state section per declared
kind, and the discovered-asset carve-out on hard removal. That is the closest existing
requirement (it already governs the command group's docs), so extending it beats adding a
third documentation requirement.

A static sweep for stale authored tokens was run over `src`, `tests`, `docs`, `examples`, and
`openspec/specs`. It found no genuine stale authored-token reference. Every hit is one
of: a deliberate negative test, the documented migration list in `docs/gitops.md`, prose
in a ratified spec recording what a token used to mean, or the C# write-store method
names `UpsertRequirementScopeDispositionAsync`/`DeleteRequirementScopeAsync` on
`IComplianceWriteStore` - a code identifier for the requirement-scope write operation,
not an authored kind token, and renaming it across the interface, the MySQL store, the
authz action list, and three test suites would be pure churn against
`code-as-liability.md`. The sweep is kept as a verification step so a regression is
caught, not as a task list.

### Decision 12: Asset-edge warning coverage - only the unknown-id edges and the root-`Company` non-warning are gaps

The acceptance bullet reads as though command-level dangling-edge coverage is absent. It is
not, and the same audit Decision 10 applies to the retired kinds applies here. Checked each
edge the rule names:

- A declared `Vendor` with no `owner` (a missing required edge) - already covered.
  `tests/Freeboard.CLI.Tests/GitOpsCommandTests.cs:170-179` is the `WarningsOnlyConfig`
  fixture, a lone ownerless declared Vendor, and it drives
  `ValidatePrintsWarningsOnValidPathAndExitsZero` (line 253) and
  `ApplyDryRunPrintsWarningsOnValidPathAndExitsZero` (line 271). Both assert `warning:` on
  stderr and exit `0`.
- A dangling `Scope.subject` - already covered on both DB-less commands, by
  `ValidatePrintsScopeSubjectWarningOnValidPathAndExitsZero` (line 213) and
  `ApplyDryRunPrintsScopeSubjectWarningOnValidPathAndExitsZero` (line 232), which assert
  the warning names the unresolved id and that the planned state still prints.
- A `parent` or `owner` naming an id no document defines - **no coverage anywhere**, on any
  of the three commands. The ownerless-vendor case proves the missing-edge branch, not the
  unknown-id branch; they are separate validator predicates producing separate diagnostics.
- A `Machine` with no `parent` - no coverage.
- The negative half of the rule, that a root `Company` with no `parent` draws no warning -
  **no coverage anywhere**. Nothing pins that the missing-edge warning stays off for a
  legitimate root, so a validator change that warned on every parentless asset would pass
  the suite while flooding an operator's stderr.

The `sync` path splits the same way. Its missing-anchor half is covered
(`SyncAndMigrateCommandTests.SyncPrintsNonScopeCoreWarning`, line 175, and
`SyncMySqlIntegrationTests.SyncPrintsNonBlockingWarningToStderrAndExitsZero`, line 225);
its unknown-id half is not. Since the requirement binds `sync` alongside `validate` and
`apply --dry-run`, that half gets a task too, on the DB-less fake-importer path in
`SyncAndMigrateCommandTests` rather than a MySQL-gated test - the diagnostic is printed by
the command from the Core result, so a database adds nothing.

So the genuine gap is narrow, and the tasks are scoped to it: the two unknown-id edges on
the DB-less commands, the same unknown-id edge on `sync`, the parentless `Machine`, and the
root-`Company` non-warning. It is closed by extending `WarningsOnlyConfig` and the two
tests that already consume it, not by a third near-duplicate warnings fixture, which would
leave three "warnings" configs and no canonical one (`code-as-liability.md`). Keeping the
existing ownerless `vendor-a` document in the fixture keeps those two consuming tests
asserting what they assert today.

_Alternative considered:_ a command-surface clause and test for the parent-cycle warning
too. Rejected on coverage, not on redundancy: the ratified text that covers the cycle
(`gitops-config-format` spec lines 139-141 and 520-528) covers the dangling-edge and
missing-edge clauses in the same sentences, and `asset-model` lines 437-449 ratifies those
two again (it carries no cycle clause at all), so "already ratified" would rule out the
kept clauses as well. The difference
is that the kept clauses get command-level tasks in this change and a cycle clause would
get none, so SHALLing it would archive an unproven requirement.

## Risks / Trade-offs

- **The fixture growth is silent: no existing assertion breaks when it happens** -> The
  inverse of the risk a reader expects, and the reason task 1.5 exists. Decision 5 carries
  the evidence; without 1.5 the fixture change is inert.

- **The new CI lint step depends on the examples staying valid, coupling example
  edits to a green build** -> That is the point of the step. The failure mode is
  loud and the fix is local; a broken shipped example is a worse failure that no
  gate currently catches.

- **The collision requirement claims only the command's exit code and message, not the
  store's state** -> Deliberate, and it means this change's `gitops-cli` delta says nothing
  about rows. The store-level no-mutation guarantee is ratified in `asset-model` and proven
  by `AssetUnificationIntegrationTests`; restating it here would put a clause in the delta
  that no task in this change discharges. The trade-off is that a reader of the `gitops-cli`
  spec alone sees the exit codes and must follow the citation for the guarantee behind
  them.

- **The `sync` counts printed come from the config, not from the database, so the
  new integration count says what was authored rather than what changed** -> This
  matches every other count on the line and is pre-existing behaviour; changing it
  needs `ImportResult` to carry per-kind created/updated/deleted tallies, which is
  out of scope. The hard-remove warning in `docs/gitops.md` remains the operator's
  notice that a narrowing config deletes rows.

- **A declared/discovered id collision maps to exit `3`, and an operator may expect
  `1`** -> The collision is detected inside the import transaction against database
  state, not by config validation, so `3` (operational) is the correct code under the
  established convention. The new requirement pins it so the mapping cannot drift,
  and the message already names the colliding id and says nothing was written.

- **A future eighth kind will again need three edits in one file, with nothing
  forcing them** -> Accepted, and partly mitigated: the CI example step and the
  seven-kind fixture both fail when a kind is added without wiring. A registry
  abstraction to make it one edit is not worth its cost at seven kinds.

## Migration Plan

None. No schema change, no migration, no config-format change, no persisted data
touched. The change is additive output plus tests, examples, and one CI step.
Rollback is a revert of the commits; no state is left behind.

## Settled questions

- Should the `Integrations` planned-state line print the resolved token health? No -
  `apply --dry-run` makes no network call and reads no configuration provider, so it
  cannot know. Token health is a read-surface concern and `connections list` already
  serves it. Recorded because it is the obvious question to ask of the new section.
- Does #67's read-parity bullet stay in scope? No. It is delegated to issue #65, and #67
  closes on its first three acceptance bullets with #65 carrying the fourth. Decision 2
  records the reasoning and the concrete residual read list #65 inherits. This change adds
  no read commands.
- The authored-token conflict is closed on both sides: Decision 7 fixes the code question
  on the ratified specs, and issue #67 has been edited to say `Integration` and to record
  the delegation to #65, so no stale issue text remains for a future reader to trip over.

## Open Questions

- The integration count missing from `/compliance/status` is an unclaimed residual with no
  ticket filed, owned by neither #67 nor #65 as they are scoped. It needs one if it is worth
  closing at all. Decision 8 records why this change does not close it.
