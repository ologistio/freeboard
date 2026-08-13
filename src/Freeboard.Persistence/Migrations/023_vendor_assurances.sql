-- Record which certifications a vendor holds and when each expires. One child table of `assets`, named
-- for its parent like every other child table here (vendor_scopes, collector_credentials), with
-- foreign-key columns ending in _id. The pair (vendor_id, standard_id) is the primary key: the row is not
-- addressable by any route, scope, or reference, so a synthetic id would be a value appearing nowhere in
-- config, and the composite key carries the one-entry-per-standard rule for free.
--
-- There is no status column. The state of a certification is derived from its expiry and the clock, so a
-- stored one would be wrong from the moment the clock passed it.
--
-- Additive with no backfill: the table starts empty, no existing table changes, and every existing row
-- and older application build stays valid, so it is safe to apply ahead of the deploy.
--
-- No index on expires. The count of expiring vendors is computed in application code over the
-- owner-narrowed read, so nothing queries expires in SQL and an index nothing queries would not pay for
-- itself.
--
-- Forward-only, matching every migration here: schema_migrations records the version only after the whole
-- file succeeds. Recovery from a failed run is restore-and-rerun.
CREATE TABLE IF NOT EXISTS vendor_assurances (
    vendor_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    standard_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    expires DATE NOT NULL,
    -- The only nullable column, because it is the only optional one. NULL means "use the deployment's
    -- configured warning window", not "no window". The CHECK is a backstop to the config validation that
    -- already rejects a negative value, so the column's domain stays true if a later write path forgets.
    warn_days INT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (vendor_id, standard_id),
    CONSTRAINT ck_vendor_assurances_warn_days CHECK (warn_days IS NULL OR warn_days >= 0),
    -- The primary key already covers vendor_id; standard_id needs its own key for its foreign key.
    KEY ix_vendor_assurances_standard_id (standard_id),
    -- Both RESTRICT, matching every other importer-pruned reference: the sync replaces the whole
    -- assurance set before it deletes a vendor or a standard.
    CONSTRAINT fk_vendor_assurances_vendor
        FOREIGN KEY (vendor_id) REFERENCES assets (id) ON DELETE RESTRICT,
    CONSTRAINT fk_vendor_assurances_standard
        FOREIGN KEY (standard_id) REFERENCES standards (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
