-- Runtime evidence: the dynamic state a collector or an attestation questionnaire produces to show
-- whether an in-scope requirement is met. evidence_runs holds a run's identity; evidence_checks holds
-- its named checks; attestation_responses is the 1:1 extension for attestation-kind runs. Ids and FK
-- columns use utf8mb4_bin to match Core's exact-byte id identity.
--
-- Append-only: a recorded run is never mutated. BEFORE UPDATE and BEFORE DELETE triggers on all three
-- tables SIGNAL, so a stray UPDATE/DELETE is rejected even from a raw SQL client. INSERT is unaffected,
-- so appending a run plus its checks plus an optional attestation row in one transaction works normally.
-- This is a DML-level guarantee; DDL (DROP/TRUNCATE) does not fire row triggers, so throwaway test
-- databases still drop cleanly and a controlled purge, if ever needed, is a deliberate future migration.
--
-- External refs (evidence_runs.organisation_id, evidence_runs.requirement_id,
-- attestation_responses.user_id) are scalar id columns with NO foreign key, following the
-- authz_audit_events precedent: evidence is durable append-only history and must survive an
-- organisation/requirement/user delete. A strict RESTRICT FK would also wedge the GitOps importer's
-- prune, which the append-only delete trigger leaves no way to satisfy. They are indexed for the read
-- path. Internal refs among the new tables (evidence_checks.evidence_id, attestation_responses.evidence_id)
-- ARE enforced as FKs; the delete trigger blocks the delete in practice, the FK is a model backstop.
--
-- Idempotency: UNIQUE (vendor, collector_ref) dedups a re-delivered observation. collector_ref is the
-- vendor's stable id for THIS observation/submission (a per-run key), not an id of the collector source,
-- so a daily collector still writes distinct rows and history is preserved. Both columns are NOT NULL and
-- utf8mb4_bin so the key dedups by exact case-sensitive bytes (MySQL treats NULL as distinct).
--
-- The migration runner replays a partially-failed migration (the version stays unrecorded on partial
-- failure), so every statement is idempotent/re-runnable: CREATE TABLE IF NOT EXISTS for the tables and
-- DROP TRIGGER IF EXISTS before each single-statement CREATE TRIGGER (no BEGIN ... END body, so the
-- runner's statement batching is not affected).
--
-- Precondition: this is the first migration to run CREATE TRIGGER. On a stock binary-logging MySQL 8.x
-- the migration database user must either connect to a server started with
-- log_bin_trust_function_creators=1 or hold a privilege sufficient to create triggers under binary
-- logging; otherwise the server rejects the create with error 1419 (ER_BINLOG_CREATE_ROUTINE_NEED_SUPER).
-- The dev/CI compose file sets --log-bin-trust-function-creators=1.

CREATE TABLE IF NOT EXISTS evidence_runs (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    kind VARCHAR(32) NOT NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    collector_ref VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    vendor VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    result VARCHAR(32) NOT NULL,
    collected_at DATETIME(6) NOT NULL,
    received_at DATETIME(6) NULL,
    raw_payload JSON NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_evidence_runs_vendor_collector_ref (vendor, collector_ref),
    KEY ix_evidence_runs_org_requirement_collected (organisation_id, requirement_id, collected_at),
    KEY ix_evidence_runs_requirement_id (requirement_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS evidence_checks (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    evidence_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    name VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    severity VARCHAR(16) NOT NULL,
    result VARCHAR(32) NOT NULL,
    ordinal INT NOT NULL,
    detail TEXT NULL,
    PRIMARY KEY (id),
    -- UNIQUE (evidence_id, name) also covers the evidence_id FK lookup via its leftmost prefix, so no
    -- separate (evidence_id) index is needed.
    UNIQUE KEY uq_evidence_checks_evidence_name (evidence_id, name),
    CONSTRAINT fk_evidence_checks_evidence
        FOREIGN KEY (evidence_id) REFERENCES evidence_runs (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS attestation_responses (
    evidence_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    quiz_passed TINYINT(1) NOT NULL,
    score INT NULL,
    PRIMARY KEY (evidence_id),
    KEY ix_attestation_responses_user_id (user_id),
    CONSTRAINT fk_attestation_responses_evidence
        FOREIGN KEY (evidence_id) REFERENCES evidence_runs (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP TRIGGER IF EXISTS trg_evidence_runs_no_update;
CREATE TRIGGER trg_evidence_runs_no_update BEFORE UPDATE ON evidence_runs
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';

DROP TRIGGER IF EXISTS trg_evidence_runs_no_delete;
CREATE TRIGGER trg_evidence_runs_no_delete BEFORE DELETE ON evidence_runs
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';

DROP TRIGGER IF EXISTS trg_evidence_checks_no_update;
CREATE TRIGGER trg_evidence_checks_no_update BEFORE UPDATE ON evidence_checks
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';

DROP TRIGGER IF EXISTS trg_evidence_checks_no_delete;
CREATE TRIGGER trg_evidence_checks_no_delete BEFORE DELETE ON evidence_checks
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';

DROP TRIGGER IF EXISTS trg_attestation_responses_no_update;
CREATE TRIGGER trg_attestation_responses_no_update BEFORE UPDATE ON attestation_responses
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';

DROP TRIGGER IF EXISTS trg_attestation_responses_no_delete;
CREATE TRIGGER trg_attestation_responses_no_delete BEFORE DELETE ON attestation_responses
    FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'evidence is append-only';
