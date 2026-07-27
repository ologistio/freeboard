-- Collapse scopes, requirement_scopes, and vendor_scopes into one generalized `scopes` table with a
-- polymorphic subject and target. The subject is a scalar utf8mb4_bin column with NO foreign key
-- (dangling tolerated, matching assets.parent/owner); the target is exactly one of three nullable FK
-- columns (standard/requirement/control), each ON DELETE RESTRICT, enforced by a single-target CHECK and
-- three NULL-distinct composite unique keys. This generalizes the vendor_scopes shape to three targets.
--
-- Forward-only and NOT atomically replay-safe, matching 015/018/019: MySQL DDL implicit-commits per
-- statement and the runner records schema_migrations only after the whole file succeeds, so a mid-file
-- crash cannot be recovered by a naive re-run. Pre-production hard cutover: recovery is restore-and-rerun,
-- and because there is no data to preserve that is always available.
--
-- The three copy steps assume the scopes/requirement_scopes/vendor_scopes id spaces are DISJOINT
-- (pre-production, no data contract): a colliding id fails on the duplicate primary key rather than
-- silently merging two distinct scopes. Post-019 the org/vendor subject columns already hold asset ids
-- (organisations/vendors became assets rows keeping their ids), so the subject copy is a direct column
-- rename with no lookup.

-- 1. The unified table. The subject is FK-free (dangling tolerated); the three target columns keep real
--    FKs. The target FKs use collision-free names (fk_scopes_v2_*): InnoDB FK constraint names are
--    schema-wide, and the old `scopes` table still carries fk_scopes_standard from 007 until step 5 drops
--    it, so reusing that name would abort this CREATE. RENAME TABLE (step 6) does not rename constraints,
--    so the _v2 names survive unchanged.
CREATE TABLE IF NOT EXISTS scopes_v2 (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    subject_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    disposition VARCHAR(16) NOT NULL,
    justification TEXT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    -- Exactly one target. MySQL casts each IS NOT NULL to 0/1 and sums them (extending the two-way
    -- vendor_scopes XOR to three targets), so exactly one set target column passes.
    CONSTRAINT ck_scopes_single_target CHECK (
        ((standard_id IS NOT NULL) + (requirement_id IS NOT NULL) + (control_id IS NOT NULL)) = 1),
    -- MySQL treats each NULL as distinct in a unique index, so each key constrains only the rows whose own
    -- target column is non-null (the partial-unique behaviour vendor_scopes already relies on).
    UNIQUE KEY uq_scopes_subject_standard (subject_id, standard_id),
    UNIQUE KEY uq_scopes_subject_requirement (subject_id, requirement_id),
    UNIQUE KEY uq_scopes_subject_control (subject_id, control_id),
    KEY ix_scopes_subject_id (subject_id),
    KEY ix_scopes_standard_id (standard_id),
    KEY ix_scopes_requirement_id (requirement_id),
    KEY ix_scopes_control_id (control_id),
    CONSTRAINT fk_scopes_v2_standard
        FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE RESTRICT,
    CONSTRAINT fk_scopes_v2_requirement
        FOREIGN KEY (requirement_id) REFERENCES requirements (id) ON DELETE RESTRICT,
    CONSTRAINT fk_scopes_v2_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 2. Copy the standard-target scopes. The legacy `scopes` table has NO justification column, so every
--    copied Out row gets the provenance marker (there is no source value to carry), and In rows get NULL.
--    The marker closes the migrate-before-sync window: `system migrate` commits before any importer runs,
--    so GET /scopes must not return an Out with no rationale. The importer's whole-set replace overwrites
--    it with the fixture's real justification on the next sync.
INSERT INTO scopes_v2
    (id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at)
SELECT id, api_version, title, organisation_id, standard_id, NULL, NULL, disposition,
    CASE WHEN disposition = 'Out'
        THEN 'Migrated legacy Out rule; justification was not recorded and requires review.'
        ELSE NULL END,
    created_at, updated_at
FROM scopes;

-- 3. Copy the requirement-target scopes. requirement_scopes also has no justification column, so it uses
--    the same source-less marker expression as step 2.
INSERT INTO scopes_v2
    (id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at)
SELECT id, api_version, title, organisation_id, NULL, requirement_id, NULL, disposition,
    CASE WHEN disposition = 'Out'
        THEN 'Migrated legacy Out rule; justification was not recorded and requires review.'
        ELSE NULL END,
    created_at, updated_at
FROM requirement_scopes;

-- 4. Copy the vendor scopes (requirement- or control-target). Unlike the two org tables, vendor_scopes
--    HAS a real justification column, so preserve the recorded value and substitute the marker only for a
--    blank Out (a defensive backstop; vendor scopes already require a justification on Out).
INSERT INTO scopes_v2
    (id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at)
SELECT id, api_version, title, vendor_id, NULL, requirement_id, control_id, disposition,
    CASE WHEN disposition = 'Out' AND NULLIF(TRIM(justification), '') IS NULL
        THEN 'Migrated legacy Out rule; justification was not recorded and requires review.'
        ELSE justification END,
    created_at, updated_at
FROM vendor_scopes;

-- 5. Drop the legacy tables. These are leaf tables (nothing foreign-keys INTO them), so each drop removes
--    its own outbound FKs (fk_scopes_organisation/fk_scopes_standard, fk_requirement_scopes_*,
--    fk_vendor_scopes_*), freeing the fk_scopes_standard name for the renamed table.
DROP TABLE scopes;
DROP TABLE requirement_scopes;
DROP TABLE vendor_scopes;

-- 6. Rename the unified table into place. RENAME does not rename constraints, so the fk_scopes_v2_* names
--    persist.
RENAME TABLE scopes_v2 TO scopes;
