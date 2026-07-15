## 1. Core config model and loader (feat(core): integration-connection config model and loader)

- [x] 1.1 In `src/Freeboard.Core/GitOps/ConfigModel.cs`: add `KindIntegrationConnection`
  to `GitOpsSchema`; add an `IntegrationConnection` record (`ApiVersion`, `Kind`,
  `Id`, `Title`, `Provider`, `BaseUrl`, `DiscoveryCadence`, `Vendor`) with a doc
  comment stating identity is `id`, `provider` is a closed token, and the API token
  is resolved out-of-band (never a field); add a `Check` record (`SourceKey`,
  `Name`, `Severity`); add `Connection` (string) and `Checks` (`List<Check>`) to
  `EvidenceCollector`; add `IntegrationConnections` to `GitOpsConfig`.
- [x] 1.2 Add `src/Freeboard.Core/GitOps/IntegrationProvider.cs` with a closed
  `Tokens` set (`fleet`) compared `StringComparer.Ordinal`, mirroring
  `EvidenceCollectorFrequency.Tokens`, so the future runner shares the token set.
- [x] 1.3 In `ConfigLoader.cs`: add the `IntegrationConnection` schema keys
  (`apiVersion`, `kind`, `id`, `title`, `provider`, `base_url`, `discovery_cadence`,
  `vendor`); add `connection` and `checks` to the `EvidenceCollector` schema keys;
  add the `IntegrationConnection` kind-routing case; register the `apiVersion` alias
  override; on the `EvidenceCollector` case, normalize an explicit-null `checks` to
  an empty list and drop null items (mirroring `config`/`fields`/`quiz`); add
  `IntegrationConnection` to the unknown-kind diagnostic message.
- [x] 1.4 Add `Freeboard.Core.Tests` load tests: an `IntegrationConnection` document
  loads into the typed model; an integration `EvidenceCollector` loads with its
  `connection` and `checks`; an unknown kind message includes `IntegrationConnection`;
  a null `checks` normalizes to empty.
- [x] 1.5 `dotnet build src/Freeboard.Core` and run `Freeboard.Core.Tests`.

## 2. Core validation (feat(core): integration-connection and collector checks validation)

- [x] 2.1 In `ConfigValidator.cs`: add a `CheckSeverityTokens` set (`Hard`, `Soft`,
  ordinal) beside `EvaluationTokens`; add `ValidateIntegrationConnections` returning
  the connection id set, run after `ValidateVendors` and before
  `ValidateEvidenceCollectors` in `Validate`, checking required
  `id`/`title`/`provider`/`base_url`/`discovery_cadence`, `provider` in
  `IntegrationProvider.Tokens`, `base_url` via `IsAbsoluteHttpUri`,
  `discovery_cadence` in `EvidenceCollectorFrequency.Tokens`, optional `vendor`
  resolves to a vendor id, unique id (ordinal, as every kind), and a
  configuration-key-safe id: reject an id containing `:` or `__`, and reject connection
  ids that collide case-insensitively (a diagnostic naming the colliding id), because
  the id is interpolated into `Freeboard:Integrations:<id>:ApiToken` and .NET config
  keys are case-insensitive and `:`/`__`-delimited. This id rule is scoped to
  `IntegrationConnection` only (no other id resolves a secret).
- [x] 2.2 In `ValidateEvidenceCollectors` (now taking the connection id set): when
  `type` is `integration`, require a `connection` that resolves to a connection id and
  a non-empty `checks`; when `type` is not `integration`, a non-empty `connection` or
  `checks` is a diagnostic. Validate each `checks` item: required
  `source_key`/`name`/`severity`, `severity` in `CheckSeverityTokens`, unique `name`
  and unique `source_key` within the collector (one diagnostic per duplicate, mirroring
  `CheckNoDuplicateRefs`).
- [x] 2.3 Update the `ConfigValidator` class doc comment to mention the
  integration-connection checks and the collector `connection`/`checks` rules.
- [x] 2.4 Add `Freeboard.Core.Tests` validation tests: missing required connection
  field; unknown provider; malformed base URL; unknown cadence; dangling vendor;
  duplicate connection id; a connection id containing `:` rejected; a connection id
  containing `__` rejected; two connection ids differing only in case rejected;
  integration collector missing/dangling connection;
  integration collector empty checks; connection/checks on a non-integration collector;
  unknown check severity; duplicate check name; duplicate check source_key; and a
  positive test that a valid integration collector plus connection validates clean.
- [x] 2.5 Add a `Freeboard.Core.Tests` test proving the tracked check set equals
  exactly the authored `checks`: a collector authored with checks `{A(Hard), B(Soft)}`
  yields exactly those two checks, and a provider-native id not in the authored list
  (a Fleet policy absent from `checks`) is not represented, so it changes nothing.
- [x] 2.6 `dotnet build src/Freeboard.Core` and run `Freeboard.Core.Tests`.

## 3. Persistence migration (feat(persistence): integration_connections migration 018)

- [x] 3.1 Add `src/Freeboard.Persistence/Migrations/018_integration_connections.sql`:
  create `integration_connections` (`id VARCHAR(190) utf8mb4_bin` PK, `api_version`,
  `title`, `provider VARCHAR(32)`, `base_url VARCHAR(2048)`,
  `discovery_cadence VARCHAR(16)`, `vendor_id VARCHAR(190) utf8mb4_bin NULL`,
  `created_at`/`updated_at DATETIME(6)`, `KEY ix_integration_connections_vendor_id`,
  `fk_integration_connections_vendor` to `vendors(id)` ON DELETE RESTRICT) with
  `CREATE TABLE IF NOT EXISTS` so its statement re-runs cleanly; then apply the
  `evidence_collectors` change as ONE multi-clause statement: `ALTER TABLE
  evidence_collectors ADD COLUMN connection_id VARCHAR(190) utf8mb4_bin NULL AFTER
  vendor_id, ADD COLUMN checks JSON NULL AFTER config, ADD KEY
  ix_evidence_collectors_connection_id (connection_id), ADD CONSTRAINT
  fk_evidence_collectors_connection FOREIGN KEY (connection_id) REFERENCES
  integration_connections (id) ON DELETE RESTRICT;` so MySQL 8.4 InnoDB atomic DDL
  applies or rolls back the whole change as a unit (no half-applied columns). No
  `organisation_id` (global GitOps kind). Do NOT add an `information_schema` /
  `PREPARE` / session-variable guard: `MySqlConnector` defaults `AllowUserVariables=false`
  and the runner runs the SQL with no parameters, so `@name` would parse as a command
  parameter and fail on the first apply. The single ALTER is atomic on a failed apply,
  but bare `ADD COLUMN` is not idempotent against the crash-after-commit window (MySQL
  8.4 has no `ADD COLUMN IF NOT EXISTS`), so `018` is not fully replay-safe - the same
  stance as sibling `015`. State this and the manual recovery in the migration's header
  comment, mirroring `015`.
- [x] 3.2 Confirm the file is picked up by the embedded `Migrations/*.sql` glob and
  orders after 017.

## 4. Persistence import and read model (feat(persistence): integration-connection import and read model)

- [x] 4.1 In `src/Freeboard.Persistence/GitOps/ImportPlan.cs`: add an
  `IntegrationConnectionRowPlan` (`Id`, `ApiVersion`, `Title`, `Provider`, `BaseUrl`,
  `DiscoveryCadence`, `Vendor?`) mapped from `config.IntegrationConnections`
  (`NullIfBlank` the vendor); add `IntegrationConnectionIds`; add `Connection` (via
  `NullIfBlank`) and `ChecksJson` (via `SerializeList`) to `EvidenceCollectorRowPlan`
  and its mapping.
- [x] 4.2 In `MySqlGitOpsImporter.cs`: add `UpsertIntegrationConnectionsAsync`
  (`INSERT ... ON DUPLICATE KEY UPDATE`) called after vendors and before
  evidence-collectors; extend the evidence-collector upsert SQL and parameters to
  write `connection_id` and `checks`; add a `DeleteAbsentAsync(..., "integration_connections",
  plan.IntegrationConnectionIds, ...)` in the delete phase after the absent
  evidence-collector prune and before the absent vendor prune; update the class doc
  comment's FK-safe ordering description.
- [x] 4.3 In `ComplianceReadModels.cs`: add an `IntegrationConnectionRow` (`Id`,
  `Provider`, `BaseUrl`, `DiscoveryCadence`, `Vendor?`) - a deliberate subset of the
  persisted columns (`api_version`/`title` persisted but not surfaced), no token state;
  add a nullable `Connection` to `EvidenceCollectorRow` (sourced for the startup warning,
  not rendered).
- [x] 4.4 In `IComplianceStore.cs` / `MySqlComplianceStore.cs`: add
  `GetIntegrationConnectionsAsync` (`SELECT ... FROM integration_connections ORDER BY
  id`); extend the evidence-collector read to select `connection_id` into
  `EvidenceCollectorRow.Connection`.
- [x] 4.5 Add `Freeboard.Persistence.Tests` unit tests for `ImportPlan`: an
  integration-connection maps to its row; an integration collector's `checks`
  serialize to a JSON array and `connection` maps to `connection_id`;
  `IntegrationConnectionIds` lists the ids.
- [x] 4.6 `dotnet build` and run the non-integration `Freeboard.Persistence.Tests`.

## 5. Persistence DB round-trip tests (test(persistence): integration-connection round-trip)

- [x] 5.1 Add MySQL integration tests gated on `FREEBOARD_TEST_DB` (skip cleanly when
  unset): migration 018 creates the table, the collector columns, and the FKs; a
  connection round-trips through the importer and read model with its exposed subset
  (no token state); an integration collector persists its `connection_id` (read back via
  the collector read model's `connection`). Assert the persisted `checks` directly by
  reading the `evidence_collectors.checks` JSON column via SQL - nothing renders `checks`
  in V1, so there is no read-model field to assert against, and the direct-SQL assertion
  mirrors the asset round-trip tests: the stored array equals exactly the authored
  checks, and a `source_key` absent from the authored list is absent from the column,
  proving a Fleet policy outside `checks` changes nothing.
- [x] 5.2 Add a hard-remove ordering integration test: sync a config with a connection
  and a referencing integration collector, then re-sync dropping the collector and the
  now-unreferenced connection; assert both rows are removed with no FK violation and
  retained rows persist.
## 6. Web read surface and token resolution (feat(web): integration-connection read view and token resolver)

- [x] 6.1 Add `IIntegrationTokenResolver` and `IntegrationTokenResolver(IConfiguration)`
  in `src/Freeboard`, owning the key shape `Freeboard:Integrations:<id>:ApiToken` and
  exposing `bool IsResolvable(string connectionId)` (returns only a bool, never the
  value); register as a singleton in `Program.cs`.
- [x] 6.2 In `Compliance/ComplianceEndpoints.cs`: add `GET
  /api/v1/freeboard/integration-connections` returning a snake_case JSON array
  (`id`, `provider`, `base_url`, `discovery_cadence`, `vendor`, `token_resolvable`),
  composing `token_resolvable` via the resolver, `Unreachable()` on store outage.
- [x] 6.3 Add `Pages/Compliance/IntegrationConnections.cshtml[.cs]` at route
  `/settings/integration-connections`: inject `IComplianceStore` and
  `IIntegrationTokenResolver`, read connections, compose the health flag per row,
  render provider/base URL/cadence/health with the `StoreUnreachable` notice, an empty
  state, and `data-*` hooks; add a `ShellNavItem` under `Platform` in
  `Navigation/ShellNavCatalog.cs`.
- [x] 6.4 In the `Program.cs` post-`builder.Build()` block: log one `LogWarning` per
  integration-referenced connection whose token is unresolvable (name the id, never the
  value), guarded so a store outage at boot is a silent non-fatal skip; do not gate
  boot on it.
- [x] 6.5 Add `Freeboard.Web.Tests`: the page lists connections with the health flag
  and never the token value; an anonymous GET redirects to `/login`; a store outage
  renders the notice; an empty store renders the empty state; the HTTP endpoint returns
  the JSON shape with `token_resolvable` and no token; a resolvable vs unresolvable
  token flips the flag; the startup warning names the id and asserts the token value
  appears in no log.
- [x] 6.6 `dotnet build src/Freeboard` (runs the asset build) and run
  `Freeboard.Web.Tests`.

## 7. CLI list command (feat(cli): connections list command)

- [x] 7.1 In `Freeboard.CLI`: add `ListIntegrationConnectionsAsync` to
  `IFreeboardApiClient`; implement it in `HttpFreeboardApiClient` against `GET
  {ApiRoutePrefix}/integration-connections` with a `ReadIntegrationConnection`
  JSON mapper (including `token_resolvable`).
- [x] 7.2 Add a `ConnectionCommands` class with a `List` command that uses
  `ApiCommandRunner.Run`/`Translate`, prints provider, base URL, discovery cadence, and
  token-resolvable health via `Console.WriteLine` (never the token), and register it in
  `Program.cs` as `app.Add<ConnectionCommands>("connections")`.
- [x] 7.3 Add `Freeboard.CLI.Tests`: `connections list` prints connections with health
  and exits 0 on success; exits 3 on an unreachable API or a rejected token; the output
  never contains a token value.
- [x] 7.4 `dotnet build src/Freeboard.CLI` and run `Freeboard.CLI.Tests`.

## 8. Documentation (docs: document the IntegrationConnection kind)

- [x] 8.1 Update `docs/gitops.md`: add the `IntegrationConnection` kind (schema fields,
  an example document, validation rules, its optional `vendor` reference, and that the
  API token is resolved out-of-band by connection id, never in config); document the new
  `EvidenceCollector` `connection` (required for `type: integration`) and `checks`
  fields and their referential-integrity rules; add `IntegrationConnection` to the
  supported-kind list and noun table. Run `npx markdownlint-cli2 "**/*.md"`.

## 9. Verification (chore: build, test, and validate)

- [x] 9.1 `dotnet build` the solution.
- [x] 9.2 `dotnet test` (unit/web/CLI tests pass; MySQL integration tests skip cleanly
  without `FREEBOARD_TEST_DB` and pass when set against the test-compose MySQL).
- [x] 9.3 Confirm `Freeboard.Architecture.Tests` still pass: no new
  `Freeboard.Enterprise` reference from Core, Persistence, or CLI; the CLI read path
  reaches the HTTP API, not the database.
- [x] 9.4 `openspec validate "add-integration-connection" --strict` passes.
