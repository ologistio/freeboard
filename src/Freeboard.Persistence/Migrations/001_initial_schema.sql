-- Initial compliance domain schema.
-- Every id and FK column uses utf8mb4_bin (binary collation) so the database's
-- identity rules match Core's ordinal, case-sensitive id semantics.
-- schema_migrations is NOT created here; the migration runner bootstraps it.

CREATE TABLE IF NOT EXISTS standards (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS controls (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS scopes (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS control_standards (
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (control_id, standard_id),
    KEY ix_control_standards_standard_id (standard_id),
    CONSTRAINT fk_control_standards_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE CASCADE,
    CONSTRAINT fk_control_standards_standard
        FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS scope_controls (
    scope_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (scope_id, control_id),
    KEY ix_scope_controls_control_id (control_id),
    CONSTRAINT fk_scope_controls_scope
        FOREIGN KEY (scope_id) REFERENCES scopes (id) ON DELETE CASCADE,
    CONSTRAINT fk_scope_controls_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
