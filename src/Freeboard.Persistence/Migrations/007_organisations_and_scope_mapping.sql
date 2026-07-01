-- Redefine the compliance scope model around organisations.
-- Adds the organisation tree, rebinds scopes to (organisation, standard, disposition),
-- and drops the old scope->controls relation. Forward-only; pre-1.0 with no data contract,
-- so this does not migrate old scope rows (their controls relation is being removed and the
-- new scope columns are NOT NULL). Ids and FK columns use utf8mb4_bin to match Core's
-- exact-byte id identity.

CREATE TABLE IF NOT EXISTS organisations (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    kind VARCHAR(32) NOT NULL,
    parent_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_organisations_parent_id (parent_id),
    CONSTRAINT fk_organisations_parent
        FOREIGN KEY (parent_id) REFERENCES organisations (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The old scope->controls relation is removed; scopes now map an organisation to a standard.
DROP TABLE IF EXISTS scope_controls;

-- Clear any legacy scope rows before adding the NOT NULL organisation/standard columns and
-- their FKs. Old rows have no valid organisation/standard to satisfy those constraints, so the
-- ALTER would fail on any instance that had synced scopes. Forward-only, pre-1.0: old scopes are
-- not migrated (see header). scope_controls (the only FK referencing scopes) is already dropped.
DELETE FROM scopes;

ALTER TABLE scopes
    ADD COLUMN organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ADD COLUMN standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ADD COLUMN disposition VARCHAR(16) NOT NULL,
    ADD UNIQUE KEY uq_scopes_organisation_standard (organisation_id, standard_id),
    ADD KEY ix_scopes_standard_id (standard_id),
    ADD CONSTRAINT fk_scopes_organisation
        FOREIGN KEY (organisation_id) REFERENCES organisations (id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_scopes_standard
        FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE RESTRICT;
