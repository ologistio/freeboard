-- Record collector identity and cadence on each evidence run so staleness can be evaluated from the
-- evidence tables alone (no join into the mutable collector config). collector_id is the authenticated
-- collector that produced the run; frequency is the collection cadence in force when the run arrived.
-- Both are nullable and additive: pre-migration rows read back NULL on both, are attributed by the
-- collector_ref prefix fallback (so their last verdict still shows), and are never flagged stale for
-- lack of a cadence. Ids use utf8mb4_bin to match Core's exact-byte id identity.
--
-- Purely additive and forward-only: a single ALTER TABLE adding two nullable columns, matching the repo
-- idiom (migrations 005, 012 use bare ADD COLUMN). No foreign key on collector_id (evidence must survive
-- GitOps churn or deletion of a collector, consistent with the scalar organisation_id/requirement_id
-- columns). No new index: the existing ix_evidence_runs_org_requirement_collected (organisation_id,
-- requirement_id, collected_at) already serves the batch read via its leftmost (organisation_id,
-- requirement_id) prefix, and per-collector grouping is done in the read store after the fetch, so a
-- fourth index would only add write cost on this append-hot table. No backfill and no touching of
-- trg_evidence_runs_no_update, so the append-only integrity guarantee holds through the migration.
--
-- NOT atomically replay-safe: the runner runs this SQL and only then records the schema_migrations
-- version, and plain ADD COLUMN is not idempotent (MySQL 8.4 has no ADD COLUMN IF NOT EXISTS). If a
-- crash lands after the ALTER commits but before the version is recorded, a re-run fails on the
-- duplicate column. Operational recovery: drop the added collector_id/frequency columns and re-run
-- `freeboard system migrate`, or record the schema_migrations row for version 015 by hand.

ALTER TABLE evidence_runs
    ADD COLUMN collector_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    ADD COLUMN frequency VARCHAR(16) NULL;
