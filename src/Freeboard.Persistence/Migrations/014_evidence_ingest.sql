-- Add runtime Evidence ingest: the immutable evidence_runs (with nested evidence_run_checks) an
-- authenticated collector POSTs, plus the per-collector machine credential collector_credentials.
-- Additive and forward-only; alters no existing table. Ids use CHAR(26) ULID; snapshot/id columns
-- that mirror GitOps config ids use utf8mb4_bin to match Core's exact-byte id identity.
--
-- evidence_runs deliberately holds NO foreign key to evidence_collectors, controls, or vendors: a
-- GitOps sync hard-deletes pruned collectors, and compliance evidence history must survive a
-- collector/control refactor. Instead the ingest endpoint snapshots collector_title/control_id/
-- vendor_id/collector_type at write time. request_body_sha256 is the SHA-256 of the exact request
-- body, so the unique (collector_id, run_id) key distinguishes an identical replay from a changed
-- body. The count columns are raw derived summaries (hard/soft fail counts and total), never a
-- rollup verdict; categorical rollup stays the scoring engine's job. metadata and check data use
-- MySQL's native JSON type, which validates well-formedness at write.
--
-- collector_credentials DOES foreign-key evidence_collectors ON DELETE CASCADE: a credential is live
-- config, not history, so revoking it with the collector is correct. Only the keyed HMAC (token_hash)
-- and its key version are stored; the raw token is never persisted.

CREATE TABLE IF NOT EXISTS evidence_runs (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    collector_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    collector_title VARCHAR(512) NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    collector_type VARCHAR(32) NULL,
    run_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    schema_version VARCHAR(64) NOT NULL,
    collector_version VARCHAR(512) NULL,
    started_at DATETIME(6) NOT NULL,
    finished_at DATETIME(6) NOT NULL,
    received_at DATETIME(6) NOT NULL,
    request_body_sha256 BINARY(32) NOT NULL,
    hard_fail_count INT NOT NULL,
    soft_fail_count INT NOT NULL,
    total_count INT NOT NULL,
    metadata JSON NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_evidence_runs_collector_run (collector_id, run_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS evidence_run_checks (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    evidence_run_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    -- utf8mb4_bin so the (evidence_run_id, name) unique key is byte-exact, matching the ordinal
    -- uniqueness the ingest validator enforces; the default collation would collide names differing
    -- only by case/accent and surface a store failure instead of a clean 422.
    name VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    severity VARCHAR(8) NOT NULL,
    status VARCHAR(16) NOT NULL,
    detail TEXT NULL,
    data JSON NULL,
    seq INT NOT NULL,
    PRIMARY KEY (id),
    KEY ix_evidence_run_checks_run_id (evidence_run_id),
    UNIQUE KEY ux_evidence_run_checks_run_name (evidence_run_id, name),
    CONSTRAINT fk_evidence_run_checks_run
        FOREIGN KEY (evidence_run_id) REFERENCES evidence_runs (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS collector_credentials (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    collector_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NULL,
    expires_at DATETIME(6) NULL,
    revoked_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_collector_credentials_token_hash (token_hash),
    KEY ix_collector_credentials_collector_id (collector_id),
    CONSTRAINT fk_collector_credentials_collector
        FOREIGN KEY (collector_id) REFERENCES evidence_collectors (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
