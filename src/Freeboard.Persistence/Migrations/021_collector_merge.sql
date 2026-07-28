-- Merge evidence_collectors and attestation_templates into one `collectors` table. A collector row
-- attaches a proving mechanism - a data source or an attestation form - to one control. Every
-- type-specific payload moves into one `config` JSON column: the old evidence_collectors.checks column
-- and the old attestation_templates body/fields/pass_mark/quiz columns all become keys inside it, so
-- there is no `checks` column here and adding a key for a new provider is a JSON change rather than a
-- migration. Ids and FK columns use utf8mb4_bin to match Core's exact-byte id identity.
--
-- Forward-only and NOT atomically replay-safe, matching 015/018/019/020: MySQL DDL implicit-commits per
-- statement and the runner records schema_migrations only after the whole file succeeds, so a mid-file
-- crash cannot be recovered by a naive re-run. Pre-production hard cutover: operational recovery is
-- restore-and-rerun, which is always available because there is no data contract to preserve.
--
-- BEFORE RUNNING, DUMP evidence_collectors AND attestation_templates. The authored YAML remains the
-- source of truth for everything this file copies, with one exception: the dropped
-- evidence_collectors.config map is the only remaining copy of any free-form config keys an operator
-- authored, and this migration deliberately does not carry them across (see step 3). A dump is the only
-- way to read them back while hand-migrating.
--
-- SERVER FLOOR. Step 1 uses an enforced CHECK constraint (MySQL 8.0.16+) whose predicate calls
-- JSON_SCHEMA_VALID (8.0.17+), so this file requires MySQL 8.0.17 or later. Every version statement in
-- this repo names 8.4 and no support is claimed for anything older, so the floor moves by one patch
-- release and breaks nothing - but it does move, so it is stated rather than left implicit.
--
-- WHAT THIS MIGRATION REFUSES. Four classes of already-invalid source row, by three named check
-- constraints at two different moments:
--   1. A non-`integration` evidence_collectors row carrying a `checks` or a `connection_id`. Today's
--      validation rejects both on any non-integration collector, so such a row cannot come from a valid
--      authored document. Refused at step 1 on ck_evidence_collectors_premerge_payload, before anything
--      is created, so there is nothing to clean up before the re-run.
--   2. A `type: integration` evidence_collectors row whose `checks` is not a non-empty JSON array of
--      objects whose known members are well-typed. Refused at step 1 on the same constraint, with the
--      same nothing-to-clean-up property.
--   3. An attestation_templates row whose `fields` or `quiz` is not a JSON array of objects whose known
--      members are well-typed. Refused at step 1 on ck_attestation_templates_premerge_payload, likewise
--      before anything is created. Step 4 re-shapes both columns into `config` by exactly the mechanism
--      step 3 re-shapes `checks`, so the same shapes land the same unreadable column; guarding one and
--      not the other would leave the read-boundary rule true for integration collectors alone.
--   4. A `type: integration` evidence_collectors row that carries its `checks` but has a NULL
--      `connection_id` - a hand-edited row, since the pre-018 shape has no checks either and is already
--      refused by class 2. `connection` has been required on an integration collector since the
--      reference landed, so this too cannot come from a valid authored document. Refused at step 3 on
--      ck_collectors_integration_provider. This is the only REFUSAL that leaves a partially created
--      `collectors` table behind, so the repair is: fix the row, DROP TABLE collectors, re-run.
-- The repair for every refused row is the same and needs only the pre-merge app: run
-- `freeboard gitops sync` against the pre-merge schema, which rewrites the row from the authored
-- document, or delete the stale row.
--
-- WHAT THIS MIGRATION ASSUMES: that the evidence_collectors and attestation_templates id spaces are
-- DISJOINT. Unlike the four refusals above, a collision is REACHABLE FROM A VALID pre-merge config:
-- duplicate-id detection runs per kind (each of the two pre-merge validators opens its own seen-id set,
-- and no set spans both) and each legacy table has its own primary key, so an EvidenceCollector and an
-- AttestationTemplate sharing an id validates, syncs, and persists today. Step 4 then aborts on the
-- `collectors` primary key with an UNNAMED `ERROR 1062 Duplicate entry '<id>' for key
-- 'collectors.PRIMARY'` - a key name, not a rule name, unlike the four refusals. Both legacy tables are
-- intact and a partially created `collectors` table holds step 3's rows; the repair is to rename one of
-- the two ids in the AUTHORED config, run `gitops sync` against the pre-merge schema, DROP TABLE
-- collectors, and re-run. That drop is valid only because this abort precedes step 5's credential
-- re-point: after the re-point the live foreign key blocks it.
--
-- `freeboard gitops sync` IS A REQUIRED FOLLOW-ON STEP, NOT AN OPTIONAL ONE. Until it runs the merged
-- table holds three states no authored document backs: the placeholder `frequency` on every migrated
-- attestation, which every read surface displays; nothing where an operator had authored free-form
-- `config` keys, which are not carried across; and TWO collector rows for every attestation authored as
-- a collector-plus-template pair on one control, which the sync collapses by pruning whichever id the
-- hand-migrated document does not keep (see step 4).

-- 1. Guard BOTH source tables BEFORE the CREATE TABLE, so a refusal here leaves the schema completely
--    untouched and the re-run after a repair needs no cleanup. The first constraint is today's
--    type-conditional evidence-collector rule enforced at rest, at the LIST level, and it refuses TWO
--    classes, not one; the second applies the same read-boundary rule to the two attestation JSON
--    columns this migration re-shapes.
--
--    The non-integration branch is load-bearing because step 3 composes JSON_OBJECT('Checks', ec.checks)
--    and carries connection_id for EVERY row it copies, with no type test: without this branch a
--    `manual-attestation` row with a stray `checks` would land as a `manual` collector carrying a
--    `Checks` key that the (manual, -) config schema does not register, and a stray `connection_id`
--    would land as a `manual` collector with a connection the merged validation forbids.
--
--    The integration branch refuses a `checks` that is not a non-empty JSON array OF OBJECTS WITH
--    WELL-TYPED KNOWN MEMBERS, because each of those shapes lands a row the target contract cannot hold:
--    a SQL NULL copies as config = NULL where the (integration, fleet) schema makes `checks` required;
--    `[]` or a JSON null copies as a member the storage contract says must be omitted; a non-array value
--    copies as a `Checks` member the typed read cannot bind, which is a store-boundary exception and a
--    503 on every collector read; a non-object item (`[1]`, `["x"]`, `[null]`, `[[]]`) lands the same
--    outcome one level in, because a scalar item does not bind to a check at all and a JSON null item
--    binds to a list with a NULL element that the read surfaces dereference; and an object whose
--    SourceKey, Name, or Severity is present with a non-string JSON type lands it one level further in
--    still, because the deserializer IGNORES an unmatched property but THROWS on a present one it cannot
--    convert. SQL NULL is also the pre-018 shape - 018 added connection_id and checks in one ALTER TABLE
--    and rewrote no rows, so an integration row older than 018 has BOTH columns NULL - so that row is
--    refused HERE rather than later by ck_collectors_integration_provider, which is where a reader would
--    expect to find it.
--
--    The `checks IS NOT NULL` conjunct is NOT redundant: a SQL NULL makes JSON_SCHEMA_VALID return NULL,
--    which makes the whole branch UNKNOWN, and an UNKNOWN CHECK counts as SATISFIED - so without it the
--    pre-018 row would pass.
--
--    The item-type and member-type rules are read-boundary rules, not domain rules, which is why they
--    are here while the FIELD rules are not. The schema declares no `required`, so `[{"nope": 1}]` and
--    `[{}]` pass; no `enum`, so a Severity of "Banana" passes; and no uniqueness, so duplicate Name or
--    SourceKey values pass. Those all travel inside a value step 3 carries verbatim, they still bind on
--    read (a missing JSON member leaves the record's own default in place), and `gitops validate` names
--    each of them in the same terms before this migration and after it. That guarantee holds for an
--    OBJECT item whose present known members carry the right token type and for nothing else, and there
--    is no store-side normalization to catch the rest: the loader repairs a null check item on the
--    AUTHORING path, but the store reads its JSON columns with a bare deserialize and no converter. This
--    constraint is what confines the column to the shapes that bind.
--
--    THIS PREDICATE IS FINISHED; DO NOT TIGHTEN IT AGAIN. With the item's type and its three known
--    members' token types pinned, everything left to constrain is a domain rule the config registry and
--    the validator own. If a future edit finds it cannot express a read-boundary rule without also
--    encoding a domain rule, accept the residual and document it, or move the check into the sync step.
--
--    Do NOT instead add a type or shape test to step 3's expressions: that silently strips the value,
--    which is the disposition this merge already refused when it declined to carry the free-form config
--    map. The three member names are the STORED PascalCase spelling, matching what the column already
--    holds per item, not the authored snake_case one.
--
--    The constraint is added and immediately dropped because its whole job is done the instant the ALTER
--    succeeds - nothing else writes evidence_collectors during this migration - and dropping it keeps
--    the file replayable: a failed ADD leaves no constraint behind and a successful one leaves none
--    either, so a re-run after a repair cannot fail on a duplicate constraint name. It is NOT pinned
--    permanently on `collectors`, because which (type, provider) pair may carry a `checks` key, and
--    whether that key is required, are the config registry's rules: a permanent constraint would make a
--    future registry edit a migration.
ALTER TABLE evidence_collectors
    ADD CONSTRAINT ck_evidence_collectors_premerge_payload CHECK (
        (type =  'integration'
             AND checks IS NOT NULL
             AND JSON_SCHEMA_VALID(
                     '{"type":"array","minItems":1,"items":{"type":"object","properties":{"SourceKey":{"type":"string"},"Name":{"type":"string"},"Severity":{"type":"string"}}}}',
                     checks))
     OR (type <> 'integration' AND checks IS NULL AND connection_id IS NULL));
ALTER TABLE evidence_collectors DROP CHECK ck_evidence_collectors_premerge_payload;

--    The attestation half of the SAME read-boundary rule. Step 4 folds attestation_templates.fields and
--    .quiz into `config` by exactly the mechanism step 3 uses for checks - the column value is carried
--    across as an opaque JSON value - so `[1]`, `[null]`, `[[]]`, and `[{"Label": 123}]` in either column
--    reach the typed read and fail it the same way, taking every collector read to a 503 until a sync
--    rewrites the row. Guarding one re-shaped column and not the other two would make the read-boundary
--    guarantee hold for integration collectors alone.
--
--    Same scope, same terminus: each item must be a JSON OBJECT and each known member, when present, must
--    carry its declared JSON token type. Nothing else. No `required`, so an item missing Id/Label/Type or
--    Id/Prompt/Answer passes and the config validator names it as it always has; no `enum` on a field
--    type; no uniqueness on ids or options; and no `minItems`, because an empty array is not a read
--    failure - unlike an integration collector's checks, neither list is required by the merged rules, so
--    an empty one carries only the cosmetic defect of a member the storage contract would have omitted,
--    which the required sync rewrites. The nested `Options` list is typed because it is the one nested
--    member that is itself a collection: a non-string element does not bind, and a JSON null binds null
--    into a member every downstream projection reads as non-null.
--
--    This constraint also removes the last way step 4's outer CASE could be wrong. That CASE tests SQL
--    NULL on all four source columns, and a JSON null LITERAL is not SQL NULL - it would slip past the
--    test and then be deleted by JSON_MERGE_PATCH, landing config = '{}' where the storage contract says
--    an empty config is SQL NULL. Only `fields` and `quiz` are JSON columns (`body` is TEXT and
--    `pass_mark` is INT, neither of which can hold a JSON null), and an array schema rejects a JSON null,
--    so after this guard the CASE's IS NULL test is exact rather than approximate.
--
--    Like the constraint above it is added and immediately dropped: its job is done the instant the ALTER
--    succeeds, nothing else writes attestation_templates during this migration, and leaving no constraint
--    behind on either outcome keeps the file replayable after a repair.
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

-- 2. The unified table. Identity is keyed on id only - a control MAY have several collectors - so there
--    is no secondary unique key, carried forward from both source tables. The FK constraint names are
--    fresh (fk_collectors_*): InnoDB constraint names are schema-wide and the source tables' own
--    constraints live until step 6, so reusing a name would abort this CREATE. config uses MySQL's
--    native JSON type, which validates well-formedness at write. All three FKs are ON DELETE RESTRICT,
--    matching both source tables: the importer prunes referencing collectors before deleting a control,
--    a vendor asset, or a connection.
--
--    ck_collectors_integration_provider is the migration's refusal guard for a row that cannot derive a
--    provider (step 3), and it keeps that invariant true for every later importer write, which is what
--    earns it a permanent place here; 020's ck_scopes_single_target is the precedent. It is deliberately
--    one-directional: the reverse half - provider absent on the other four types - is owned by
--    validation, neither this migration nor the importer can violate it, and pinning it here would make
--    a future provider-bearing type a migration.
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

-- 3. Copy evidence_collectors, joining integration_connections to derive the provider. All three foreign
--    key columns are named explicitly - control_id and vendor_id from 012, connection_id from 018 -
--    because a shorthand drops connection_id and ck_collectors_integration_provider cannot catch that:
--    provider comes from the JOIN rather than from the carried column, so every integration collector
--    would land with a null connection, the startup unresolvable-token scan would see nothing to warn
--    about, and the schedule fingerprint would hash a null connection.
--
--    The config expression composes `Checks` for whatever row it copies and deliberately carries no type
--    test and no shape test of its own: step 1 has already refused every non-integration row with a
--    non-NULL checks, so a non-NULL checks here implies type = 'integration', and it has refused every
--    integration row whose checks is not a non-empty array of objects whose known members are
--    well-typed, so the composed `Checks` member is always a non-empty array whose items bind. Either
--    test here would silently strip a value this migration is meant to refuse. The CASE around
--    JSON_OBJECT is required rather than defensive: JSON_OBJECT writes a JSON null for a NULL value
--    instead of returning NULL, and a NULL config with a non-NULL checks is the common integration
--    shape. The key literal 'Checks' is the config model's member name, matching what
--    evidence_collectors.checks already stores per item, so the column value is carried across verbatim.
--
--    The old free-form evidence_collectors.config map is NOT carried. It accepts any ad-hoc key, so
--    persisting it would seed the new closed column with unregistered and possibly credential-shaped
--    keys on day one; and every value in it is a JSON string, so a carried key colliding with a typed
--    config member (a PassMark of "90", or a key named for the checks/fields/quiz list) would fail the
--    typed read of the column and take every collector read to a 503 until a sync repaired the row.
--    Operators hand-migrate those keys into their authored documents.
--
--    EXPECT THIS STEP TO FAIL on ck_collectors_integration_provider when a source row is
--    type = 'integration' with a NULL connection_id AND a checks that passed step 1 - a hand-edited row,
--    since the pre-018 shape has no checks either and step 1 has already refused it. That is the
--    intended refusal, not a defect: such a row is already invalid under the pre-merge rules, the
--    operator repairs it with a `gitops sync` against the pre-merge schema or by deleting it, and
--    because this is the first copy step nothing has been dropped when it fails. Of the four refusals
--    this is the only one that leaves a partially created `collectors` table to drop before the re-run
--    (the id collision in step 4 is not a refusal but leaves one too), and that drop is valid only
--    before step 5's credential re-point; afterwards the live foreign key blocks it. Do not "fix" the
--    guard away.
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
--    attestation_templates.type is already 'manual' or 'training', and carrying it verbatim is what
--    keeps ck_collectors_integration_provider from firing on this path. JSON_MERGE_PATCH's merge-patch
--    semantics delete every null-valued member, so an absent member is omitted rather than written as a
--    JSON null; the outer CASE then yields SQL NULL when all four source columns are NULL, because the
--    storage contract says an empty config is SQL NULL, not {}. That is the same JSON NULL-boundary
--    defect the CASE in step 3 exists to avoid, one level further out. The IS NULL test is exact, not
--    approximate: a JSON null LITERAL would slip past it and then be deleted by the merge patch, landing
--    '{}', and step 1's attestation guard is what makes that unreachable - only fields and quiz are JSON
--    columns and an array schema rejects a JSON null. pass_mark is carried as the INT
--    the source column holds so it stores as a JSON number, matching what the importer writes and what
--    the read model binds to; it is not cast to a string.
--
--    ONE RESIDUAL IS KNOWN AND ACCEPTED: an empty-string body (SQL '', not NULL) is not a null member,
--    so it survives as {"Body": ""} where the storage contract says omit a blank body. It is
--    unreachable from a synced database - the importer writes NULL for a blank body - it is inert
--    downstream, and the required sync clears it. Step 1's predicates are deliberately not widened to
--    catch a value only a hand edit can produce.
--
--    frequency is written as 'annual', a PLACEHOLDER the next `gitops sync` overwrites.
--    attestation_templates has no cadence column, and frequency is required on every merged collector,
--    so there is no value to carry. The sync is required rather than optional because until it runs the
--    read API, the register page, and the CLI listing all display a cadence no authored document backs.
--    The SoA drill-down does not: a migrated attestation tags as an attestation there and is projected
--    with a null cadence. 'annual' is chosen as the longest window in the vocabulary so the pre-sync
--    display is permissive rather than alarm-generating. The placeholder is NOT observable through
--    ingest staleness: only template rows receive it, attestation_templates has no vendor column so
--    every migrated template row lands with a NULL vendor, ingest rejects a collector with no vendor,
--    and collector_credentials foreign-keys evidence_collectors until step 5, so no template row can
--    hold a credential either. The collectors that CAN ingest come through step 3 with their own
--    authored cadence.
--
--    THIS STEP DELIBERATELY DOES NOT MERGE a template with the manual-attestation/training-attestation
--    collector on the same control. Nothing in the source schema links them: neither table references
--    the other, the pairing is the shared control_id alone, and neither side is required - so a control
--    with two templates and one collector has no pairing the data expressed, and the surviving id would
--    be an arbitrary choice while that id is what credentials, scheduler state, and evidence runs are
--    keyed on. The pair therefore lands as two rows and the required sync collapses it. Nor does it
--    refuse the rows: both are valid under the pre-merge rules, and a migrated `training` row lands with
--    no pass_mark and no quiz only because evidence_collectors has no column that could carry a form, so
--    refusing would block a healthy database. The form-less row still reads cleanly (an absent config
--    deserializes to an empty typed config), is never scheduled, and has no runtime that reads the
--    missing form, which is what makes copying it acceptable. It is not, however, never acted on: a row
--    copied by step 3 keeps its vendor and its credential, so it MAY still ingest evidence - unchanged
--    behaviour that reads the vendor, the control's mapped requirements, and the cadence, never the
--    form.
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

-- 5. Re-point the collector credential foreign key at the merged table. This MUST precede the drops in
--    step 6, or DROP TABLE evidence_collectors fails on the live constraint. A credential is live
--    config, not history, so ON DELETE CASCADE is carried across unchanged.
--
--    No foreign key is added from collector_scheduler_state.collector_id or evidence_runs.collector_id:
--    both are scalar with no FK by existing design, so past runs survive a prune.
ALTER TABLE collector_credentials DROP FOREIGN KEY fk_collector_credentials_collector;
ALTER TABLE collector_credentials
    ADD CONSTRAINT fk_collector_credentials_collector
        FOREIGN KEY (collector_id) REFERENCES collectors (id) ON DELETE CASCADE;

-- 6. Drop the legacy tables. Their data now lives in `collectors`, and each drop removes its own
--    outbound foreign keys (fk_evidence_collectors_*, fk_attestation_templates_control).
DROP TABLE evidence_collectors;
DROP TABLE attestation_templates;
