## ADDED Requirements

### Requirement: Runtime Evidence persistence

The system SHALL persist runtime Evidence in two dedicated MySQL tables, `evidence_runs`
and `evidence_run_checks`, created by a forward-only migration (`014`) that alters no
existing table. One collector run SHALL be one immutable `evidence_runs` row with its nested
`evidence_run_checks` rows; Evidence is append-only and SHALL NOT be updated after insert.

The `evidence_runs` table SHALL NOT hold a foreign key to any GitOps-managed config table
(`evidence_collectors`, `controls`, `vendors`). Compliance evidence history MUST survive a
GitOps sync that hard-deletes a pruned collector, control, or vendor, so collector identity
SHALL be snapshotted onto each run at ingest time as plain values, not referenced. The
`evidence_runs` table SHALL hold: an `id` (the existing ULID `CHAR(26)` form used for
sessions and users); a `collector_id` plain string (no FK) using `utf8mb4_bin` to match
Core's exact-byte id identity; snapshot columns `collector_title`, `control_id` (no FK,
`utf8mb4_bin`, NOT NULL because the source `evidence_collectors.control_id` is NOT NULL so an
ingest always has a control id to snapshot), `vendor_id` (no FK, `utf8mb4_bin`, nullable
because its source `evidence_collectors.vendor_id` is nullable), and `collector_type` copied
from the collector's `evidence_collectors` register row at ingest; a caller-supplied
`run_id`; a `schema_version`; an optional `collector_version`; a `started_at`; a
`finished_at`; a server-set `received_at`; a `request_body_sha256` (`BINARY(32)`) holding
the SHA-256 of the exact request body; the raw derived summary counts `hard_fail_count`,
`soft_fail_count`, and `total_count`; and an optional `metadata` JSON value.

The store SHALL NOT compute or persist a control-level or rollup compliance verdict at
ingest: `hard_fail_count`, `soft_fail_count`, and `total_count` are raw derived counts, not a
status. Categorical rollup remains the scoring engine's responsibility. `hard_fail_count`
SHALL count checks with `severity = hard` and `status = fail`; `soft_fail_count` SHALL count
checks with `severity = soft` and `status = fail`; `total_count` SHALL be the number of
checks in the run.

Each `evidence_run_checks` row SHALL hold: an `id`; an `evidence_run_id` foreign key to
`evidence_runs` with `ON DELETE CASCADE`; a `name` (bounded length, unique within a run); a
`severity` (`hard`/`soft`); a `status` (`pass`/`fail`/`unknown`/`not_applicable`); an
optional `detail`; an optional `data` JSON value; and a `seq` preserving check order. The
Evidence write store SHALL live in `Freeboard.Persistence` and add no database dependency to
`Freeboard.Core`.

#### Scenario: Fresh database gains the evidence tables

- **WHEN** migrations are applied to a fresh database through `014`
- **THEN** the `evidence_runs` table exists with its primary key, the idempotency unique key
  `(collector_id, run_id)`, its snapshot columns, and its summary count columns, and holds no
  foreign key to `evidence_collectors`, `controls`, or `vendors`; and the
  `evidence_run_checks` table exists with its primary key, `evidence_run_id` foreign key to
  `evidence_runs` with `ON DELETE CASCADE`, and its `severity`, `status`, `detail`, `data`,
  and `seq` columns

#### Scenario: A run persists as one evidence row with its checks and snapshot

- **WHEN** a collector run with several checks is written through the Evidence write store
- **THEN** one `evidence_runs` row is persisted with its `collector_id`, snapshot
  `collector_title`/`control_id`/`vendor_id`/`collector_type`, `run_id`, `started_at`,
  `finished_at`, `received_at`, `request_body_sha256`, and derived counts, and one
  `evidence_run_checks` row per check is persisted with its `name`, `severity`, `status`,
  `detail`, `data`, and `seq`

#### Scenario: Evidence history survives collector removal

- **WHEN** a GitOps sync hard-deletes a collector's `evidence_collectors` row after evidence
  runs for it exist
- **THEN** the `evidence_runs` rows for that collector remain, because they hold no foreign
  key to `evidence_collectors` and carry the collector identity as a snapshot

#### Scenario: Summary counts are raw counts, not a verdict

- **WHEN** a run includes checks of mixed severity and status
- **THEN** the persisted `hard_fail_count`, `soft_fail_count`, and `total_count` are the raw
  counts of matching checks and no control-level or rollup status is computed or stored at
  ingest

### Requirement: Evidence idempotency and immutability

The `evidence_runs` table SHALL enforce a unique key on `(collector_id, run_id)` so a
re-POST of the same run does not create a second row. The write store SHALL be append-only
within one DML transaction: it SHALL insert the `evidence_runs` row and all its
`evidence_run_checks` rows together, and on a duplicate `(collector_id, run_id)` it SHALL NOT
insert a second row or mutate the first. The store SHALL report, for a duplicate, whether the
stored `request_body_sha256` matches the incoming one, so the endpoint can distinguish a
safe replay from a conflicting body. A partial failure SHALL leave the store in its prior
state, not a half-written run.

The write result SHALL surface the persisted run's `received_at`, `hard_fail_count`,
`soft_fail_count`, and `total_count` alongside the Evidence id and the new-vs-duplicate and
body-matches flags, so the endpoint can build the response body without a second read. For a
new insert these are the values just written; for a same-body duplicate (a replay) these are
the ORIGINAL stored values of the existing run, read back within the same transaction, so a
replay response reports the original run's `received_at` and counts, not values derived from
the replayed request.

#### Scenario: Duplicate run with the same body is a no-op returning the existing id

- **WHEN** a run whose `(collector_id, run_id)` already exists is written again with an
  identical `request_body_sha256`
- **THEN** the store returns the existing Evidence id with the existing run's `received_at` and
  summary counts, reports the body as matching, and does not insert a second `evidence_runs` row
  or add or change any `evidence_run_checks` row

#### Scenario: Duplicate run with a different body is reported as a conflict

- **WHEN** a run whose `(collector_id, run_id)` already exists is written again with a
  different `request_body_sha256`
- **THEN** the store does not insert or mutate anything and reports the body as conflicting so
  the endpoint can reject it

#### Scenario: Failed write does not partially apply

- **WHEN** an Evidence write fails partway through inserting its checks
- **THEN** the store reflects its prior state and no `evidence_runs` row for that run remains

### Requirement: Per-collector credential persistence

The system SHALL persist per-collector machine credentials in a dedicated MySQL table
`collector_credentials`, created by the same forward-only migration, that alters no existing
table. Each row SHALL hold an `id`, a `collector_id` foreign key to `evidence_collectors`
with `ON DELETE CASCADE` (removing a collector revokes its credentials), a `token_hash`
(`BINARY(32)`, unique) holding the keyed HMAC-SHA256 of the credential secret, a
`token_key_version`, a `created_at`, a nullable `last_seen_at`, a nullable `expires_at`, and a
nullable `revoked_at`. The `collector_id` column SHALL use `utf8mb4_bin`. Unlike
`evidence_runs`, this table MAY foreign-key to `evidence_collectors`: a credential is live
config, not compliance history, so cascading its deletion with the collector is correct. The
store SHALL support looking a credential up by token hash (for authentication), issuing a
credential (insert), revoking one (set `revoked_at`), and a best-effort last-seen touch that
updates `last_seen_at` for a credential. The `last_seen_at` column SHALL be written on a
successful collector authentication (mirroring the session store's last-seen update), so the
column reflects real collector activity and is not dead schema; a failure of that update SHALL
NOT fail the authenticated request. The raw token SHALL never be stored.

#### Scenario: Fresh database gains the collector_credentials table

- **WHEN** migrations are applied to a fresh database through `014`
- **THEN** the `collector_credentials` table exists with its primary key, its unique
  `token_hash`, its `token_key_version`, its `collector_id` foreign key to
  `evidence_collectors` with `ON DELETE CASCADE`, and its nullable `last_seen_at`,
  `expires_at`, and `revoked_at` columns

#### Scenario: Lookup resolves a live credential by token hash

- **WHEN** the store looks up a credential by the keyed HMAC of a presented secret and the
  matching row has a null `revoked_at` and no elapsed `expires_at`
- **THEN** the store returns the credential with its `collector_id`, `token_key_version`,
  `expires_at`, and `revoked_at`

#### Scenario: Successful authentication touches last-seen

- **WHEN** a collector credential authenticates successfully and the store's last-seen touch is
  invoked for it
- **THEN** the credential's `last_seen_at` is updated to the authentication time, and a failure
  of that touch does not fail the authenticated request

#### Scenario: Deleting a collector removes its credentials

- **WHEN** a collector is removed and its `evidence_collectors` row is deleted
- **THEN** its `collector_credentials` rows are removed by the cascade
