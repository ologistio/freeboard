## ADDED Requirements

### Requirement: Vendor tier and data classes persist on the asset row

The system SHALL persist a vendor's tier and data classes on the `assets` row, via
a forward-only migration applied by `freeboard system migrate`. The migration SHALL
add two nullable columns to `assets`: `tier VARCHAR(16) NULL`, holding one of
`Critical`, `High`, `Medium`, or `Low`, and `data_classes JSON NULL`, holding the
authored token set as a JSON array. Both are additive and nullable, so every
existing row is valid after the migration with a null tier and a null class list,
and no backfill runs. The migration SHALL add no foreign key and no index: the
vendor register filters in application code over the full asset set rather than in
SQL, so an index nothing queries would not pay for itself.

The columns live on `assets` rather than in a child table or a generic attribute
bag, matching the table's established wide-and-sparse shape (`hostname`,
`identity_kind`, `identity_value`, `state`, `first_seen_at`, `last_seen_at`, and
`retired_at` are already type-specific and nullable) and the repo's precedent of a
JSON column for an authored list.

A `sync` SHALL write both columns from the declared config on every declared-asset
upsert, and SHALL clear a column when the author removes its key from the config,
matching how removing `owner` from a document nulls the `owner` column. A stale
tier or class list surviving a config edit would misreport the vendor's risk
profile with no diagnostic anywhere, so this clear-on-removal path SHALL be covered
by a test. An empty `data_classes` list in config SHALL persist identically to an
absent one: the store SHALL NOT distinguish "not assessed" from "assessed as
holding nothing". Neither column applies to a `Company`, `Department`, or `Machine`
asset; config validation rejects authoring either field on one (see the
gitops-config-format capability), so both stay null on every non-Vendor row.

Both columns SHALL be readable through the compliance read store alongside the rest
of the asset row, so the vendor register and the vendors API project them without a
second query. This code SHALL live in the MIT `Freeboard.Persistence` project, SHALL
NOT reference `Freeboard.Enterprise`, and SHALL add no new dependency.

#### Scenario: Migration adds both columns without touching existing rows

- **WHEN** `freeboard system migrate` runs against a database carrying declared
  vendor assets
- **THEN** the migration applies successfully, `assets` carries `tier` and
  `data_classes`, and every pre-existing row has both columns null

#### Scenario: Sync writes both columns

- **WHEN** a `sync` runs against a config declaring a `Vendor` asset with a `tier`
  and a non-empty `data_classes`
- **THEN** the asset row carries that tier and that token set

#### Scenario: Removing a key clears its column

- **WHEN** a `sync` runs against a config whose `Vendor` asset previously carried a
  `tier` and `data_classes` and now carries neither
- **THEN** both columns are null on the asset row, not left at their previous values

#### Scenario: Empty data classes persist as absent

- **WHEN** a `sync` runs against a config declaring a `Vendor` asset with
  `data_classes: []`
- **THEN** the stored row is indistinguishable from one synced with the key omitted

#### Scenario: A non-Vendor asset carries neither value

- **WHEN** a `sync` runs against a config declaring `Company`, `Department`, and
  `Machine` assets
- **THEN** every such row has a null `tier` and a null `data_classes`
