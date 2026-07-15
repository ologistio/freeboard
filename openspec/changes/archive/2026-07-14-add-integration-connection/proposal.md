## Why

A collector today attaches to a single `Control` and its `config` map holds no
secret material. An integration such as FleetDM is not one collector but a
*connection*: one base URL and one API token that both drive discovery and back
many per-control collectors. There is nowhere in the config model to hang that
connection, and nowhere to resolve its secret. This change introduces the
first-class integration-connection primitive and the out-of-band token
resolution it needs, so a real integration collector has a connection to attach
to.

## What Changes

- Add a new GitOps kind `IntegrationConnection` with a full DB round-trip
  (loader -> validator -> importer -> table -> read model), read-only over the
  DB on the web. A connection carries a required unique `id`, a required
  `provider` from a closed token set (V1 `fleet`) that selects the runner and
  aligns with a machine's `asset_source.source`, a required absolute http/https
  `base_url` (same check as `source_url`/`citation_url`), a required
  `discovery_cadence` reusing the evidence-collector frequency token set, and an
  optional `vendor` reference to a `Vendor` id.
- Extend the `EvidenceCollector` kind with two fields: `connection` (required and
  must resolve when `type` is `integration`; must be empty for every other type,
  a diagnostic if present) and `checks` (a typed, validated list of
  `{ source_key, name, severity }`, required non-empty when `type` is
  `integration`, with known severity `Hard`/`Soft` and unique `name` and unique
  `source_key` within the collector).
- Resolve the API token out-of-band from `IConfiguration`, keyed by the
  connection id (`Freeboard:Integrations:<id>:ApiToken`, supplied by env or
  user-secrets). Because the id becomes a configuration-key segment, a connection
  id is validated to be a safe key segment: it may not contain `:` or `__`, and two
  connection ids may not collide case-insensitively, so a token never resolves
  ambiguously or to the wrong connection. The token is never authored in git, never
  in a collector `config`, never persisted, never logged. A referenced connection
  with no resolvable token warns once at startup (naming the connection id, never the
  value) and boots anyway; that connection's collector runs then fail their scheduled
  dispatch, recorded as the collector-scheduler's `error` status at collection time
  (not a `Pass`/`Fail` evidence run). It is not a hard boot gate.
- Add a minimal read surface on both the web and the CLI: a read-only web list
  view of connections and a `connections list` CLI command (CLI via the HTTP
  API), each showing provider, base URL, cadence, and a `tokenResolvable` health
  flag. The DB read model carries only a subset of the persisted columns (`api_version`
  and `title` are persisted but not surfaced); the health flag is composed from
  `IConfiguration` at read time in the web API and never stored.

## Capabilities

### New Capabilities

- `integration-connection`: the integration-connection domain and its lifecycle
  outside the config-format contract. Covers the closed `provider` token set and
  its provider-level alignment with `asset_source.source`, the `integration_connection`
  persistence table and its persisted-subset read model, out-of-band token
  resolution from `IConfiguration` (never persisted, never logged, startup
  warning, and a scheduler `error` status at collection time when unresolvable),
  the `tokenResolvable` health flag composed at read time, the read-only web list
  view, and the `connections list` CLI command.

### Modified Capabilities

- `gitops-config-format`: add the `IntegrationConnection` kind (authorship and
  validation), add `connection` and `checks` to the `EvidenceCollector` kind and
  its validation, and extend the enumerated kind list in the schema, loader
  kind-set, no-secret-material, validation-umbrella, and documentation
  requirements to include the new kind. The token remains out-of-band: no
  schema field holds credential material.
- `gitops-cli`: extend the referential-integrity and sync round-trip
  requirements so `gitops validate` and `gitops sync` cover
  `IntegrationConnection` (round-trip, hard-remove on absence, and the
  `EvidenceCollector.connection` dangling-reference check).
- `compliance-persistence`: extend the `EvidenceCollector persistence` requirement,
  whose closed column enumeration owns the `evidence_collectors` table, to add the
  nullable `connection_id` foreign key and the nullable `checks` JSON column, the
  `connection_id` RESTRICT foreign key, the collector read model's nullable
  `connection`, and the importer's foreign-key-safe prune order (collectors before
  integration-connections before vendors). The new `integration_connections` table
  stays owned by the `integration-connection` capability.
- `compliance-web-read`: narrowly amend the `Read path tolerates an unavailable store`
  requirement, which forbids the web app from auto-connecting to MySQL at startup, to
  permit one guarded, non-fatal, best-effort startup read whose sole effect is logging
  integration-connection token warnings and which silently skips (never throws, never
  blocks or gates boot) on any store outage. The startup warning is a required behaviour
  and this read touches that invariant, so the requirement carries the delta; boot keeps
  no hard DB dependency.

## Impact

- New code in `Freeboard.Core` (the `IntegrationConnection` record, the
  `Check` item type, the closed `provider` token set, the new fields on
  `EvidenceCollector`, loader kind-routing and schema keys, validator rules) and
  `Freeboard.Persistence` (row plan, importer upsert/prune, migration
  `018_integration_connections.sql` adding the `integration_connection` table
  plus `connection_id` and `checks` columns on `evidence_collectors`, a read
  model and store read method, DI wiring). New read surface in `Freeboard` (web
  list page, HTTP API endpoint, `IConfiguration`-based token-resolvable health
  and startup warning) and `Freeboard.CLI` (`connections list` via the HTTP
  API).
- MIT only. Nothing in `Freeboard.Enterprise`. `Freeboard.CLI` stays
  community and cross-platform and reaches the data through the HTTP API, not the
  database.
- No new package dependency; reuses YamlDotNet, Dapper, MySqlConnector, the
  migration runner, and the existing config loader/validator/importer pipeline.
- Unblocks the FleetDM integration collector and discovery runner, which consume
  the connection and its resolved token.

## Non-goals

- No integration runner or adapter, no discovery, no HTTP call to any provider,
  and no collector-run execution engine. This change is the connection primitive,
  its secret resolution seam, and its read surface only. The collection-time failure
  outcome (the collector-scheduler's existing `error` status) is a contract the run
  engine will honour; this change does not build that engine, and it adds no
  first-class `Error` evidence-run result (the evidence result set stays closed to
  `{Pass, Fail}`).
- No secret-store retrieval (Vault, cloud secret manager, or equivalent). V1
  resolves the token from `IConfiguration` only; a secret store is backlog.
- No per-provider config-schema validation. Validating each provider's remaining
  free-form `config` block against a provider-supplied schema lands later with
  the provider-adapter registry, not here.
- No write surface for connections (they are git-authored, DB-read-only) and no
  edit UI.
- No new provider beyond `fleet`, and no speculative connection fields beyond
  what discovery attach and the read surface need.
