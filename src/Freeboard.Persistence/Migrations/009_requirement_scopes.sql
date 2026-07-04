-- Add requirement-level scoping. A requirement_scopes row marks one organisation In or Out for
-- one requirement, layered under the standard-level scope. Additive and forward-only; no existing
-- table is altered. The standard is derived from the requirement, so there is no standard_id column
-- and no standards FK: referential integrity to standards is transitive through requirement_id.
-- Both FKs are ON DELETE RESTRICT (matching scopes): the importer prunes referencing rows before
-- deleting an organisation or requirement. Ids and FK columns use utf8mb4_bin to match Core's
-- exact-byte id identity; disposition copies the scopes.disposition definition exactly.

CREATE TABLE IF NOT EXISTS requirement_scopes (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    disposition VARCHAR(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_requirement_scopes_organisation_requirement (organisation_id, requirement_id),
    KEY ix_requirement_scopes_requirement_id (requirement_id),
    CONSTRAINT fk_requirement_scopes_organisation
        FOREIGN KEY (organisation_id) REFERENCES organisations (id) ON DELETE RESTRICT,
    CONSTRAINT fk_requirement_scopes_requirement
        FOREIGN KEY (requirement_id) REFERENCES requirements (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
