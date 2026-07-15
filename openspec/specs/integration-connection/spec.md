# integration-connection Specification

## Purpose

Model a provider integration as a first-class GitOps kind - an `IntegrationConnection` that an
`EvidenceCollector` of `type: integration` references - so discovery and integration collection
share one persisted, referentially-consistent connection record. The connection's API token is
resolved out-of-band from configuration (never git-tracked, persisted, or logged) and surfaced
only as a `tokenResolvable` health flag; an unresolvable token warns once at startup and fails
its scheduled collection as a scheduler error rather than a masked evidence result. Web, HTTP
API, and CLI read surfaces expose the connection subset and its token health without ever
returning the token value.

## Requirements
### Requirement: IntegrationConnection persistence and read model

The system SHALL persist integration-connections in MySQL via a forward-only
migration applied by `freeboard system migrate`, following the global (not
org-scoped) GitOps-kind table pattern. The migration SHALL create an
`integration_connections` table keyed on `id` (an authored id in an exact-byte
`utf8mb4_bin` column) carrying `api_version`, `title`, `provider`, `base_url`,
`discovery_cadence`, an optional `vendor_id`, and `created_at`/`updated_at` timestamps,
with a `RESTRICT` foreign key from `vendor_id` to `vendors(id)`. `api_version` and
`title` are `NOT NULL`: every GitOps kind authors both and every kind table persists
them, so the connection follows that pattern. The migration SHALL add
a nullable `connection_id` column (a `RESTRICT` foreign key to
`integration_connections(id)`) and a `checks` JSON column to `evidence_collectors`.
The migration SHALL NOT rewrite existing `evidence_collectors` rows (they read the
new columns as absent).

The importer SHALL upsert integration-connections by id after vendors and before
evidence-collectors, SHALL write an integration collector's `checks` list as a
JSON array on its row, and SHALL hard-remove an absent integration-connection in
foreign-key-safe order (after any referencing evidence-collector is pruned and
before any referenced vendor is pruned).

The read model for an integration-connection SHALL expose a deliberate subset of the
persisted columns - `id`, `provider`, `base_url`, `discovery_cadence`, and `vendor` -
and SHALL omit the persisted `api_version` and `title`, which are persisted for pattern
consistency but not surfaced. It SHALL NOT carry any token or token-derived state; the
token-resolvable health flag is composed at read time (see the token-resolution
requirement) and never stored.

This code SHALL live in the MIT `Freeboard.Persistence` project and SHALL NOT
reference `Freeboard.Enterprise` or add any new dependency.

#### Scenario: Migration applies cleanly on a fresh database

- **WHEN** `freeboard system migrate` runs against a database migrated to the prior
  ordinal
- **THEN** the migration applies successfully, the `integration_connections` table
  exists, and `evidence_collectors` has a nullable `connection_id` column and a
  `checks` column

#### Scenario: Connection round-trips with persisted fields only

- **WHEN** a valid config with an integration-connection is imported and read back
- **THEN** the read model returns the connection's `id`, `provider`, `base_url`,
  `discovery_cadence`, and `vendor`, and carries no token or token-derived field

### Requirement: Out-of-band API token resolution

The system SHALL resolve an integration-connection's API token out-of-band from
`IConfiguration`, keyed by the connection instance id at the configuration key
`Freeboard:Integrations:<id>:ApiToken` (supplied by environment variables,
user-secrets, or another configuration provider). The token SHALL NEVER be read
from git-tracked config, SHALL NEVER be stored in an `EvidenceCollector.config`
map, SHALL NEVER be persisted to the database, and SHALL NEVER be written to any
log or read surface.

The system SHALL expose the token only as a resolvability check: a
`tokenResolvable` health flag that is true when the keyed configuration value is
present and non-blank, and false otherwise. This flag SHALL be composed at read
time and SHALL be the only token-derived value any read surface exposes; the raw
token value SHALL NOT be returned by the web read view, the HTTP API, or the CLI.

#### Scenario: Token resolves from configuration by connection id

- **WHEN** the configuration provides a non-blank value at
  `Freeboard:Integrations:<id>:ApiToken` for a connection `<id>`
- **THEN** that connection's `tokenResolvable` health flag is true

#### Scenario: Missing token is not resolvable

- **WHEN** the configuration provides no value (or a blank value) at
  `Freeboard:Integrations:<id>:ApiToken` for a connection `<id>`
- **THEN** that connection's `tokenResolvable` health flag is false

#### Scenario: Token value is never exposed

- **WHEN** a connection is read through the web view, the HTTP API, or the CLI
- **THEN** the response carries the `tokenResolvable` flag but never the token value,
  and no persisted row or log line contains the token

### Requirement: Unresolvable token warns at startup and fails collection as a scheduler error

The system SHALL warn once at startup for each integration-connection that is
referenced by an `EvidenceCollector` of `type: integration` and whose token is not
resolvable. The warning SHALL name the connection id and SHALL NOT include the
token value. An unresolvable token SHALL NOT be a boot gate: the application SHALL
start regardless.

At collection time, a collector whose connection has no resolvable token SHALL fail
its scheduled dispatch rather than silently succeeding or crashing the scheduler. The
failure SHALL be recorded as the collector-scheduler's per-collector `error` status
(the existing failed-dispatch status set by the scheduler), NOT as a `Pass` or `Fail`
evidence run - the evidence run and check `result` set is closed to `{Pass, Fail}`, so
an unresolvable token is not masked as a `Fail` run. The token value SHALL NOT appear in
the recorded failure, its error detail, or any log. Building the runner that fails the
dispatch is out of scope for this change; this requirement fixes the contract the runner
SHALL honour.

#### Scenario: Referenced connection with no token warns at startup

- **WHEN** the application starts and a referenced integration-connection has no
  resolvable token
- **THEN** a startup warning naming the connection id is logged, the token value is
  not logged, and the application boots

#### Scenario: Collector with an unresolvable token fails as a scheduler error

- **WHEN** a collector attached to a connection whose token is unresolvable is
  collected
- **THEN** its scheduled dispatch fails and is recorded as the scheduler's `error`
  status, not as a `Pass` or `Fail` evidence run, and the token value appears in no log
  or stored field

### Requirement: Integration-connection web read view

The web app SHALL serve a read-only integration-connections page that lists each
connection with its `provider`, `base_url`, `discovery_cadence`, and its
`tokenResolvable` health flag composed at read time. The page SHALL read through the
compliance store in-process, SHALL be GET-only and served in GitOps read-only mode,
and SHALL require an authenticated user: an anonymous browser GET SHALL redirect to
`/login`. When the store is unreachable the page SHALL render an in-page notice
rather than an error page. When no connections exist the page SHALL render an empty
state.

The system SHALL also expose an authenticated read-only HTTP endpoint that returns
the connection list as JSON (each item carrying `id`, `provider`, `base_url`,
`discovery_cadence`, `vendor`, and the composed `token_resolvable` flag, and never
the token value), so the CLI can read connections without direct database access.

#### Scenario: Connections page lists connections with health

- **WHEN** an authenticated user opens the integration-connections page and
  connections exist
- **THEN** the page lists each connection's provider, base URL, discovery cadence, and
  token-resolvable health, and never the token value

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests the integration-connections page
- **THEN** the response redirects to `/login`

#### Scenario: Store outage renders a notice

- **WHEN** the compliance store is unreachable while rendering the page
- **THEN** the page shows an in-page notice rather than an error page

### Requirement: Integration-connection CLI list command

The CLI SHALL provide a `connections list` command that reads the
integration-connection list through the HTTP API, not by direct database access,
using the configured API base URL and admin token. It SHALL display each
connection's provider, base URL, discovery cadence, and token-resolvable health,
and SHALL NOT display the token value. The command SHALL follow the CLI exit-code
convention: `0` on success, `1` on a validation response, and `3` on an operational
failure (unauthorized, forbidden, server error, or connection failure). The command
SHALL live in the community, cross-platform `Freeboard.CLI` and SHALL NOT reference
`Freeboard.Enterprise` or reach the database directly.

#### Scenario: connections list prints connections with health

- **WHEN** the user runs `connections list` against a reachable, authenticated API
  with connections present
- **THEN** the command prints each connection's provider, base URL, discovery cadence,
  and token-resolvable health, and exits `0`

#### Scenario: connections list reports an operational failure

- **WHEN** the user runs `connections list` and the API is unreachable or rejects the
  admin token
- **THEN** the command prints an error and exits `3`

