## Context

The GitOps config model has ten kinds. An `EvidenceCollector` attaches one data
source to one `Control`; its `config` map is free-form type-specific settings and
holds no secret material. A real integration such as FleetDM does not fit that
shape: it is one base URL and one API token that both drive discovery (enumerate
machines) and back many per-control collectors. Today there is no place to author
that connection and no way to resolve its token.

This change mirrors the established GitOps-kind pattern exactly, so the new kind
carries no new machinery:

- Domain records and the loader/validator live in `Freeboard.Core`
  (`GitOps/ConfigModel.cs`, `ConfigLoader.cs`, `ConfigValidator.cs`). Closed token
  sets are `HashSet<string>` compared `StringComparer.Ordinal`
  (`EvaluationTokens`, `CollectorTypeTokens`), or a shared static like
  `EvidenceCollectorFrequency.Tokens`. Absolute-URL validation is the private
  `ConfigValidator.IsAbsoluteHttpUri` used for `source_url`/`citation_url`.
- Persistence flattens the validated model to id-keyed row plans
  (`GitOps/ImportPlan.cs`), upserts by id in one transaction with
  `INSERT ... ON DUPLICATE KEY UPDATE` and prunes absent rows in FK-safe order
  (`GitOps/MySqlGitOpsImporter.cs`), and reads back through `IComplianceStore` /
  `MySqlComplianceStore` into read-model records (`ComplianceReadModels.cs`).
  GitOps-kind tables are global (no `organisation_id`); child lists are stored as
  a native `JSON` column on the parent row, not a child table
  (`evidence_collectors.config`, `attestation_templates.fields`/`quiz`).
- Migrations are embedded `Migrations/NNN_slug.sql` resources applied by
  `freeboard system migrate`; the highest ordinal is 017, so this change adds
  018.
- The web app reads through `IComplianceStore` in-process for Razor Pages
  (`Pages/Compliance/*`) and exposes read-only `GET /api/v1/freeboard/<kind>`
  minimal APIs (`Compliance/ComplianceEndpoints.cs`). The CLI reads those
  endpoints over HTTP only (`HttpFreeboardApiClient`, `ConsoleAppFramework`
  command groups), never the database.

Constraints: MIT only, in `Freeboard.Core`, `Freeboard.Persistence`, `Freeboard`,
and `Freeboard.CLI`; nothing in `Freeboard.Enterprise`. `Freeboard.CLI` stays
community and cross-platform and reaches data through the HTTP API. No new package
dependency.

## Goals / Non-Goals

**Goals:**

- Author an `IntegrationConnection` in git (provider, base URL, discovery
  cadence, optional vendor) and round-trip it through the DB to a read surface.
- Let an `EvidenceCollector` of `type: integration` name a connection and declare
  its `checks` list (`source_key` -> check name + `Hard`/`Soft`).
- Resolve the API token out-of-band from `IConfiguration`, never in git, never in
  a collector `config`, never persisted, never logged.
- Warn once at startup for a referenced connection whose token is unresolvable
  (naming the id, never the value), boot anyway, and let that collector's runs
  fail as an `Error` run at collection time.
- List connections with a `tokenResolvable` health flag on both the web and the
  CLI.

**Non-Goals:**

- No integration runner/adapter, discovery, provider HTTP call, or collection
  execution engine. The `Error`-run outcome is a contract the future runner
  honours.
- No secret-store retrieval; `IConfiguration` only.
- No per-provider `config`-block schema validation (lands with the
  provider-adapter registry).
- No write/edit surface for connections.
- No new provider beyond `fleet`; no speculative connection fields.

## Decisions

### Decision: capability decomposition

The change spans four projects but four capability specs:

- `gitops-config-format` (MODIFIED + ADDED): the `IntegrationConnection` kind
  (authorship + validation) and the two new `EvidenceCollector` fields, plus the
  enumerated-kind touch-ups (schema kind list, loader kind-set, no-secret-material
  list, validation umbrella, docs). The config contract is this spec's job.
- `gitops-cli` (MODIFIED): the `gitops validate`/`gitops sync` referential-
  integrity and round-trip requirements enumerate the kind set, so they gain the
  new kind and the `EvidenceCollector.connection` dangling-reference check.
- `compliance-persistence` (MODIFIED): its `EvidenceCollector persistence`
  requirement enumerates the `evidence_collectors` columns as a closed list. This
  change adds `connection_id` and `checks` columns to that table and extends the
  importer's foreign-key-safe order to prune integration-connections between
  collectors and vendors, so the enumeration and the ordering are updated there to
  stay the single source of truth for that table. The new `integration_connections`
  table itself is owned by the new `integration-connection` capability, not here.
- `compliance-web-read` (MODIFIED): its `Read path tolerates an unavailable store`
  requirement forbids the web app from auto-connecting to MySQL at startup. The startup
  token-warning read is a guarded, non-fatal boot-time read, so that requirement is
  narrowly amended to permit a single best-effort startup read whose sole effect is
  logging warnings and which silently skips on any store outage - keeping the invariant's
  spirit (boot never blocked or failed by the store).
- `integration-connection` (NEW): everything that is not the config contract - the
  `provider` domain token and its provider-level `asset_source.source` alignment, the
  `integration_connection` persistence table and its persisted-subset read
  model, out-of-band token resolution (never persisted/logged, startup warning,
  scheduler `error` status at collection time), the `tokenResolvable` health flag composed at
  read time, the read-only web list view, and the `connections list` CLI command.

Alternative considered: a separate `integration-connection-register` capability
for the read surfaces, mirroring `evidence-collector-register` / `vendor-register`.
Rejected for this increment: the connection read surface is small and is
inseparable from the out-of-band token mechanism it displays (`tokenResolvable`),
so splitting it adds a capability without reducing coupling. The one new
capability holds a register-style requirement pair (web + CLI) internally.

### Decision: `provider` is a closed token set in Core, aligned to `asset_source.source`

`provider` is required and drawn from a closed, case-sensitive token set whose only
V1 value is `fleet`. It selects the runner/adapter and is the axis that aligns a
connection with the machines a source reports: a machine's `asset_source.source`
token equals the `provider` token. The alignment is provider-level, not
connection-level - `asset_source` keys a machine by `(organisation_id, source,
external_id)` with no connection id, so a machine aligns with a provider, not with a
specific connection instance. Disambiguating among several connections that share a
provider is future work. The token set lives in `Freeboard.Core`
as a small static (`IntegrationProvider.Tokens`, a `HashSet<string>` compared
`StringComparer.Ordinal`), mirroring `EvidenceCollectorFrequency.Tokens`, so the
future runner references the same set. `provider` is not unique - one provider
backs many connections (1:many); identity is `id`.

Note the naming tension: `asset_source.source` is a free
`VARCHAR(64)` token with no closed set, and the asset-model precedent used the
example value `fleetdm`, while this issue fixes the connection `provider` token as
`fleet`. The alignment contract is "the machine source token equals the connection
provider token", so whichever literal the FleetDM collector writes into
`asset_source.source` must equal the `provider` token authored here. This change
fixes `fleet` as the provider token; the collector change must use `fleet` as the
source token to honour the alignment. See Open Questions.

Alternative considered: a free-form `provider` string (like `asset_source.source`).
Rejected: `provider` selects a runner, so an unknown value is an authoring error
that must fail validation, not a silently unrouteable connection.

### Decision: absolute-URL validation reuses the existing check

`base_url` is required and validated with the same rule as `source_url` and
`citation_url`: `ConfigValidator.IsAbsoluteHttpUri` (a `Uri.TryCreate` absolute
parse whose scheme is `http` or `https`). No new validator.

### Decision: `discovery_cadence` reuses the frequency token set

`discovery_cadence` is required and validated against
`EvidenceCollectorFrequency.Tokens` (`continuous`/`daily`/`weekly`/`monthly`/
`quarterly`/`annual`) - the same closed set a collector's `frequency` uses. It is
the cadence at which discovery enumerates the provider's machines. Reusing the
token set avoids a second cadence vocabulary; the staleness/interval math in
`EvidenceCollectorFrequency` is available to the future discovery runner unchanged.

### Decision: `checks` is a typed JSON list, validated in Core

`EvidenceCollector.checks` is an ordered list of `Check { source_key, name,
severity }`:

- `source_key` is the provider-native id (a Fleet policy id). It is the join key
  between a provider result and a Freeboard check; a policy whose id is not in the
  authored list is simply not a tracked check.
- `name` is the Freeboard check name (the same identifier `evidence_checks.name`
  carries).
- `severity` is `Hard` or `Soft`, the exact string tokens `evidence_checks.severity`
  stores and `MySqlEvidenceStore` compares (`Hard` fails the requirement, `Soft`
  warns). Validated against a closed `{ Hard, Soft }` token set in `ConfigValidator`
  (a new `CheckSeverityTokens` set beside `EvaluationTokens`), compared
  `StringComparer.Ordinal`. There is no Core severity enum to reuse - evidence
  checks carry severity as a string - so a closed token set is the consistent
  model.

Storage mirrors `config`/`fields`/`quiz`: a native `JSON NULL` column on
`evidence_collectors`, serialized by `ImportPlan.SerializeList` and null when
empty. Not a child table. The `compliance-persistence` spec fixes the pattern: a
collector's `config` and a template's `fields`/`quiz` are all nullable `JSON` columns
on the parent row, and a single-valued reference (`vendor_id`, `control_id`) is a
nullable foreign-key column on the parent - never a child or link table. The
connection reference follows the same rule: a `connection_id` foreign-key column on
`evidence_collectors`, not a link table, because it is single-valued. Item-level
uniqueness that a child table's `UNIQUE (collector_id, source_key)` / `(collector_id,
name)` would enforce is already enforced in Core validation (unique `name` and unique
`source_key` within the collector), so a child table would add a join and a
whole-set-replace delete path for a guarantee already made upstream and a read no
surface needs.

Validation (only in the collector phase, where the type is known):

- `checks` required non-empty when `type: integration`.
- Each item: `source_key`, `name`, `severity` required non-blank; `severity` in
  `{ Hard, Soft }`.
- `name` unique within the collector; `source_key` unique within the collector
  (mirrors the duplicate-ref check style, one diagnostic per duplicate).

### Decision: `connection` is conditional on `type: integration`

`EvidenceCollector.connection` is validated by type, in the collector phase after
the connection id set is known:

- `type: integration`: `connection` is required and must resolve to an
  `IntegrationConnection` id (an unresolved reference is a dangling-reference
  diagnostic, same style as `control`/`vendor`).
- any other type (`script`, `agent`, `manual-attestation`, `training-attestation`):
  `connection` must be empty; a present `connection` is a diagnostic (a
  connection is meaningless without an integration runner).

`checks` follows the same conditional shape for symmetry: required non-empty for
`integration`, and a diagnostic when present on a non-integration collector. The
issue states the non-empty-when-integration rule explicitly; the
diagnostic-when-present-otherwise rule is the deliberate mirror of `connection`,
so a mis-typed collector fails cleanly rather than silently carrying dead checks.

Validator phase order gains one step: vendors -> integration-connections
(consumes vendor ids for the optional `vendor` reference, produces the connection
id set) -> evidence-collectors (consumes control, vendor, and connection id sets).

### Decision: connection id must be a safe configuration-key segment

The token is resolved from `IConfiguration` at `Freeboard:Integrations:<id>:ApiToken`,
so the connection `id` is interpolated into a configuration key path. .NET
configuration keys are case-insensitive and `:`-delimited, and the environment-variable
provider maps `__` to `:`. Three id shapes make token resolution ambiguous or wrong:

- an id containing `:` splits the key path, so `Freeboard:Integrations:<id>:ApiToken`
  no longer addresses a single token slot;
- an id containing `__` collides with the environment-variable delimiter and resolves a
  different key than intended;
- two connection ids that differ only in case (`fleet-prod` and `Fleet-Prod`) are
  distinct rows under the `utf8mb4_bin` id collation and distinct under Core's ordinal
  id identity, yet resolve to the same case-insensitive configuration key, so one
  connection's token silently answers for the other.

`ConfigValidator` has no existing id-charset rule - ids are only checked non-blank
(`CheckRequired`) and unique per kind (ordinal `Dup`), so nothing today constrains an id
to a safe key segment. This change adds the minimum needed and scopes it to the
`IntegrationConnection` id alone, because only the connection id becomes a configuration
key segment. `ValidateIntegrationConnections` rejects a connection `id` that contains `:`
or `__`, and rejects connection ids that collide case-insensitively (a diagnostic naming
the colliding id). No other kind gains an id rule.

The case-insensitive-uniqueness rule is a deliberate, kind-local tightening of Core's
ordinal id identity: connection ids are still stored in a binary `utf8mb4_bin` column
(the `compliance-persistence` binary-collation rule is unchanged), but two case-colliding
ids are rejected in validation before reaching the store, because they cannot both
resolve a distinct token. No other kind's exact-byte identity is relaxed.

Alternative considered: a general id-charset rule across every kind. Rejected
(code-as-liability): only the connection id resolves a secret, so a blanket rule adds
validation surface with no consumer.

### Decision: out-of-band token resolution seam

The token is resolved from `IConfiguration` at the key
`Freeboard:Integrations:<id>:ApiToken`, where `<id>` is the connection instance id
(supplied by environment variables or user-secrets). A single small service in the
web project, `IIntegrationTokenResolver` (impl `IntegrationTokenResolver(IConfiguration)`),
owns the key shape and exposes `bool IsResolvable(string connectionId)` - true when
the keyed value is present and non-blank. It returns only a bool and never returns,
logs, or stamps the value. Registered as a singleton.

`IsResolvable` has multiple consumers, so the seam is not a single-caller helper:
the read health flag (web page and HTTP endpoint) and the startup warning. The
future collection runner adds a value-returning `TryResolve` when it exists; it is
not added now (no consumer yet - code-as-liability).

- Never persisted: the DB read model (`IntegrationConnectionRow`) carries only
  `id`, `provider`, `base_url`, `discovery_cadence`, `vendor`. No token-derived
  column, no token-derived field.
- Never logged: only `IsResolvable` (a bool) is observable; the startup warning and
  any diagnostic name the connection id, never the value.

### Decision: startup warning names the id, boots anyway

At startup the web app logs one `LogWarning` per referenced connection whose token
is unresolvable, naming the connection id, and boots regardless. "Referenced"
means named by an `EvidenceCollector` of `type: integration`; to know that set the
read model `EvidenceCollectorRow` gains a nullable `Connection` field (selected as
`connection_id`), so the check reads collectors + connections and warns for each
integration collector's connection that fails `IsResolvable`. It is not a boot
gate.

Placement: the post-`builder.Build()` startup block in `Program.cs` already runs
non-fatal `LogWarning` checks (the email dev-sink warning) and fail-fast throws.
The token check goes there, wrapped so a store outage at boot neither fails boot
nor emits a spurious warning (it simply skips - the runtime read path and the
scheduler will surface the same condition later). This keeps the check to existing
machinery rather than a new hosted service.

This is a boot-time DB read, which the ratified `compliance-web-read` `Read path
tolerates an unavailable store` requirement otherwise forbids ("SHALL NOT auto-connect
to MySQL at startup"). Issue acceptance requires the startup warning, so the behaviour
stays and the requirement is narrowly amended (a `compliance-web-read` MODIFIED delta):
the web app MAY perform this one guarded, best-effort startup read whose sole effect is
logging warnings and which silently skips - never throwing, never blocking or gating
boot - on any store outage. The invariant's spirit holds: boot has no hard DB dependency
and is never blocked or failed by the store.

The collection-time outcome is a contract, not code in this change, and it maps onto
the collector-scheduler's existing `error` status - not onto a new evidence-run result.
The distinction matters and is settled by the code:

- The evidence store's run and check `result` is a closed `{Pass, Fail}` set.
  `MySqlEvidenceWriteStore.Validate` rejects any run or check whose `result` is not
  `Pass` or `Fail` (`IsResult`), `NewEvidenceRun`/`NewEvidenceCheck` document the same,
  and the ratified `evidence-persistence` spec states "both the run-overall `result` and
  each per-check `result` SHALL draw from the closed set `{Pass, Fail}`". There is
  therefore no first-class `Error` evidence-run result to write, and this change adds
  none.
- The scheduler, by contrast, already has an `error` outcome at dispatch time.
  `CollectorSchedulerService` wraps `IScheduledCollectorRunner.RunAsync` in a try/catch,
  and `MySqlCollectorSchedulerStore.CompleteFailureAsync` records a failed dispatch as
  `collector_scheduler_state.status = 'error'` (or `'dead'` past `MaxAttempts`). The
  ratified `collector-scheduler` spec closes that status set to
  `pending`/`running`/`ok`/`error`/`dead`.

So the future integration runner, on an unresolvable token, fails the dispatch, and the
existing wiring records that as the scheduler `error` status - distinct from a `Pass`
or `Fail` evidence run, and not a masked `Fail`. The token value never enters the
recorded error or any log. This change asserts that contract in the spec and builds no
runner and no evidence-persistence change. It does not surface an `Error` status in the
evidence read model or the Statement of Applicability, whose ratified status set is
`HardFailure`/`SoftFailure`/`Passing`/`Stale`/`Unknown` with no `Error` member; doing so
would require deltas to `evidence-persistence` and `statement-of-applicability` and
belongs with the runner. See Open Questions.

Alternative considered: a dedicated `IHostedService` startup check that reads the
DB asynchronously post-boot. Rejected as higher-liability (a new component) for the
same outcome; noted in Open Questions because the web app otherwise avoids a
synchronous boot-time DB read.

### Decision: `tokenResolvable` health composed at read time

The read model never carries token state. Both read surfaces compose
`tokenResolvable` at read time via `IIntegrationTokenResolver.IsResolvable(id)`:

- Web list page (`Pages/Compliance/IntegrationConnections.cshtml[.cs]`, route
  `/settings/integration-connections`): injects `IComplianceStore` and
  `IIntegrationTokenResolver`, reads `GetIntegrationConnectionsAsync`, composes the
  flag per row, renders provider, base URL, cadence, and the health flag using the
  established `StoreUnreachable` notice, empty-state (L7), and `data-*` hooks; nav
  entry added to `ShellNavCatalog` under `Platform`.
- HTTP endpoint (`GET /api/v1/freeboard/integration-connections` in
  `ComplianceEndpoints.cs`): returns a snake_case JSON array
  `{ id, provider, base_url, discovery_cadence, vendor, token_resolvable }`,
  composing `token_resolvable` the same way; `Unreachable()` (503) on store outage;
  GET-only so GitOps read-only mode never blocks it.
- CLI (`connections list`): a `ConnectionCommands` group registered
  `app.Add<ConnectionCommands>("connections")`, a client method on
  `IFreeboardApiClient`/`HttpFreeboardApiClient` hitting the endpoint, a
  `ReadIntegrationConnection` mapper, and plain `Console.WriteLine` rendering of
  provider, base URL, cadence, and token-resolvable health; exit codes 0/1/3 via
  `ApiCommandRunner`.

### Decision: schema (migration 018)

`integration_connection` table (singular table name follows the kind; the
established GitOps-kind tables are plural, so use `integration_connections` to
match `evidence_collectors`/`vendors`):

- `id VARCHAR(190) utf8mb4_bin NOT NULL` PK (authored id, exact-byte, as every
  GitOps-kind id).
- `api_version VARCHAR(64) NOT NULL`, `title VARCHAR(512) NOT NULL`.
- `provider VARCHAR(32) NOT NULL`, `discovery_cadence VARCHAR(16) NOT NULL` (token
  columns, default collation, matching `evidence_collectors.type`/`frequency`).
- `base_url VARCHAR(2048) NOT NULL` (matching `source_url`/`citation_url` width).
- `vendor_id VARCHAR(190) utf8mb4_bin NULL`.
- `created_at DATETIME(6) NOT NULL`, `updated_at DATETIME(6) NOT NULL`.
- `PRIMARY KEY (id)`, `KEY ix_integration_connections_vendor_id (vendor_id)`,
  `CONSTRAINT fk_integration_connections_vendor FOREIGN KEY (vendor_id) REFERENCES
  vendors (id) ON DELETE RESTRICT`.
- No `organisation_id` (GitOps-kind tables are global; org relevance, if ever
  needed, is a separate `*_scopes` mapping table - not in scope).

`evidence_collectors` gains, in the same migration and as a single multi-clause
`ALTER TABLE` (one statement, applied after `integration_connections` exists):

- `ADD COLUMN connection_id VARCHAR(190) utf8mb4_bin NULL AFTER vendor_id`
  (nullable: only integration collectors carry it),
- `ADD COLUMN checks JSON NULL AFTER config`,
- `ADD KEY ix_evidence_collectors_connection_id (connection_id)`, and
- `ADD CONSTRAINT fk_evidence_collectors_connection FOREIGN KEY (connection_id)
  REFERENCES integration_connections (id) ON DELETE RESTRICT`.

The column adds are metadata-only in MySQL 8.4 (a nullable column and a JSON
column) and rewrite no rows; existing collectors read `connection_id = NULL` and
`checks = NULL`.

Replay safety. `MySqlMigrationRunner` runs the whole migration file as one batch and
records the `schema_migrations` version only after the file succeeds; DDL
implicit-commits on MySQL, so a crash partway through leaves the version unrecorded and
the entire file re-runs on the next `freeboard system migrate`. The
`integration_connections` table is created with `CREATE TABLE IF NOT EXISTS`, so its
statement re-runs cleanly (as `017` does). The `evidence_collectors` change is one
multi-clause `ALTER TABLE`: MySQL 8.4 InnoDB DDL is atomic per statement, so a failed
apply of that ALTER rolls back wholly - the file never leaves some columns added and
others not, unlike four separate bare ALTERs. A re-run after a failed apply therefore
completes: the `CREATE TABLE` no-ops and the rolled-back ALTER re-applies.

One residual window is not idempotent: if a crash lands after the ALTER commits but
before the runner records the version, the re-run's `ADD COLUMN` fails with a
duplicate-column error. MySQL 8.4 has no `ADD COLUMN IF NOT EXISTS`, and this repo
cannot use the usual `information_schema` + `PREPARE`/`EXECUTE` guard: that guard needs
session variables (`SET @x := ...`), but `MySqlConnector` defaults
`AllowUserVariables=false` and the runner executes `migration.Sql` with no parameters,
so a `@name` parses as a command parameter and the file would fail on the FIRST apply.
Forcing `AllowUserVariables=true` on the shared connection factory is rejected
(app-wide blast radius). So `018` matches the sibling `015` convention: a single atomic
ALTER that is not atomically replay-safe against the crash-after-commit window. The
migration header comment states this and the manual recovery (drop the added columns,
key, and FK and re-run `freeboard system migrate`, or record the `schema_migrations`
row by hand), exactly as `015` documents.

Importer ordering: upsert vendors -> upsert integration-connections (references
vendors) -> upsert evidence-collectors (references connections). Prune order in the
delete phase: absent evidence_collectors (they reference connections) ->
absent integration_connections (they reference vendors) -> absent vendors, so every
RESTRICT FK stays satisfied.

## Risks / Trade-offs

- [Provider/source token literal mismatch] The connection `provider` is fixed to
  `fleet` here, but the machine `asset_source.source` literal is set by the future
  FleetDM collector and the asset-model example used `fleetdm`. If the two diverge,
  a connection would not tie to its machines. -> Mitigation: state the alignment
  contract in the spec (source token equals provider token) and pin `fleet` as the
  provider token so the collector change has one literal to match. Flagged as an
  open question.
- [Startup warning needs a boot-time DB read] Sourcing the referenced-connection
  set requires reading collectors/connections at boot, which the web app otherwise
  avoids. -> Mitigation: wrap the read so a store outage at boot is a silent skip
  (non-fatal), never a boot failure; the same unresolvable-token condition
  re-surfaces at collection time as the scheduler's `error` status. Alternative
  hosted-service placement noted.
- [Checks acceptance not fully provable in V1] "A Fleet policy absent from the
  authored checks list does not change results" cannot be exercised end-to-end
  without the scoring/runner. -> Mitigation: prove it at the model and round-trip level.
  Core validation proves the tracked check set equals exactly the authored `checks`;
  the DB round-trip asserts the persisted set by reading the `evidence_collectors.checks`
  JSON column directly via SQL (nothing renders `checks` in V1, so there is no read-model
  field to read back - the direct-SQL assertion mirrors the asset round-trip tests). An
  unauthored `source_key` is absent from that column and referenced by nothing. The
  behavioural proof lands with the runner.
- [Token in `IConfiguration` only] No secret-store integration. -> Trade-off:
  accepted for V1 per the issue; the resolver seam is the single place a secret
  store later plugs in.
- [Two `type`-conditional field rules on one kind] `connection` and `checks` both
  branch on `type: integration`. -> Mitigation: both rules live in the existing
  collector validation phase and reuse the established diagnostic style; symmetry
  keeps them readable.

## Migration Plan

- Forward-only: add `018_integration_connections.sql`, applied by `freeboard system
  migrate` after 017. Creates `integration_connections`, then alters
  `evidence_collectors` to add `connection_id` + `checks` with their key and FK. No
  backfill (no prior connection data; existing collectors read the new columns as
  NULL).
- Rollback: none required pre-release; additive and unused by existing behaviour.
  A later forward migration would drop the FK, the two columns, and the table if
  ever needed.

## Open Questions

- Provider/source literal: confirm the FleetDM collector will write
  `asset_source.source = 'fleet'` to match the `provider` token fixed here, or
  agree a different shared literal now. Current plan: `fleet`. This is the one
  cross-change coordination note that stays open, because the collector change owns the
  source literal and lands separately.
- Should the evidence-collector read surfaces (register page, `collector list`,
  `GET /evidence-collectors`) also display a collector's `connection`? Out of scope
  here (the connections list is the new read surface); `EvidenceCollectorRow` gains
  `Connection` only to source the startup warning, not to render it. Confirm this
  is the intended minimal surface.
- `checks`-on-non-integration: this plan makes a present `checks` on a
  non-integration collector a diagnostic (mirroring `connection`). Confirm that
  symmetry is wanted, or keep `checks` merely ignored off the integration path.

The collection-time error scope and the startup-check placement are settled, not open.
The collection-time "Error" outcome is the collector-scheduler's existing `error`
status, not an evidence run (the evidence `result` set is ratified closed to `{Pass,
Fail}` and the Statement of Applicability status set has no `Error` member), so this
change builds no runner and adds no evidence-run `Error` result; see the startup-warning
decision. The startup check is the guarded boot-time read in the `Program.cs` post-build
block, permitted by the narrow `compliance-web-read` amendment above; the hosted-service
alternative is recorded there as considered and rejected.

## Reconciled decisions

Where more than one reasonable design existed, the choice made and why:

1. Connection-id config-key safety. The token key `Freeboard:Integrations:<id>:ApiToken`
   makes the connection id a configuration key segment; .NET keys are case-insensitive
   and `:`/`__`-sensitive, so an id with `:` or `__`, or two ids differing only in case,
   resolve the wrong or an ambiguous secret. `ConfigValidator` had no id-charset rule, so
   a minimal rule was added, scoped to the `IntegrationConnection` id only (no other id
   resolves a secret).
2. `checks` and `connection` storage as columns, not child tables. The
   `compliance-persistence` spec fixes the pattern: child lists are `JSON` columns on the
   parent (`config`, `fields`, `quiz`) and single-valued references are foreign-key
   columns (`vendor_id`, `control_id`). So `checks` is a `JSON` column and `connection`
   is a `connection_id` column. An `evidence_collector_checks` child table and an
   `evidence_collector_connections` link table were rejected: uniqueness is already
   enforced in Core validation, and a link table over-models a single-valued reference.
3. Error-run scope. The collection-time "Error" is the collector-scheduler's existing
   `error` status, not an evidence run: evidence `result` is ratified closed to `{Pass,
   Fail}`, so no first-class `Error` evidence run exists and none is added here. The
   contract framing holds but its wording is precise (scheduler `error` status, not a
   masked evidence run). Surfacing a first-class `Error` in the evidence read model or the
   Statement of Applicability would contradict the ratified closed `{Pass, Fail}` result
   set and the SoA status set, and belongs with the runner. This is settled, not open.
4. Provider/source token `fleet`. The alignment contract (a machine's
   `asset_source.source` token equals the connection `provider`) is stated and `fleet` is
   pinned. The asset-model precedent uses `fleetdm` only in comments/examples
   (`AssetReadModels.cs`, `017_assets.sql`) over a free `VARCHAR(64)` `source` with no
   closed set; those are not rewritten here, but the FleetDM collector must write `fleet`
   to honour the alignment.
5. `title` on the connection. Every GitOps kind carries a `title` and `ConfigValidator`
   checks it required on every kind (`CheckRequired(... "title" ...)`), and every kind
   table stores `title NOT NULL`, so `title` stays authored and persisted for pattern
   consistency. The read model exposes a deliberate subset -
   `id`/`provider`/`base_url`/`discovery_cadence`/`vendor` - and omits the persisted
   `api_version` and `title`; both are persisted but not surfaced.
6. Startup check placement. The `Program.cs` post-`builder.Build()` block already runs
   non-fatal `LogWarning` checks (the email log-sink warning) and fail-fast throws; a
   guarded boot-time read there is lower liability than a new `IHostedService`. The
   hosted-service alternative stays noted.
7. Spec-delta completeness. `compliance-persistence`'s `EvidenceCollector persistence`
   requirement enumerates the `evidence_collectors` columns as a closed list, and this
   change adds `connection_id` and `checks` to that table, so a `compliance-persistence`
   MODIFIED delta was added. A `compliance-web-read` MODIFIED delta was also added: its
   `Read path tolerates an unavailable store` requirement forbids a startup auto-connect,
   and the guarded boot-time token-warning read touches that invariant, so the requirement
   is narrowly amended to permit that one non-fatal, silently-skipping read. No
   `evidence-persistence`, `evidence-ingest`, `collector-scheduler`, or
   `statement-of-applicability` delta is needed because the Error outcome stays the
   existing scheduler `error` status (item 3).
