## Context

Freeboard's GitOps config is declarative YAML (Kubernetes-style `apiVersion` +
`kind`) loaded into a typed model in `Freeboard.Core`, validated to a diagnostic
list, and synced into per-kind MySQL tables by an importer in `Freeboard.Persistence`.
Existing kinds are `Standard`, `Requirement`, `Control`, `Organisation`, `Scope`,
`RequirementScope`, `Vendor`, and `VendorScope`. The pipeline is pure and testable:
the loader and validator never throw and never write output; the importer replaces
the whole persisted set in one transaction.

This change adds one kind and extends one existing kind. It mirrors the just-merged
`add-vendor-gitops-kinds` change (archived at
`openspec/changes/archive/2026-07-07-add-vendor-gitops-kinds`) at every layer, so
the shapes below match the real current code, not a fresh design.

Templates studied in the current tree:

- `VendorScope` (`Freeboard.Core/GitOps/ConfigModel.cs`, `ConfigLoader.cs`,
  `ConfigValidator.cs`): a record that binds to one of two typed foreign keys with
  reference resolution, a closed-enum field parsed case-sensitively, a conditional
  required field (`justification` when `Out`), unique-id and unique-pair checks.
  `EvidenceCollector` mirrors this machinery.
- `Control` already exists with `id`, `title`, `maps_to`; it is validated to have a
  non-empty `maps_to` whose ids resolve. This change adds one optional field to it.
- Persistence: `Migrations/011_vendors.sql` (DDL template; highest existing migration
  is `011`), `GitOps/ImportPlan.cs`, `GitOps/MySqlGitOpsImporter.cs`,
  `ComplianceReadModels.cs`, `IComplianceStore.cs`, `MySqlComplianceStore.cs`.
- Read surfaces: `Compliance/ComplianceEndpoints.cs`,
  `Pages/Compliance/Vendors.cshtml(.cs)`, `Pages/Shared/_Layout.cshtml` (nav),
  `Freeboard.CLI/{VendorCommands.cs,ApiCommandRunner.cs,IFreeboardApiClient.cs,HttpFreeboardApiClient.cs,GitOpsCommands.cs,Program.cs}`.

## Goals / Non-Goals

**Goals:**

- `EvidenceCollector` and the `Control.evaluation` field parse, validate, and sync
  through the existing pipeline with no new machinery beyond one migration and the
  per-kind wiring.
- Referential integrity: `collector -> control` and `collector -> vendor` resolve;
  an unknown `type`, `frequency`, or `evaluation` value is rejected with a clear
  diagnostic; `threshold` is range-checked.
- `frequency`, `threshold`, and `config` are persisted and exposed on both read
  surfaces (web SSR + CLI) for later scoring/staleness use.
- A control-centric read register on web SSR and CLI, in one PR, showing each
  control's evaluation rule and attached collectors.

**Non-Goals:**

- Runtime Evidence store (#49), evidence ingest (#51), scoring/staleness, and any
  runtime evaluator. Config only.
- Deep per-type validation of the `config` map (no runtime consumer yet).
- App-managed collector CRUD (GitOps-only authoring in V1).

## Decisions

### D1: EvidenceCollector is a static GitOps kind; the collector -> control edge lives on the collector

Match every other kind. `EvidenceCollector` is a YAML kind parsed in `Freeboard.Core`,
validated to diagnostics, synced by the importer; no app write path in V1.

A collector names its `control`; the control does not list its collectors. A
control's "attached collectors" are derived by reverse lookup (collectors whose
`control` equals the control id) and surfaced only on the read side. This keeps the
edge single-sided (like `VendorScope.control`, `control_requirements`), avoids a
duplicated list that could drift, and means `Control` gains only the `evaluation`
rule field.

Rejected alternative: a `collectors:` list on `Control`. It duplicates the edge,
invites the two sides disagreeing, and needs its own reference-resolution and
uniqueness rules. The single-sided FK reuses the existing pattern.

Files: `ConfigModel.cs` (add `KindEvidenceCollector`; add the `EvidenceCollector`
record; add `Evaluation` to `Control`; add `List<EvidenceCollector> EvidenceCollectors`
to `GitOpsConfig`), `ConfigLoader.cs` (one `SchemaKeys` entry; add `evaluation` to
the `Control` keys; the `apiVersion` attribute override; one `switch` case that
normalizes an explicit-null `config:` to an empty map - `collector with { Config =
collector.Config ?? [] }`, mirroring the existing `MapsTo ?? []` control case, because
a present-but-null YAML key overwrites the record default with null; extend
the unknown-kind message list), `ConfigValidator.cs` (add `ValidateEvidenceCollectors`
called after controls, vendors, and requirements; add the `evaluation` enum check to
`ValidateControls`; add the evaluation-required cross-check).

### D2: EvidenceCollector field schema

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: EvidenceCollector
id: collector-endpoint-mfa
title: Endpoint MFA via Crowdstrike
control: ctrl-mfa
vendor: crowdstrike            # optional
type: integration              # integration | script | manual-attestation | training-attestation | agent
frequency: daily               # continuous | daily | weekly | monthly | quarterly | annual
threshold: 100                 # optional, integer percent 0..100
config:                        # optional, free-form type-specific map
  endpoint: policies.mfa
```

- `id`, `title`: identity + display, like every kind.
- `control`: required; references a `Control` id. This is the attach point.
- `vendor`: optional; references a `Vendor` id when present. `integration` and
  `agent` collectors typically name a vendor; `script`, `manual-attestation`, and
  `training-attestation` often have none, so `vendor` is not required.
- `type`: required; exactly one of the five task-fixed tokens. Case-sensitive parse
  (identity is exact-byte, consistent with the existing disposition/kind parsers). An
  unknown value yields a clear diagnostic (acceptance).
- `frequency`: required; a closed cadence enum for staleness. Case-sensitive.
- `threshold`: optional; an integer percent in `[0, 100]` = the share of the
  collector's checks that must pass for the collector to be satisfied. When absent,
  downstream defaults to 100 (all checks must pass). Only parsed/validated/exposed
  here; nothing consumes it in this change. In the Core model `Threshold` is carried as
  raw authored text (`string Threshold = string.Empty`, matching the raw-string enum
  fields the validator already range-checks), NOT `int?`: typing it `int?` would make a
  malformed value (e.g. `threshold: high`) a YamlDotNet binding error instead of the
  intended clean validation diagnostic. The validator parses and range-checks the text
  and emits a diagnostic on a non-integer or out-of-`[0,100]` value; ImportPlan converts
  it to `int?` only after validation passes (blank stays null).
- `config`: optional; a free-form `string -> string` map of type-specific settings
  (for example an integration endpoint or a script id). Stored and echoed verbatim.
  Holds no secrets (D6). A present-but-null `config:` (the key with no value) binds
  `Config` to null and is unvalidated (D6), so the loader normalizes it to an empty map
  (see D1) before it reaches ImportPlan serialization, page render, or CLI grouping;
  without that a null `Config` would NRE downstream.

The `type` and `frequency` tokens are lowercase-hyphenated, deliberately unlike the
existing PascalCase `In`/`Out` and `Company`/`Department` enums: the task fixes the
`type` tokens as lowercase-hyphenated, and `frequency`/`evaluation` follow the new
kind's own convention for internal consistency. Each parses case-sensitively against
its exact token set.

### D3: Control gains an optional `evaluation` rule, required when the control has collectors

`Control` gains `evaluation`, a closed enum describing how the control's attached
collectors' checks roll up into a status:

- `all`: satisfied only if every attached collector is satisfied (weakest link / AND).
- `any`: satisfied if at least one attached collector is satisfied (OR).
- `manual`: a human sets the control status; collectors are advisory.

`evaluation` is optional in isolation (a control with no collectors needs no rule, so
existing configs keep validating), but is REQUIRED when the control has at least one
attached `EvidenceCollector`. This is the one net-new conditional rule of this change,
directly parallel to VendorScope's `justification`-required-when-`Out` (archived D3).
When present it must be one of the three tokens; case-sensitive parse. When absent on
a control that has collectors, validation fails with a diagnostic naming the control.
When absent on a control that has no collectors, downstream defaults to `all`.

Phase order: `ValidateControls` runs before collectors, so it checks only the enum
value (when present). `ValidateEvidenceCollectors` runs after and knows which control
ids are referenced, so it emits the "control X has collectors but no evaluation rule"
diagnostic. This keeps each id set available before it is consumed, matching the
existing fixed phase order.

The missing-evaluation check iterates the real `config.Controls` and tests each
control's membership in the set of control ids that have at least one collector - it is
NOT driven off the collectors' control-refs directly. A collector that names a
non-existent control therefore yields only the unknown-control diagnostic (D4); it
cannot also raise a spurious "control X missing evaluation" for an id no document
defines.

Rejected alternatives: (a) `evaluation` unconditionally required - breaks every
existing collector-free control and forces noise on controls that are not yet
evidenced. (b) a nested `evaluation: { rule, threshold }` object - the per-collector
`threshold` already carries the numeric bar; the control-level rule only needs to say
how collectors combine, so a single enum is the smaller model. (c) a free-form
expression DSL - speculative; no runtime consumes it yet.

### D4: Referential integrity reuses the existing id-set machinery

`ValidateEvidenceCollectors` receives the control id set and the vendor id set built by
the earlier phases. It does not need the requirement id set: no rule uses it, because
the transitive `collector -> control -> requirement` path is already guaranteed by the
existing non-empty `maps_to` check (below), which needs no parameter. It checks:

- `control` resolves against control ids (required).
- `vendor`, when present, resolves against vendor ids.
- `type` in the closed set; `frequency` in the closed set; `threshold` in `[0,100]`
  when present.
- `id` unique within the kind.

`collector -> control -> requirement` needs no new rule: `ValidateControls` already
rejects a control with an empty `maps_to` and rejects `maps_to` ids that do not
resolve, so a control that a collector resolves to always maps to at least one real
requirement. The design states this transitive guarantee rather than re-checking it.

No new pair-uniqueness rule: a control may legitimately have several collectors (for
example two integrations from different vendors), so the only uniqueness is on `id`.

### D5: Persistence - one migration adds the table and the `controls.evaluation` column

`Migrations/012_evidence_collectors.sql` (forward-only; auto-discovered as an
embedded resource, no code registration):

- `ALTER TABLE controls ADD COLUMN evaluation VARCHAR(16) NULL` after `title`. This
  is the first ALTER of an existing table in this feature line; it is a nullable
  column add, so it rewrites no data and old rows read `evaluation = NULL`. Justified:
  the evaluation rule is a property of the control, so it belongs on the control row,
  not a side table.
- `CREATE TABLE evidence_collectors` (utf8mb4_bin ids, `DATETIME(6)` timestamps,
  InnoDB, `CREATE TABLE IF NOT EXISTS`, matching `011_vendors.sql`):
  `id` PK, `api_version`, `title`, `control_id` NOT NULL FK -> `controls` RESTRICT,
  `vendor_id` NULL FK -> `vendors` RESTRICT, `type` VARCHAR(32), `frequency`
  VARCHAR(16), `threshold` INT NULL, `config` JSON NULL, `created_at`, `updated_at`,
  with secondary indexes on `control_id` and `vendor_id`. No secondary unique key
  (uniqueness is on `id` only, per D4), so the importer can upsert by id and prune
  absent rows - no whole-set replace is needed (unlike `vendor_scopes`, which has
  pair unique keys). `config` uses MySQL's native JSON type (8.4 baseline), which
  validates well-formedness at write; the store round-trips it as a JSON object.

Importer (`MySqlGitOpsImporter`):

- Controls can no longer use the generic `UpsertAsync(DomainRow)` because they now
  carry `evaluation`. Add a `ControlRowPlan(Id, ApiVersion, Title, Evaluation?)` and a
  dedicated `UpsertControlsAsync` that writes the `evaluation` column (NULL when
  blank). `ImportPlan.Controls` becomes `IReadOnlyList<ControlRowPlan>`;
  `ControlRequirements` is still built from `config.Controls` directly, so the join
  build is unchanged.
- Upsert `evidence_collectors` by id in the FK-safe upsert phase, after controls and
  vendors are upserted (its FKs point at both). Its `config` value is serialized to a
  JSON string in `ImportPlan` via `System.Text.Json` (null when the map is empty).
- Prune absent `evidence_collectors` in the delete phase BEFORE pruning absent
  `vendors`, `controls`, and `requirements`: the collector FKs are RESTRICT, so a
  still-referenced control or vendor cannot be deleted while a stale collector points
  at it. Placement mirrors how `vendor_scopes` are cleared before their targets.

### D6: The type-specific `config` holds no secret material

The existing schema rule "Config carries no secret material" now covers
`EvidenceCollector` and its `config` map. `config` is git-tracked YAML, so it must not
inline a token, key, or password for an integration. A collector that needs a
credential references it by a named credential resolved out-of-band (the mechanism is
downstream, in the collector runtime). This change adds no secret field and echoes
`config` verbatim; the rule is documented and asserted, not enforced by content
scanning.

### D7: Read surfaces mirror the compliance stack; parity via SSR page + CLI-over-API

- Read store: `ControlRow` gains `Evaluation` (string?, null when unset); the
  `GetControlsAsync` query selects the new column. Add `EvidenceCollectorRow(Id,
  Title, Control, Vendor?, Type, Frequency, Threshold?, Config)` where `Config` is an
  `IReadOnlyDictionary<string, string>` deserialized from the stored JSON (empty when
  null). Add `GetEvidenceCollectorsAsync` to `IComplianceStore`/`MySqlComplianceStore`.
  Extend `ComplianceCounts` with `EvidenceCollectors`.
- API (`ComplianceEndpoints`): add `evaluation` to the `/controls` payload (null when
  unset). Add `GET /api/v1/freeboard/evidence-collectors` (GET-only,
  `RequireAuthorization`, 503 on unreachable store) with payload keys `id`, `title`,
  `control`, `vendor`, `type`, `frequency`, `threshold`, `config`. Add
  `evidenceCollectors` (camelCase, matching `requirementScopes`/`vendorScopes`) to the
  `/compliance/status` `persisted` object in BOTH the reachable and unreachable
  branches (integer, then null).
- Not org-narrowed. Like vendors, collectors are org-independent reference data (no
  `organisation` dimension), so the endpoint and page intentionally skip the
  `IOrgAccess` narrowing that `/organisations`, `/scopes`, and `/requirement-scopes`
  apply: any authenticated user - including a zero-grant caller under strict enforce -
  reads every collector. A zero-grant test pins this deliberate non-narrowing.
- Web SSR (`evidence-collector-register`): new page
  `Pages/Compliance/EvidenceCollectors.cshtml(.cs)` at `/compliance/evidence-collectors`,
  injecting `IComplianceStore` in-process like the SoA and Vendors pages. Control-centric:
  each control shows its `evaluation` rule and, under it, its attached collectors
  (type, vendor, frequency, threshold, and any config). GET-only, any authenticated
  user, unaffected by read-only mode, in-page notice on an unreachable store. Add a nav
  link in `_Layout.cshtml`. Add the route to the parametrized `Pages` cases in
  `Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`.
- CLI (`evidence-collector-register`): new `CollectorCommands` group (`collector list`),
  modelled on `VendorCommands`, calling the shared `ApiCommandRunner.Run`/`Translate`.
  It reads `/controls` (now with `evaluation`) and `/evidence-collectors`, then prints
  each control with its evaluation rule and, under it, its attached collectors (type,
  vendor, frequency, threshold, and each `config` key/value), matching the fields the
  web SSR page renders. The `config` map already arrives on the API wire record, so no
  new field is needed - only the print path. Add
  `ListControlsAsync` and `ListEvidenceCollectorsAsync` to `IFreeboardApiClient` (with
  the corresponding wire records) and implement them in `HttpFreeboardApiClient`.
  Register the `collector` group in `Program.cs`. Exit codes follow the CLI convention
  (0 ok, 1 validation, 3 operational). The CLI reads over HTTP, never the database.
- Sync summary parity: add the evidence-collector count to `GitOpsCommands`
  `PrintSummary`, `PrintPlannedState`, and the `Sync` success line.

JSON field naming: every collector payload key is single-word, so snake_case and
camelCase are byte-identical; the only multi-word key is the status count
`evidenceCollectors`, which uses camelCase to match the existing count keys. This is
the same convention the vendor change fixed (archived D7).

## Risks / Trade-offs

- [First ALTER of an existing table (`controls`)] -> Mitigation: a single nullable
  column add is metadata-only in MySQL 8.4 (instant add), rewrites no rows, and leaves
  old configs syncing unchanged (`evaluation` reads NULL). Rollback is dropping the
  column and the new table.
- [`config` is free-form and unvalidated per type] -> Mitigation: nothing consumes
  config values in this change, so per-type schema validation is speculative and
  deferred to the collector runtime (#51). V1 stores and echoes the map and enforces
  only the no-secret rule (D6). The JSON column validates well-formedness.
- [Nested config values would fail to bind to `string -> string`] -> Mitigation:
  V1 config values are scalars; a nested value throws in YamlDotNet and surfaces as a
  loader diagnostic (never an unhandled crash), which is the correct "not supported
  yet" signal. Nested config is an additive future change.
- [Evaluation-required cross-check couples control integrity into the collector phase]
  -> Mitigation: the check runs where both id sets exist (the same fixed-phase-order
  pattern the pipeline already uses for maps_to and scopes); it is one branch, not new
  machinery.
- [Read-surface size: store, API, page, CLI is a lot of code] -> Mitigation: each
  layer is a thin mirror of the vendor register; the parity rule is an explicit
  acceptance criterion; no new abstraction is introduced (`ApiCommandRunner` already
  exists).
- [Collector RESTRICT FKs could wedge a control/vendor delete on resync] -> Mitigation:
  absent collectors are pruned before absent controls and vendors, mirroring the
  vendor-scope ordering; integration tests assert the FK-safe resync.

## Migration Plan

- Add `012_evidence_collectors.sql`. Additive and forward-only: create
  `evidence_collectors`, add the nullable `controls.evaluation` column. Rollback is
  dropping the table and the column (no data migration).
- Applied by the existing operator path (`freeboard system migrate` or
  `gitops sync --migrate`). The web app does not migrate at startup.
- Deploy order: apply the migration, then `gitops sync` a config that includes the new
  kind. Old configs without collectors continue to sync unchanged (empty collector set,
  `evaluation` NULL on every control).

## Resolved questions (Q1-Q5)

These were open in the planner's draft; both this plan and the independent Codex
plan converged on the same value for each, so they are settled here, not left for a
reviewer. The chosen value is the one already used in D2/D3 above.

- Q1 - `frequency` shape: RESOLVED to a closed cadence enum `continuous`, `daily`,
  `weekly`, `monthly`, `quarterly`, `annual`. Not an ISO-8601 duration string. The
  enum is trivial to validate against a fixed token set (reusing the disposition-parser
  pattern), reads cleanly on both surfaces, and maps to a concrete staleness window
  downstream. An ISO-8601 duration is more expressive but adds a parser and a range of
  degenerate values with no V1 consumer; it can be added later without breaking the
  enum tokens.
- Q2 - `threshold` unit: RESOLVED to an optional integer percent in `[0, 100]` (the
  share of the collector's checks that must pass). Not an absolute check count. Percent
  is portable across collectors with different check counts and needs only a range
  check; an absolute count would be meaningless without knowing each collector's check
  total, which is a runtime property this change does not have.
- Q3 - `evaluation` set: RESOLVED to exactly `all`, `any`, `manual`. No numeric
  N-of-M rule in V1. The three tokens cover AND, OR, and human-authoritative; an
  N-of-M rule is additive (a later token plus a count field) and has no runtime
  consumer yet.
- Q4 - CLI group name: RESOLVED to `collector` (the planner's choice), short and
  consistent with the existing `vendor` and `user` groups. `evidence-collector` would
  match the kind name exactly but is longer than every existing group; the shorter name
  wins for the same reason `vendor` did. Noted rather than re-litigated.
- Q5 - `vendor` requiredness: RESOLVED to globally optional, validated when present.
  Not per-type required. Forcing a vendor on `script`, `manual-attestation`,
  `training-attestation`, and `agent` collectors would pollute the vendor register with
  placeholder vendors; per-type conditional requiredness adds branching for no real
  gain. `integration` and `agent` collectors will usually name a vendor by convention,
  but nothing enforces it. When a `vendor` is given it must resolve to a real `Vendor`.

## Plan provenance (A/B synthesis)

This change was synthesized from two independently produced plans: Plan A (this
change's original proposal/design/tasks) and Plan B (an independent Codex plan). They
were highly convergent. Recorded here so a reviewer can see where each idea originated
and how the few divergences were closed.

- From both (identical): static GitOps/read-model change only; migration
  `012_evidence_collectors.sql` (highest existing is `011_vendors.sql`); the
  collector -> control edge lives on the collector (no `collectors:` list on `Control`);
  `Control.evaluation` as `all`/`any`/`manual`; referential integrity via the Core
  validator backed by DB RESTRICT FKs (`control_id` -> `controls`, nullable `vendor_id`
  -> `vendors`); transitive `collector -> control -> requirement` guaranteed by the
  existing non-empty `maps_to` check with no direct requirement FK on collectors;
  `config` as a scalar `string -> string` map stored as JSON, no per-type validation in
  V1; read parity on web SSR + CLI-over-API; the Q1-Q5 values above.
- From Plan A specifically: the exact per-file wiring against the current tree
  (D1-D7 file lists), the parallel to VendorScope's conditional-required
  `justification` for the evaluation-required rule, the org-non-narrowing decision and
  its zero-grant test, the JSON snake_case/camelCase byte-identity note, and the
  archived-vendor-change template mapping.
- From Plan B specifically: the explicit framing of the roll-up status semantics as
  documentation-only (folded in as D8 below), and three sharpened test asks now
  reflected in tasks.md - malformed/nested `config` value diagnostics (loader returns a
  diagnostic, never an uncaught exception), an FK-safe resync prune-order integration
  test, and updating every fake store and every `ComplianceCounts`/`ControlRow`
  constructor call site broken by the new members.
- Divergences and resolution: there were no substantive conflicts. Plan A left Q1-Q5
  open; Plan B had already committed to the same values, so they are resolved above.
  Plan B named the risks H-1/H-2/M-1/M-2/M-3; each maps onto an existing decision
  (H-1 -> Non-goals/scope; H-2 -> D5 prune order; M-1 -> Q5; M-2 -> D6 plus the new
  loader-diagnostic test; M-3 -> the new call-site-update task). No decision was
  reversed to reconcile the two.

### D8: Roll-up status semantics are documented, not executed

`Control.evaluation` and the per-collector `threshold` define how collected checks
would roll up into a control status. This change writes those semantics down but runs
none of them. No code in this change computes a collector's pass state, a control's
status, or a staleness verdict. The intended semantics, recorded for the downstream
scoring/ingest work (#49/#51) and for reviewers, are:

- A collector "passes" when its future check pass-ratio is `>= threshold`. When
  `threshold` is absent the downstream default is 100 (every check must pass).
- `all`: the control is satisfied only if every attached collector passes; any failing
  collector fails the control; while collectors are unknown or stale the control stays
  unknown unless a failure exists.
- `any`: the control is satisfied if at least one attached collector passes; it fails
  only when all attached collectors have failed; unknown with no passing collector
  stays unknown.
- `manual`: attached collectors are advisory; a human-set status is authoritative.

These bullets are specification for later issues, not behaviour added here. Nothing in
this change reads them at runtime; they exist so the persisted `evaluation`/`threshold`
values have a single agreed meaning when a scoring engine is built.
