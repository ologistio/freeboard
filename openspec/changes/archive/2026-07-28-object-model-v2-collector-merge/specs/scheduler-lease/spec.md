## MODIFIED Requirements

### Requirement: MySQL schema and migration for the scheduler state

A forward-only migration SHALL create the `collector_scheduler_state` table with a
`collector_id` primary key (`VARCHAR(190)` `utf8mb4_bin`), a non-null `next_due_at
DATETIME(6)`, a nullable `current_run_id CHAR(26)` `utf8mb4_bin` (a ULID, the future
idempotency key), the lease columns `lease_owner VARCHAR(190)` `utf8mb4_bin`, `lease_token
CHAR(26)` `utf8mb4_bin` (a ULID fencing token), `lease_expires_at DATETIME(6)`,
`lease_heartbeat_at DATETIME(6)`, the history columns `last_started_at`, `last_completed_at`,
`last_success_at`, `last_failure_at` (all nullable `DATETIME(6)`), a non-null `failure_count
INT` defaulting to 0, a nullable `last_error TEXT`, a non-null `status VARCHAR(16)`
`utf8mb4_bin` defaulting to `'pending'` (closed set `pending|running|ok|error|dead`), a
nullable `config_fingerprint CHAR(64)` `utf8mb4_bin`, and `created_at`/`updated_at
DATETIME(6)` each defaulting to `UTC_TIMESTAMP(6)` (with `updated_at ON UPDATE
UTC_TIMESTAMP(6)`), so an ensure insert cannot fail on the NOT NULL timestamps. It SHALL index
the claim path (at least `next_due_at`). It SHALL have no foreign key to the collector table,
before or after the collector merge: `collector_id` stays a scalar column so scheduler state
survives a collector being deleted by gitops churn, and the collector-merge migration SHALL
NOT add one. The migration SHALL be replay-safe using `CREATE TABLE IF NOT EXISTS`.

#### Scenario: Migration creates the scheduler-state table

- **WHEN** the migration runner applies the collector-scheduler migration to a database at
  the prior version
- **THEN** the `collector_scheduler_state` table exists with the columns and claim index
  above (including `status`, `config_fingerprint`, and the ULID `current_run_id`/`lease_token`
  columns), has no foreign key to the collector table, and the migration is re-runnable
  without error

#### Scenario: The collector merge adds no foreign key to scheduler state

- **WHEN** the collector-merge migration completes
- **THEN** `collector_scheduler_state.collector_id` still carries no foreign key, so a row
  left behind for a deleted collector neither blocks a delete nor is cascaded away
