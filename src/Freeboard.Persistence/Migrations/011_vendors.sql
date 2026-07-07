-- Add vendors and per-vendor requirement/control exceptions. A vendor names a piece of software or
-- platform in use; a vendor_scopes row records whether one requirement or one control applies to one
-- vendor, with a justification for exceptions. Additive and forward-only; no existing table is
-- altered. Ids and FK columns use utf8mb4_bin to match Core's exact-byte id identity; disposition
-- copies the scopes.disposition definition exactly.
--
-- vendor_scopes targets exactly one of requirement_id or control_id. Both FK columns are nullable so
-- each row names a single target kind; MySQL treats NULLs as distinct, so the two unique keys each
-- constrain only their own target kind. The exactly-one-target invariant is enforced primarily by the
-- Core validator (the user-facing diagnostic) and backstopped here by a CHECK so a raw row with both
-- or neither target is rejected by the engine (MySQL 8.4 enforces CHECK). All three FKs are ON DELETE
-- RESTRICT (matching the scope tables): the importer prunes referencing vendor_scopes before deleting
-- a vendor, requirement, or control.

CREATE TABLE IF NOT EXISTS vendors (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS vendor_scopes (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    requirement_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    disposition VARCHAR(16) NOT NULL,
    justification TEXT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_vendor_scopes_vendor_requirement (vendor_id, requirement_id),
    UNIQUE KEY uq_vendor_scopes_vendor_control (vendor_id, control_id),
    KEY ix_vendor_scopes_vendor_id (vendor_id),
    KEY ix_vendor_scopes_requirement_id (requirement_id),
    KEY ix_vendor_scopes_control_id (control_id),
    CONSTRAINT ck_vendor_scopes_single_target CHECK ((requirement_id IS NULL) <> (control_id IS NULL)),
    CONSTRAINT fk_vendor_scopes_vendor
        FOREIGN KEY (vendor_id) REFERENCES vendors (id) ON DELETE RESTRICT,
    CONSTRAINT fk_vendor_scopes_requirement
        FOREIGN KEY (requirement_id) REFERENCES requirements (id) ON DELETE RESTRICT,
    CONSTRAINT fk_vendor_scopes_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
