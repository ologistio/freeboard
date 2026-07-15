-- Add integration connections and the two integration fields on evidence_collectors. An
-- integration_connections row is a provider connection (a base URL and a discovery cadence that drive
-- machine discovery and back many per-control collectors); it optionally names a vendor. The API token
-- is resolved out-of-band by connection id and is never stored here. evidence_collectors gains a
-- connection_id foreign key (only integration collectors carry it) and a checks JSON column (the ordered
-- tracked-check list). Additive and forward-only. Ids and FK columns use utf8mb4_bin to match Core's
-- exact-byte id identity. No organisation_id: like the other GitOps-kind tables this is global.
--
-- The evidence_collectors change is ONE multi-clause ALTER TABLE so MySQL 8.4 InnoDB atomic DDL applies
-- or rolls back the whole change as a unit (never some columns added and others not). The column adds are
-- metadata-only (a nullable column and a JSON column) and rewrite no rows; existing collectors read
-- connection_id = NULL and checks = NULL. checks uses MySQL's native JSON type, which validates
-- well-formedness at write. Both new FKs are ON DELETE RESTRICT: the importer prunes referencing rows
-- before deleting a target (absent evidence_collectors before absent integration_connections before
-- absent vendors).
--
-- NOT atomically replay-safe: the runner runs this SQL and only then records the schema_migrations
-- version, and plain ADD COLUMN is not idempotent (MySQL 8.4 has no ADD COLUMN IF NOT EXISTS). The
-- CREATE TABLE IF NOT EXISTS re-runs cleanly, and a failed apply of the single ALTER rolls back wholly,
-- so a re-run after a failed apply completes. The one residual window is a crash after the ALTER commits
-- but before the version is recorded: a re-run then fails on the duplicate column. This repo cannot use
-- the usual information_schema + PREPARE/EXECUTE guard, because that needs session variables and
-- MySqlConnector defaults AllowUserVariables=false while the runner runs this SQL with no parameters, so
-- a @name would parse as a command parameter and fail on the FIRST apply. So this matches the sibling
-- 015 convention. Operational recovery: drop the added connection_id/checks columns, the
-- ix_evidence_collectors_connection_id key, and the fk_evidence_collectors_connection constraint and
-- re-run `freeboard system migrate`, or record the schema_migrations row for version 018 by hand.

CREATE TABLE IF NOT EXISTS integration_connections (
    id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    provider VARCHAR(32) NOT NULL,
    discovery_cadence VARCHAR(16) NOT NULL,
    base_url VARCHAR(2048) NOT NULL,
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_integration_connections_vendor_id (vendor_id),
    CONSTRAINT fk_integration_connections_vendor
        FOREIGN KEY (vendor_id) REFERENCES vendors (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

ALTER TABLE evidence_collectors
    ADD COLUMN connection_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL AFTER vendor_id,
    ADD COLUMN checks JSON NULL AFTER config,
    ADD KEY ix_evidence_collectors_connection_id (connection_id),
    ADD CONSTRAINT fk_evidence_collectors_connection
        FOREIGN KEY (connection_id) REFERENCES integration_connections (id) ON DELETE RESTRICT;
