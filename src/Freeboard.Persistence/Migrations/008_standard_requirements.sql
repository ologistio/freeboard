-- Make a Standard a described object and add the Requirement kind.
-- Additive metadata columns on standards (nullable; existing rows read null until re-synced),
-- a new requirements table where each requirement is owned by exactly one standard, and a repoint of the control join
-- from standards to requirements. Forward-only, pre-1.0: the control_standards join is replaced,
-- not migrated (no join rows carried over; rebuilt on the next gitops sync). Ids and FK columns
-- use utf8mb4_bin to match Core's exact-byte id identity.

ALTER TABLE standards
    ADD COLUMN version VARCHAR(64) NULL,
    ADD COLUMN authority VARCHAR(512) NULL,
    ADD COLUMN publisher VARCHAR(512) NULL,
    ADD COLUMN source_url VARCHAR(2048) NULL;

CREATE TABLE IF NOT EXISTS requirements (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    theme VARCHAR(190) NOT NULL,
    statement TEXT NOT NULL,
    guidance TEXT NULL,
    citation_label VARCHAR(512) NOT NULL,
    citation_url VARCHAR(2048) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_requirements_standard_id (standard_id),
    CONSTRAINT fk_requirements_standard
        FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Repoint the control join from standards to requirements. Pre-1.0, forward-only: no join rows
-- are migrated (the join now targets a different table); it is rebuilt on the next gitops sync.
-- control_requirements must be created after requirements so its FK resolves.
DROP TABLE IF EXISTS control_standards;

CREATE TABLE IF NOT EXISTS control_requirements (
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (control_id, requirement_id),
    KEY ix_control_requirements_requirement_id (requirement_id),
    CONSTRAINT fk_control_requirements_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE CASCADE,
    CONSTRAINT fk_control_requirements_requirement
        FOREIGN KEY (requirement_id) REFERENCES requirements (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
