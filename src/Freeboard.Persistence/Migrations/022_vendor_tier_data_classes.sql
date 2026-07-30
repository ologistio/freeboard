-- Add the vendor risk profile to the asset row: how much damage the vendor can do (tier) and which
-- regulated data it holds (data_classes). Both are Vendor-only and both are authored in gitops config;
-- the Core validator rejects either on a Company, Department, or Machine, so they stay NULL there.
--
-- Additive and nullable, with no backfill: every existing row is valid afterwards with both NULL, which
-- the register already renders as "Not tracked".
--
-- No index and no foreign key. The vendor register filters in application code over the whole asset set
-- rather than in SQL, so an index nothing queries would not pay for itself, and the data class taxonomy
-- is a closed set in Freeboard.Core rather than a table to reference.
--
-- Forward-only, matching every migration here: schema_migrations records the version only after the whole
-- file succeeds. Recovery from a failed run is restore-and-rerun.
ALTER TABLE assets
    ADD COLUMN tier VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NULL AFTER owner,
    ADD COLUMN data_classes JSON NULL AFTER tier;
