## ADDED Requirements

### Requirement: AttestationTemplate persistence

The system SHALL persist the `AttestationTemplate` kind in a dedicated MySQL table
`attestation_templates`, created by a forward-only migration that alters no existing
table. Ids and foreign-key columns SHALL use `utf8mb4_bin` to match Core's exact-byte
id identity, consistent with the existing compliance tables.

The `attestation_templates` table SHALL hold `id`, `api_version`, `title`, a non-null
`control_id` foreign key to `controls`, a `type`, a nullable `body` text value, a
nullable `fields` JSON value holding the ordered list of form fields, a nullable
`pass_mark` integer, a nullable `quiz` JSON value holding the ordered list of quiz
items, `created_at`, and `updated_at`. The `control_id` foreign key SHALL be
`ON DELETE RESTRICT`, matching the other reference tables, so the importer prunes
referencing templates before deleting a control. Identity SHALL be keyed on `id`
only; the table SHALL NOT impose a secondary uniqueness key, because a control MAY
have several attestation templates.

The GitOps importer SHALL sync attestation-templates in the same whole-set-replace
transaction as the other kinds, in a foreign-key-safe order: attestation-templates
upserted by id after controls are upserted (its foreign key points at controls);
absent attestation-templates deleted before absent controls are deleted, so the
`control_id` RESTRICT foreign key is not violated. A blank `body` SHALL be stored as
NULL, a blank `pass_mark` as NULL, and an empty `fields` or `quiz` list as NULL; a
non-empty `fields` or `quiz` list SHALL be stored as a JSON array. Each stored quiz
item SHALL retain its `answer` in the `quiz` JSON, because the later grading runtime
needs it; the answer lives only in storage and is never surfaced by the read store.

The read store SHALL expose the persisted attestation-templates through the
`IComplianceStore` abstraction, deserializing the `fields` and `quiz` JSON back into
their typed lists, and the persisted-counts read SHALL include the
attestation-template count. The read model's quiz items SHALL NOT carry the `answer`:
the read store projects each quiz item to an answer-free shape at the store boundary,
so no read surface (API, CLI, or web register) can expose a training quiz's correct
answer.

#### Scenario: Read store redacts the quiz answer

- **WHEN** a training template with a quiz `answer` is imported and then read back
  through the store
- **THEN** the returned quiz items expose their `prompt` and `options` but carry no
  `answer`, while the stored `quiz` JSON still contains the answer for later grading

#### Scenario: Attestation-templates round-trip through import and read

- **WHEN** a valid config containing a manual template and a training template is
  imported and then read back through the store
- **THEN** every template is persisted and returned with its `id`, `title`,
  `control`, `type`, `body` (null when absent), `fields` (empty when absent),
  `pass_mark` (null when absent), and `quiz` (empty when absent)

#### Scenario: Import order respects foreign keys when a targeted control is removed

- **WHEN** an import removes a control that an attestation-template in the previous
  persisted set attaches to
- **THEN** the importer deletes the referencing template before deleting the control,
  so the control RESTRICT foreign key is not violated

#### Scenario: Counts include attestation-templates

- **WHEN** the persisted-counts read runs against a reachable store
- **THEN** the counts include the number of persisted attestation-templates
