-- Add the machine dimension, the collection cycle, and the error result to evidence runs.
-- asset_id names the machine asset a run describes. It is null for a run that describes the
-- organisation as a whole. cycle_id records the scheduler's collection cycle. error_detail
-- carries why an errored collection failed. See the evidence-persistence capability for the
-- rules these columns serve.
--
-- asset_id carries no foreign key, following organisation_id and collector_id. Evidence is
-- append-only history, so a retired machine must not block an append or cascade a run away.
--
-- The second idempotency key dedups a re-delivered collection cycle. It carries the generated
-- asset_key, not asset_id. MySQL treats each NULL in a unique index as distinct, so a key on
-- asset_id would not dedup an organisation-level run at all.
--
-- The legacy key uq_evidence_runs_vendor_collector_ref is kept unchanged. A cycle-keyed run
-- leaves vendor and collector_ref null, so the two key populations stay disjoint. The two check
-- constraints require exactly one identity per run, so exactly one key dedups it.
--
-- Compared literals declare COLLATE utf8mb4_0900_bin with the _utf8mb4 introducer. Only
-- utf8mb4_0900_bin is NO PAD, so it matches the application's ordinal comparison. utf8mb4_bin
-- is PAD SPACE and would read 'Error ' as 'Error'. The introducer fixes the literal's character
-- set, which a latin1 client would otherwise reject with error 1253.
--
-- OPERATIONAL. Run `SELECT DISTINCT result FROM evidence_runs;` first. MySQL validates recorded
-- rows against each new check, so a run outside {Pass, Fail} fails the whole ALTER. Investigate
-- such a value rather than migrating around it.
--
-- MySQL 8.4.10 refuses this statement under ALGORITHM=INPLACE, LOCK=NONE with error 1845 and
-- accepts it with no clause, so the server serves it with ALGORITHM=COPY. A copy BLOCKS every
-- concurrent write to evidence_runs for the whole rebuild, including every HTTP evidence
-- ingest. Schedule the blocking window, not only the elapsed time.
--
-- uq_evidence_runs_cycle also costs an index write on every later insert, including every
-- insert of a run that carries no cycle.
--
-- No row is backfilled and no trigger is dropped or re-created, so the append-only guarantee
-- holds throughout. Like 015 and 018 this migration is not atomically replay-safe. The runner
-- records the version only after the SQL succeeds, and ADD COLUMN is not idempotent. To
-- recover, drop the added columns, the added unique key, and the added check constraints and
-- re-run, or record the migration version by hand.

ALTER TABLE evidence_runs
    ADD COLUMN asset_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL AFTER requirement_id,
    ADD COLUMN cycle_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    ADD COLUMN error_detail TEXT NULL,
    ADD COLUMN asset_key VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
        GENERATED ALWAYS AS (COALESCE(asset_id, _utf8mb4'')) VIRTUAL,
    MODIFY vendor VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    MODIFY collector_ref VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    ADD UNIQUE KEY uq_evidence_runs_cycle (cycle_id, organisation_id, requirement_id, asset_key),
    ADD CONSTRAINT ck_evidence_runs_result CHECK (result IN (
        _utf8mb4'Pass' COLLATE utf8mb4_0900_bin,
        _utf8mb4'Fail' COLLATE utf8mb4_0900_bin,
        _utf8mb4'Error' COLLATE utf8mb4_0900_bin)),
    ADD CONSTRAINT ck_evidence_runs_error_detail CHECK (
        (result = _utf8mb4'Error' COLLATE utf8mb4_0900_bin
            AND error_detail IS NOT NULL AND TRIM(error_detail) <> _utf8mb4'')
        OR (result <> _utf8mb4'Error' COLLATE utf8mb4_0900_bin AND error_detail IS NULL)),
    ADD CONSTRAINT ck_evidence_runs_ref_pair CHECK (
        (vendor IS NULL AND collector_ref IS NULL)
        OR (vendor IS NOT NULL AND collector_ref IS NOT NULL)),
    ADD CONSTRAINT ck_evidence_runs_cycle_identity CHECK (
        (vendor IS NOT NULL AND collector_ref IS NOT NULL AND cycle_id IS NULL)
        OR (collector_id IS NOT NULL AND TRIM(collector_id) <> _utf8mb4''
            AND cycle_id IS NOT NULL AND TRIM(cycle_id) <> _utf8mb4''
            AND vendor IS NULL AND collector_ref IS NULL));
