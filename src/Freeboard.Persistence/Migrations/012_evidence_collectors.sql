-- Add evidence-collectors and the control evaluation rule. An evidence_collectors row attaches a data
-- source to one control (its attach point) and, optionally, names a vendor; controls gains a nullable
-- evaluation column recording how a control's attached collectors roll up into a status. Additive and
-- forward-only. Ids and FK columns use utf8mb4_bin to match Core's exact-byte id identity.
--
-- The evaluation column add is a single nullable column (metadata-only in MySQL 8.4): it rewrites no
-- rows and old controls read evaluation = NULL. config uses MySQL's native JSON type, which validates
-- well-formedness at write. Both FKs are ON DELETE RESTRICT (matching the scope tables): the importer
-- prunes referencing evidence_collectors before deleting a control or a vendor. Identity is keyed on id
-- only - a control MAY have several collectors - so there is no secondary unique key.

ALTER TABLE controls ADD COLUMN evaluation VARCHAR(16) NULL AFTER title;

CREATE TABLE IF NOT EXISTS evidence_collectors (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    type VARCHAR(32) NOT NULL,
    frequency VARCHAR(16) NOT NULL,
    threshold INT NULL,
    config JSON NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_evidence_collectors_control_id (control_id),
    KEY ix_evidence_collectors_vendor_id (vendor_id),
    CONSTRAINT fk_evidence_collectors_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE RESTRICT,
    CONSTRAINT fk_evidence_collectors_vendor
        FOREIGN KEY (vendor_id) REFERENCES vendors (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
