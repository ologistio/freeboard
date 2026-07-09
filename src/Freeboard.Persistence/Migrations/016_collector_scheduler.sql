-- Per-collector scheduler state for the in-service collector scheduler. One mutable row per integration
-- collector holds its schedule (next_due_at), the in-flight run (current_run_id), the lease
-- (lease_owner/lease_token/lease_expires_at/lease_heartbeat_at), and health/history fields. The web app
-- claims due rows across replicas with SELECT ... FOR UPDATE SKIP LOCKED, so exactly one worker runs a
-- collector and a stuck collector holds only its own row. All leasing time is compared on the database
-- clock (UTC_TIMESTAMP(6)), so cross-replica clock skew cannot corrupt claiming.
--
-- current_run_id and lease_token are ULIDs (CHAR(26) utf8mb4_bin), matching the repo id convention:
-- current_run_id is the stable run token a crash retry reuses (assigned only when null via COALESCE) so
-- the future runner can key an idempotent append on it; lease_token is the fencing token minted on every
-- claim, so a worker that lost its lease affects 0 rows on any post-claim write. status is a closed set
-- {pending, running, ok, error, dead}; dead is the terminal dead-letter state after MaxAttempts failures
-- and is excluded from claiming until a config-change revival (config_fingerprint) or a manual reset.
--
-- config_fingerprint is a hash over the scheduling-relevant config (frequency + type); a change revives a
-- dead/error row. created_at/updated_at auto-default (updated_at auto-updates) so the ensure upsert never
-- has to supply them. MySQL permits only CURRENT_TIMESTAMP as a DATETIME default/ON UPDATE function, so
-- these two audit-only columns use it; they never drive leasing, which always compares UTC_TIMESTAMP(6).
--
-- No foreign key to evidence_collectors: the state must survive GitOps churn or deletion of a collector,
-- consistent with the scalar-id convention used by evidence_runs. A row for a removed or type-changed
-- collector simply lingers and is never claimed (excluded by the active-collector filter in the service).
--
-- Replay-safe: CREATE TABLE IF NOT EXISTS, so a crash between the DDL and the schema_migrations version
-- record is re-runnable without manual recovery. No seed row: rows are ensured lazily by the service.

CREATE TABLE IF NOT EXISTS collector_scheduler_state (
    collector_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    next_due_at DATETIME(6) NOT NULL,
    current_run_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    lease_owner VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    lease_token CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    lease_expires_at DATETIME(6) NULL,
    lease_heartbeat_at DATETIME(6) NULL,
    last_started_at DATETIME(6) NULL,
    last_completed_at DATETIME(6) NULL,
    last_success_at DATETIME(6) NULL,
    last_failure_at DATETIME(6) NULL,
    failure_count INT NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    status VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT 'pending',
    config_fingerprint CHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (collector_id),
    KEY ix_collector_scheduler_state_next_due (next_due_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
