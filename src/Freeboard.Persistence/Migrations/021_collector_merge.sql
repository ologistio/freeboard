-- Merge evidence_collectors and attestation_templates into one collectors table. A collector attaches a
-- proving mechanism - a data source or an attestation form - to one control. Every type-specific payload
-- lives in one config JSON column, so there is no checks column and registering a key for a new provider
-- is a JSON change rather than a migration.
--
-- Forward-only and not atomically replay-safe: DDL implicit-commits per statement and schema_migrations
-- is recorded only after the whole file succeeds. Recovery is restore-and-rerun.
--
-- Requires MySQL 8.0.17 or later: step 1 calls JSON_SCHEMA_VALID inside an enforced CHECK.
--
-- Dump both source tables before running. evidence_collectors.config is the only remaining copy of any
-- free-form config keys an operator authored, and step 3 does not carry them across.
--
-- `freeboard gitops sync` is a required follow-on step. Until it runs, a migrated attestation carries a
-- placeholder frequency, and an attestation authored as a collector-plus-template pair on one control is
-- two rows that the sync collapses.

-- 1. Refuse source rows the merged contract cannot hold, before anything is created, so a refusal needs
--    no cleanup. Each guard is dropped as soon as it has run: nothing else writes these tables here, and
--    leaving none behind on either outcome keeps the file replayable after a repair.
--
--    checks IS NOT NULL is load-bearing. A SQL NULL makes JSON_SCHEMA_VALID return NULL, which makes the
--    branch UNKNOWN, and an UNKNOWN CHECK counts as satisfied.
--
--    The schema pins item and member types only. Requiredness, token sets, and uniqueness belong to the
--    config registry and the validator; these are the shapes that fail the typed read. Member names are
--    the stored PascalCase spelling.
ALTER TABLE evidence_collectors
    ADD CONSTRAINT ck_evidence_collectors_premerge_payload CHECK (
        (type =  'integration'
             AND checks IS NOT NULL
             AND JSON_SCHEMA_VALID(
                     '{"type":"array","minItems":1,"items":{"type":"object","properties":{"SourceKey":{"type":"string"},"Name":{"type":"string"},"Severity":{"type":"string"}}}}',
                     checks))
     OR (type <> 'integration' AND checks IS NULL AND connection_id IS NULL));
ALTER TABLE evidence_collectors DROP CHECK ck_evidence_collectors_premerge_payload;

--    The same rule for the two columns step 4 re-shapes. No minItems: neither list is required by the
--    merged rules, so an empty array is not a read failure. Options is typed because it is the one nested
--    member that is itself a collection.
ALTER TABLE attestation_templates
    ADD CONSTRAINT ck_attestation_templates_premerge_payload CHECK (
        (fields IS NULL
             OR JSON_SCHEMA_VALID(
                    '{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Label":{"type":"string"},"Type":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}',
                    fields))
    AND (quiz IS NULL
             OR JSON_SCHEMA_VALID(
                    '{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Prompt":{"type":"string"},"Answer":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}',
                    quiz)));
ALTER TABLE attestation_templates DROP CHECK ck_attestation_templates_premerge_payload;

-- 2. The unified table. Keyed on id alone - a control may have several collectors. Constraint names are
--    fresh because InnoDB names are schema-wide and the source tables' own constraints live until step 6.
--    ck_collectors_integration_provider holds the derive-a-provider invariant for every later importer
--    write, not only for this migration.
CREATE TABLE IF NOT EXISTS collectors (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    connection_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    type VARCHAR(32) NOT NULL,
    provider VARCHAR(32) NULL,
    frequency VARCHAR(16) NOT NULL,
    threshold INT NULL,
    config JSON NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_collectors_control_id (control_id),
    KEY ix_collectors_vendor_id (vendor_id),
    KEY ix_collectors_connection_id (connection_id),
    CONSTRAINT ck_collectors_integration_provider CHECK (type <> 'integration' OR provider IS NOT NULL),
    CONSTRAINT fk_collectors_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE RESTRICT,
    CONSTRAINT fk_collectors_vendor
        FOREIGN KEY (vendor_id) REFERENCES assets (id) ON DELETE RESTRICT,
    CONSTRAINT fk_collectors_connection
        FOREIGN KEY (connection_id) REFERENCES integration_connections (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 3. Copy evidence_collectors, joining integration_connections to derive the provider. Every foreign key
--    column is named explicitly: provider comes from the join, so a dropped connection_id would leave an
--    integration collector with no connection and ck_collectors_integration_provider would not catch it.
--
--    The CASE around JSON_OBJECT is required, not defensive: JSON_OBJECT writes a JSON null for a NULL
--    value rather than returning NULL, and a NULL config beside a non-NULL checks is the common shape.
--
--    An integration row with no connection fails here on ck_collectors_integration_provider. Repair the
--    row, DROP TABLE collectors, and re-run; that drop is valid only before step 5.
INSERT INTO collectors
    (id, api_version, title, control_id, vendor_id, connection_id, type, provider, frequency, threshold,
     config, created_at, updated_at)
SELECT ec.id, ec.api_version, ec.title, ec.control_id, ec.vendor_id, ec.connection_id,
    CASE ec.type
        WHEN 'manual-attestation' THEN 'manual'
        WHEN 'training-attestation' THEN 'training'
        ELSE ec.type
    END,
    CASE WHEN ec.type = 'integration' THEN ic.provider ELSE NULL END,
    ec.frequency, ec.threshold,
    CASE WHEN ec.checks IS NULL THEN NULL ELSE JSON_OBJECT('Checks', ec.checks) END,
    ec.created_at, ec.updated_at
FROM evidence_collectors ec
LEFT JOIN integration_connections ic ON ic.id = ec.connection_id;

-- 4. Copy attestation_templates, folding the four form columns under their config member names.
--    JSON_MERGE_PATCH deletes null-valued members, so an absent member is omitted rather than stored as a
--    JSON null, and the outer CASE yields SQL NULL when all four are NULL because an empty config is SQL
--    NULL rather than {}. pass_mark is carried as the INT the column holds so it stores as a JSON number.
--
--    frequency is a placeholder the next sync overwrites: attestation_templates has no cadence column.
--
--    An id shared with an evidence_collectors row aborts here on the primary key. Both legacy tables are
--    intact; rename one id in the authored config, sync, DROP TABLE collectors, and re-run.
INSERT INTO collectors
    (id, api_version, title, control_id, vendor_id, connection_id, type, provider, frequency, threshold,
     config, created_at, updated_at)
SELECT tpl.id, tpl.api_version, tpl.title, tpl.control_id, NULL, NULL, tpl.type, NULL, 'annual', NULL,
    CASE
        WHEN tpl.body IS NULL AND tpl.fields IS NULL AND tpl.pass_mark IS NULL AND tpl.quiz IS NULL
            THEN NULL
        ELSE JSON_MERGE_PATCH(
                 JSON_OBJECT(),
                 JSON_OBJECT('Body', tpl.body, 'Fields', tpl.fields, 'PassMark', tpl.pass_mark,
                             'Quiz', tpl.quiz))
    END,
    tpl.created_at, tpl.updated_at
FROM attestation_templates tpl;

-- 5. Re-point the collector credential foreign key. This must precede step 6 or the drop fails on the
--    live constraint. collector_scheduler_state and evidence_runs hold scalar collector ids with no
--    foreign key by design, so past runs survive a prune.
ALTER TABLE collector_credentials DROP FOREIGN KEY fk_collector_credentials_collector;
ALTER TABLE collector_credentials
    ADD CONSTRAINT fk_collector_credentials_collector
        FOREIGN KEY (collector_id) REFERENCES collectors (id) ON DELETE CASCADE;

-- 6. Drop the legacy tables.
DROP TABLE evidence_collectors;
DROP TABLE attestation_templates;
