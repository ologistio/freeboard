-- Machine assets: the integration-agnostic machine an evidence collector or discovery source resolves
-- to. asset holds one row per resolved machine (its canonical identity, kind, lifecycle state, display
-- hostname, and seen/retired timestamps); asset_source holds one row per reporting source attachment,
-- recording what that source observed and its (source, external_id). Ids and reference columns use
-- utf8mb4_bin to match Core's exact-byte id identity.
--
-- Mutable, not append-only: an asset's state, last_seen_at, and hostname change over time, so unlike
-- evidence these tables carry no BEFORE UPDATE / BEFORE DELETE triggers and the write store upserts.
--
-- organisation_id is a scalar reference with NO foreign key (like evidence_runs and authz_audit_events),
-- so an asset survives organisation deletion and GitOps churn. The internal asset_source -> asset
-- reference IS an enforced foreign key widened to carry organisation_id: asset has UNIQUE
-- (id, organisation_id) and asset_source references (asset_id, organisation_id), so the database itself
-- forbids a source in one organisation from pointing at an asset in another - cross-org isolation of the
-- internal reference does not rest on query filtering alone. ON DELETE RESTRICT because an asset with
-- sources is never hard-deleted. InnoDB maintains an index on the referencing (asset_id, organisation_id),
-- whose leftmost asset_id prefix also serves the asset-id reverse lookup, so no separate KEY (asset_id).
--
-- Exact-byte columns: identity_kind, identity_value, kind, state, source, and external_id are utf8mb4_bin
-- so identity and source uniqueness compare by exact case-sensitive bytes; without it MySQL 8's
-- case-insensitive default would collide 'fleetdm' with 'FleetDM' inside the org-scoped unique keys.
--
-- No KEY (organisation_id, state): nothing reads assets by organisation and state, so an index here would
-- carry write and storage cost with no reader to justify it. Add it alongside the query that needs it.
--
-- Replay-safe: CREATE TABLE IF NOT EXISTS, forward-only, no triggers, so a crash between the DDL and the
-- schema_migrations version record re-runs without manual recovery.

CREATE TABLE IF NOT EXISTS asset (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    identity_kind VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    identity_value VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    hostname VARCHAR(255) NULL,
    state VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    retired_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_asset_org_identity (organisation_id, identity_kind, identity_value),
    UNIQUE KEY uq_asset_id_org (id, organisation_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS asset_source (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    asset_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    organisation_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    source VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    external_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    observed_serial VARCHAR(190) NULL,
    observed_host_uuid VARCHAR(190) NULL,
    first_seen_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_asset_source_org_source_external (organisation_id, source, external_id),
    CONSTRAINT fk_asset_source_asset
        FOREIGN KEY (asset_id, organisation_id) REFERENCES asset (id, organisation_id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
