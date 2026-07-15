## MODIFIED Requirements

### Requirement: EvidenceCollector persistence and Control evaluation column

The system SHALL persist the `EvidenceCollector` kind in a dedicated MySQL table
`evidence_collectors` and SHALL persist a control's `evaluation` rule in a new
nullable `evaluation` column on the existing `controls` table, both created by a
forward-only migration. The `controls.evaluation` column SHALL be a nullable
`VARCHAR(16)`; existing control rows read back `null` when no rule is set. Ids and
foreign-key columns SHALL use `utf8mb4_bin` to match Core's exact-byte id identity,
consistent with the existing compliance tables.

The `evidence_collectors` table SHALL hold `id`, `api_version`, `title`, a non-null
`control_id` foreign key to `controls`, a nullable `vendor_id` foreign key to
`vendors`, a `type`, a `frequency`, a nullable `threshold` integer, a nullable
`config` JSON value holding the type-specific settings map, a nullable `connection_id`
foreign key to `integration_connections` (set only for a `type: integration`
collector), a nullable `checks` JSON value holding an integration collector's ordered
checks list, `created_at`, and `updated_at`. The `control_id`, `vendor_id`, and
`connection_id` foreign keys SHALL be `ON DELETE RESTRICT`, matching the scope tables,
so the importer prunes referencing collectors before deleting a control, a vendor, or an
integration-connection. Identity SHALL be keyed on `id` only; the table SHALL NOT impose
a secondary uniqueness key, because a control MAY have several collectors.

The GitOps importer SHALL sync controls (with their evaluation rule) and
evidence-collectors in the same whole-set-replace transaction as the other kinds, in
a foreign-key-safe order: controls upserted by id with their `evaluation` column;
evidence-collectors upserted by id after controls, vendors, and integration-connections
are upserted (its foreign keys point at all three); absent evidence-collectors deleted
before absent integration-connections, vendors, controls, and requirements are deleted,
so no RESTRICT foreign key is violated. A blank `evaluation` SHALL be stored as NULL, a
blank `threshold` as NULL, an empty `config` map as NULL, a blank `connection` as NULL,
and an empty `checks` list as NULL; a non-empty `config` map SHALL be stored as a JSON
object and a non-empty `checks` list as a JSON array.

The read store SHALL expose the persisted evidence-collectors through the
`IComplianceStore` abstraction, SHALL include the control's `evaluation` rule (null
when unset) on the control read and the collector's `connection` (null when absent) on
the collector read, and the persisted-counts read SHALL include the evidence-collector
count.

#### Scenario: Evidence-collectors round-trip through import and read

- **WHEN** a valid config containing controls with an evaluation rule and
  evidence-collectors is imported and then read back through the store
- **THEN** every collector is persisted and returned with its `id`, `title`,
  `control`, `vendor` (null when absent), `type`, `frequency`, `threshold` (null when
  absent), `config` map, and `connection` (null when absent), and every control returns
  its `evaluation` rule (null when unset)

#### Scenario: Integration collector persists its connection and checks

- **WHEN** a `type: integration` collector naming a connection and declaring a
  non-empty checks list is imported
- **THEN** its row stores the `connection_id` foreign key and the `checks` JSON array,
  and a non-integration collector stores `connection_id` and `checks` as NULL

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that an evidence-collector in the previous
  persisted set attaches to
- **THEN** the importer deletes the referencing collector before deleting the
  control, so the control RESTRICT foreign key is not violated

#### Scenario: Import order respects foreign keys when a named vendor is removed

- **WHEN** an import removes a vendor that an evidence-collector in the previous
  persisted set names
- **THEN** the importer deletes the referencing collector before deleting the vendor,
  so the vendor RESTRICT foreign key is not violated

#### Scenario: Import order respects foreign keys when a referenced connection is removed

- **WHEN** an import removes an integration-connection that an evidence-collector in the
  previous persisted set names
- **THEN** the importer deletes the referencing collector before deleting the
  integration-connection, so the connection RESTRICT foreign key is not violated

#### Scenario: Evaluation rule is added to an existing control without data loss

- **WHEN** a control that previously had no `evaluation` is re-synced with an
  `evaluation` rule
- **THEN** the stored control row returns the new rule and its other columns and
  cross-references are unchanged

#### Scenario: Counts include evidence-collectors

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include the number of persisted evidence-collectors
