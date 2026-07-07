-- Add attestation-templates. An attestation_templates row attaches a form or quiz to one control (its
-- attach point). Additive and forward-only; alters no existing table. Ids and FK columns use utf8mb4_bin
-- to match Core's exact-byte id identity.
--
-- fields and quiz use MySQL's native JSON type, which validates well-formedness at write; each stores an
-- ordered JSON array round-tripped back into typed lists by the read store. The full quiz item including
-- its answer is serialized into quiz; the answer is stored for the later grading runtime but redacted at
-- the read-model boundary. The control_id FK is ON DELETE RESTRICT (matching the collector table): the
-- importer prunes referencing attestation_templates before deleting a control. Identity is keyed on id
-- only - a control MAY have several templates - so there is no secondary unique key.

CREATE TABLE IF NOT EXISTS attestation_templates (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    control_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    type VARCHAR(16) NOT NULL,
    body TEXT NULL,
    fields JSON NULL,
    pass_mark INT NULL,
    quiz JSON NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_attestation_templates_control_id (control_id),
    CONSTRAINT fk_attestation_templates_control
        FOREIGN KEY (control_id) REFERENCES controls (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
