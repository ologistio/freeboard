-- Unify organisations, vendors, and the discovered machine `asset` into one `assets` table with one id
-- space. `type` (Company/Department/Machine/Vendor) and `source` (declared/discovered) discriminate the
-- rows; declared rows carry api_version/title, discovered rows carry the identity/state/seen columns, and
-- the two never overlap. The two edges - `parent` (containment) and `owner` (accountability) - are
-- scalar VARCHAR with NO foreign key and are mutually exclusive (a CHECK backstops the Core validator),
-- matching the v1 asset.organisation_id precedent: an asset must survive org churn and a discovered child
-- must not let the inventory wedge a config sync.
--
-- Forward-only and NOT atomically replay-safe, matching 015/018: MySQL DDL implicit-commits per
-- statement and the runner records the schema_migrations version only after the whole file succeeds, so a
-- mid-file crash cannot be recovered by a naive re-run (a re-run can fail on an already-created table or
-- already-dropped constraint). Pre-production hard cutover: operational recovery is restore-and-rerun,
-- and because there is no data to preserve that is always available.
--
-- The three copy steps assume the org, vendor, and machine id spaces are DISJOINT (pre-production, no
-- data contract): a colliding id fails on the duplicate primary key rather than merging two subjects.

-- 1. The unified table. Declared-shared columns (api_version, title, updated_at) are nullable because a
--    discovered machine has none; discovered-only columns (identity_kind..retired_at) are nullable
--    because a declared row has none. id/parent/owner use utf8mb4_bin (exact-byte id identity); the
--    token columns use utf8mb4_0900_bin so they compare by exact bytes.
CREATE TABLE IF NOT EXISTS assets (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    type VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
    source VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
    api_version VARCHAR(64) NULL,
    title VARCHAR(512) NULL,
    parent VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    owner VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    identity_kind VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NULL,
    identity_value VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NULL,
    state VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NULL,
    hostname VARCHAR(255) NULL,
    first_seen_at DATETIME(6) NULL,
    last_seen_at DATETIME(6) NULL,
    retired_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    -- At most one edge per row. The Core validator is the user-facing check; this backstops a raw row.
    CONSTRAINT ck_assets_parent_owner_exclusive CHECK ((parent IS NULL) OR (owner IS NULL)),
    -- Carries forward v1's per-org discovered-machine dedup, now keyed on parent. A declared row leaves
    -- identity_kind/identity_value NULL, and MySQL treats each NULL as distinct, so this index never
    -- collides two declared rows; it only dedups discovered machines under a non-null parent. Ingest
    -- always writes a machine under its discovering org, so the null-parent case does not arise there.
    UNIQUE KEY uq_assets_parent_identity (parent, identity_kind, identity_value)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 2. Copy organisations as declared Company/Department assets (parent_id -> parent).
INSERT INTO assets (id, type, source, api_version, title, parent, created_at, updated_at)
SELECT id, kind, 'declared', api_version, title, parent_id, created_at, updated_at
FROM organisations;

-- 3. Copy vendors as declared Vendor assets. owner is re-authored from config on the next sync.
INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at)
SELECT id, 'Vendor', 'declared', api_version, title, created_at, updated_at
FROM vendors;

-- 4. Copy discovered machines (organisation_id -> parent), carrying the discovered-only columns.
INSERT INTO assets (id, type, source, parent, identity_kind, identity_value, state, hostname,
    first_seen_at, last_seen_at, retired_at, created_at)
SELECT id, 'Machine', 'discovered', organisation_id, identity_kind, identity_value, state, hostname,
    first_seen_at, last_seen_at, retired_at, created_at
FROM asset;

-- 5. Relax asset_source's composite FK to a simple asset_id FK. The machine's org column is now the
--    nullable, FK-free assets.parent, so the composite FK cannot survive; widen asset_id to match
--    assets.id so the single-column FK is type-compatible. asset_source keeps its own organisation_id
--    column and (organisation_id, source, external_id) uniqueness; cross-org isolation of sources moves
--    to query filtering.
ALTER TABLE asset_source DROP FOREIGN KEY fk_asset_source_asset;
ALTER TABLE asset_source MODIFY asset_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;
ALTER TABLE asset_source
    ADD CONSTRAINT fk_asset_source_asset FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE RESTRICT;

-- 6. Re-point every downstream enforced FK at assets(id). Existing rows keep their ids, which now
--    resolve to the copied asset rows, so nothing is orphaned. Missing any one here would block the
--    DROP TABLE below, so this is the complete verified set.
ALTER TABLE scopes DROP FOREIGN KEY fk_scopes_organisation;
ALTER TABLE scopes
    ADD CONSTRAINT fk_scopes_organisation FOREIGN KEY (organisation_id) REFERENCES assets (id) ON DELETE RESTRICT;

ALTER TABLE requirement_scopes DROP FOREIGN KEY fk_requirement_scopes_organisation;
ALTER TABLE requirement_scopes
    ADD CONSTRAINT fk_requirement_scopes_organisation FOREIGN KEY (organisation_id) REFERENCES assets (id) ON DELETE RESTRICT;

ALTER TABLE authz_organisation_role_assignments DROP FOREIGN KEY fk_authz_org_role_assignments_org;
ALTER TABLE authz_organisation_role_assignments
    ADD CONSTRAINT fk_authz_org_role_assignments_org FOREIGN KEY (organisation_id) REFERENCES assets (id) ON DELETE RESTRICT;

ALTER TABLE vendor_scopes DROP FOREIGN KEY fk_vendor_scopes_vendor;
ALTER TABLE vendor_scopes
    ADD CONSTRAINT fk_vendor_scopes_vendor FOREIGN KEY (vendor_id) REFERENCES assets (id) ON DELETE RESTRICT;

ALTER TABLE evidence_collectors DROP FOREIGN KEY fk_evidence_collectors_vendor;
ALTER TABLE evidence_collectors
    ADD CONSTRAINT fk_evidence_collectors_vendor FOREIGN KEY (vendor_id) REFERENCES assets (id) ON DELETE RESTRICT;

ALTER TABLE integration_connections DROP FOREIGN KEY fk_integration_connections_vendor;
ALTER TABLE integration_connections
    ADD CONSTRAINT fk_integration_connections_vendor FOREIGN KEY (vendor_id) REFERENCES assets (id) ON DELETE RESTRICT;

-- 7. Drop the old tables; their data now lives in assets. This only succeeds if step 6 re-pointed every
--    FK. organisations carried the self-FK fk_organisations_parent, dropped with the table (it becomes
--    the scalar-no-FK assets.parent).
DROP TABLE organisations;
DROP TABLE vendors;
DROP TABLE asset;
