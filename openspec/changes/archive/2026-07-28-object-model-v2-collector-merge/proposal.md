## Why

Object model v2 needs one collector. Today two kinds attach a proving mechanism to a
`Control`: `EvidenceCollector` (a data source with a `type`, `frequency`, `threshold`,
and a free-form `config` map) and `AttestationTemplate` (a form or quiz with `body`,
`fields`, `pass_mark`, and `quiz`). They already overlap: `EvidenceCollector.type`
carries `manual-attestation` and `training-attestation` tokens describing exactly what
an `AttestationTemplate` is, so a manual attestation is authored as a collector with a
type that has no form and a template with a form that has no cadence. Two kinds, two
schemas, two validators, two tables, two read models, two read endpoints, two register
pages, and two CLI commands for one idea. The RFC's Collector section (the agreed v2
baseline) collapses them into one `Collector` whose attestations are just
`type: manual` / `type: training` with the form carried in `config`.

The second half is the `config` map. It is the last free-form escape hatch in the config
format: every other field is a closed token, a typed scalar, or an id reference that must
resolve, but `config` accepts any key with any string value and is never checked. That
means a typo silently does nothing, a runner cannot rely on a key being present, and the
"no secret material" rule is prose rather than something the validator can enforce. This
change makes `config` validated against a schema registered for the collector's
`(type, provider)` pair, with unknown keys rejected, so the merged kind can carry an
attestation form in `config` without reopening a free-form hole.

This is a pre-production HARD CUTOVER: there is no data contract to preserve, so the
migration merges the two tables in place, the in-repo fixtures, docs, and tests are
hand-migrated in this change, and there is no dual-kind compatibility layer and no
converter tool. `apiVersion` stays `freeboard.dev/v1alpha1`.

## What Changes

- **BREAKING** Collapse `EvidenceCollector` and `AttestationTemplate` into one
  `Collector` kind. Both legacy document kinds are removed and become unknown-kind
  diagnostics. A `Collector` has an `id`, a `title`, a `control` (the required attach
  point), an optional `vendor` (a `Vendor`-type `Asset` id), a `type`, a `frequency`, an
  optional `threshold`, an optional `config`, and - for `type: integration` only - a
  required `provider` and a required `connection`. Top-level fields are identity, the
  attach point, cross-document references, and the cadence; every type-specific payload
  (an attestation's `body`/`fields`/`pass_mark`/`quiz` AND an integration collector's
  `checks`) moves under `config`. `frequency` is required on every type, including
  `manual` and `training`, because evidence ingest stamps the collector's cadence onto each
  run it posts and staleness is judged from that stamp.
- **BREAKING** Re-token `type`: the set becomes
  `integration | script | agent | manual | training`. The `manual-attestation` and
  `training-attestation` tokens are removed; a manual or training attestation is a
  `Collector` of `type: manual` / `type: training` carrying its form in `config`.
- **BREAKING** `type: integration` gains a required `provider` discriminator drawn from
  the shared provider token set (V1 = `{ fleet }`, the same set `Integration.provider`
  uses). A collector's `provider` MUST equal the `provider` of the `Integration` its
  `connection` names; a mismatch is a validation error. `provider` MUST be absent on any
  other type.
- **BREAKING** `config` is no longer free-form. Every `(type, provider)` pair has a
  registered schema naming its allowed keys and which are required; a key not in that
  schema is rejected exactly as an unknown top-level field is, and a required key that is
  absent fails validation. The V1 registry is: `(manual, -)` = `{ body?, fields? }`;
  `(training, -)` = `{ body?, fields?, pass_mark, quiz }`; `(integration, fleet)` =
  `{ checks }` (required, non-empty); `(script, -)` and `(agent, -)` = the empty schema (no
  key is accepted). `body` and `fields` are optional on both attestation types and only
  `pass_mark`/`quiz` are type-conditional, exactly as `AttestationTemplate` validation
  behaves today. Requiredness is evaluated on the parsed config member rather than on key
  presence - an empty list and a blank scalar both count as absent - so a `training`
  collector still fails on `quiz: []`, on `pass_mark: ""`, and on a null-valued `pass_mark`
  exactly as a training template does today. One `manual`/`training` verdict does change,
  deliberately: a key the pair's schema does not register is now rejected whatever its
  value, so a `manual` collector authoring `quiz: []` or a blank `pass_mark` - which the
  pre-merge value test ignored - is an unknown-key error. Nothing that carried meaning
  changes verdict.
  The `manual`/`training` value rules are carried over unchanged from
  `AttestationTemplate` validation (field type tokens, option counts and uniqueness, quiz
  answer membership, `pass_mark` range, unique field and quiz ids), the `checks` item rules
  are carried over unchanged from `EvidenceCollector` validation (`source_key`/`name`/
  `severity` required, severity a closed token set, name and source-key uniqueness), and
  the training quiz `answer` stays git-authored, persisted for grading, and redacted from
  every read surface.
- **BREAKING** The required-`evaluation` rule on a `Control` now reaches a control whose only
  proving mechanism is an attestation. Today only the evidence-collector validation phase
  records which controls have something attached, so a `Control` carrying an
  `AttestationTemplate` and no `EvidenceCollector` validates with no `evaluation` rule at all.
  After the merge a former template IS a collector, so `evaluation` is required on a control
  with at least one attached collector of any type, and that control fails validation until it
  gains one. This is accepted, not incidental: the pre-merge asymmetry is the anomaly - a
  `manual-attestation` `EvidenceCollector` already forced the rule while the
  `AttestationTemplate` carrying that same attestation's form did not - and the merge removes
  it. Operators hand-migrate by adding an `evaluation` to such a control; `gitops sync` is a
  required migration step and refuses the config until they do.
- **BREAKING** Reject the half-migrated authoring shapes explicitly: a top-level `body`,
  `fields`, `pass_mark`, `quiz`, or `checks` on a `Collector` is an unknown field; a
  `provider` or `connection` on a collector whose `type` is not `integration` is a
  validation error; and a `checks` key inside the `config` of a non-integration collector
  is rejected by the registry. The cutover therefore cannot silently accept two authoring
  shapes.
- **BREAKING** Merge `evidence_collectors` and `attestation_templates` into one
  `collectors` table (migration `021`): `control_id` and `connection_id` foreign keys as
  today, a `vendor_id` foreign key to `assets(id)`, a new nullable `provider` column, and
  one nullable `config` JSON column carrying the whole validated config map - absorbing
  both the template's `body`/`fields`/`pass_mark`/`quiz` columns and the collector's
  `checks` column, five columns replaced by one. Both legacy tables are dropped.
  `collector_credentials.collector_id` is re-pointed at `collectors(id)` keeping its
  `ON DELETE CASCADE`, and the re-point happens BEFORE the legacy drops or the drop fails
  on the live constraint. The migration composes the merged `config` from schema-owned
  source data only - the old `checks` column and the four template form columns - and does
  NOT carry the old free-form `config` map across: that map accepts any ad-hoc key, so
  persisting it would put unregistered and possibly credential-shaped keys into the column
  this change declares closed, and a carried value colliding with a typed member would
  break the typed read of every collector until a sync repaired it. Operators hand-migrate
  those keys into their authored documents instead.
- **BREAKING** State the stored `config` shape as a contract, because a migration, an
  importer, and a read store all have to compose the same bytes: the stored keys are
  `Body`, `Fields`, `PassMark`, `Quiz`, and `Checks` - the config members' own names, since
  the authored snake_case spelling is a YAML and wire concern, not a storage one - a
  `pass_mark` is stored as a JSON number as the pre-merge
  `pass_mark INT` column already holds it, an absent member is omitted rather than written
  as JSON null or an empty array (an empty list and a blank body both count as absent), an
  entirely empty config is stored as SQL NULL rather than
  `{}`, and a stored quiz item keeps its `answer` for the later grading runtime. The
  migration refuses to migrate rows that the PRE-merge rules already reject,
  rather than copying them into a state the merged validation forbids or the typed read
  cannot bind. Two named check constraints enforce this, so a failure names itself, and each
  is stated by its PREDICATE rather than by one example row, because each refuses more than
  one shape. A transitional guard on the source table asserts today's type-conditional rule
  at the list level -
  `(type = 'integration' AND checks IS NOT NULL AND
  JSON_SCHEMA_VALID('{"type":"array","minItems":1,"items":{"type":"object","properties":{"SourceKey":{"type":"string"},"Name":{"type":"string"},"Severity":{"type":"string"}}}}',
  checks))
  OR (type <> 'integration' AND checks IS NULL AND connection_id IS
  NULL)` - so it refuses a non-`integration` row carrying a `checks` list or a
  `connection_id` (today's validator rejects both on every non-integration type) AND a
  `type: integration` row whose `checks` is missing or malformed (today's validator requires a
  non-empty list of typed checks, and the loader normalizes every item to a non-null check
  before it runs):
  SQL NULL - the shape of every integration row that predates the migration
  which added the column - an empty array, a JSON null, a value that is not an array, an
  array carrying an item that is not an object, or an object item carrying a known member
  (`SourceKey`, `Name`, or `Severity`, in the PascalCase spelling the storage contract fixes)
  whose JSON type is not a string. It
  does NOT enforce a check item's FIELD RULES: the schema declares no `required`, no `enum`,
  and no uniqueness, so an object item whose known members are absent, or whose severity token
  or name uniqueness is wrong, is
  carried, because it travels inside a
  value the migration copies verbatim, it still binds on read, and `gitops validate` names it
  before and after in the
  same terms. The two token-type rules - the item must be an object, and a present known
  member must be a string - are what make that read guarantee true: the
  loader repairs a null check item on the authoring path but the persistence read path is a
  plain typed deserialize with no such repair, so a scalar or null item, or an object whose
  present known member has the wrong JSON type, would fail the read
  rather than bind to an empty check. A second transitional guard applies the same
  read-boundary rule to `attestation_templates.fields` and `.quiz`, which the migration
  re-shapes into `config` by the same mechanism, so the same shapes fail the same read;
  it has no minimum length, because neither list is required by any registered schema.
  Because `JSON_SCHEMA_VALID` is MySQL 8.0.17+ (enforced
  CHECK is 8.0.16+), this migration requires MySQL 8.0.17 or later - the project targets 8.4
  throughout and claims support for nothing older, so the floor moves by one patch release and
  affects nothing. A permanent constraint on the merged table then refuses a
  `type: integration` row that carries its `checks` but whose `connection_id` is NULL
  (`connection` is required on an integration collector), because no `provider` can be
  derived for it. They differ in lifetime on
  purpose: the provider constraint is permanent on the merged table, so the invariant also
  holds for every later write, while both source-table guards are transitional, because which
  `(type, provider)` pair may carry a given `config` key, and whether that key is required,
  are the
  config registry's rules and pinning them in DDL would make a future registry edit a
  migration.
  No refusal can lose data - the payload guards run before the merged table is created,
  the provider guard before either legacy table is dropped - and the repair for both is a
  `gitops sync` against the pre-merge schema, or deleting the row, then re-running. The
  migration deliberately does NOT quietly drop the offending value instead: a silent strip is
  the same disposition this change already refuses for the pre-merge free-form `config` map.
  It does NOT refuse a row that was
  VALID pre-merge and that only the MERGED contract rejects: an attestation authored as a
  collector-plus-template pair on one control has no link between the two source rows, so it
  lands as two collectors and a migrated `training` row lands with no form. Refusing there
  would refuse a healthy database, so the migration copies it, the row reads cleanly, nothing
  consumes the value it is missing (it is never scheduled, and no grading runtime exists;
  it may still ingest evidence, which is unchanged behaviour that never reads the missing
  form), and the required post-migration `gitops sync` collapses the pair.
- **BREAKING** Collapse the two read endpoints (`GET /evidence-collectors` and
  `GET /attestation-templates`) into one `GET /api/v1/freeboard/collectors` returning the
  merged row with its typed, answer-free `config`, whose object omits every absent member
  rather than emitting it as `null` or an empty array, so it is `{}` for a collector whose
  schema accepts no key. The `/compliance/status` `persisted`
  object drops the `evidenceCollectors` and `attestationTemplates` keys and reports one
  `collectors` count, in both the healthy and the all-null degraded shapes.
- **BREAKING** Rename the credential admin routes to
  `POST /api/v1/freeboard/collectors/{id}/credentials` and
  `DELETE /api/v1/freeboard/collectors/{id}/credentials/{credId}`, matching the merged
  noun. Behaviour, permission, and status codes are unchanged.
- **BREAKING** Collapse the two register pages (`/settings/evidence-collectors` and
  `/settings/attestation-templates`) into one `/settings/collectors` page, and the two nav
  entries into one `Collectors` entry. The old routes cease to exist with no redirect.
- **BREAKING** Collapse the two CLI commands (`freeboard collector list` and
  `freeboard attestation-template list`) into one `freeboard collector list` that prints
  every collector under its control, including a manual or training collector's form. The
  `attestation-template` command group is removed. `collector credential issue|revoke`
  are unchanged apart from the renamed API path they call.
- Widen the collector scheduler's schedule fingerprint from `type` + `frequency` to
  `type` + `frequency` + `provider` + `connection` + the collector's typed config. The
  fingerprint exists to revive a dead or errored scheduler row when the collection inputs
  change; with `checks` now inside `config`, the two most likely operator fixes for a
  failing integration collector (correcting a `source_key`, repointing a `connection`) are
  a `config` and a `connection` edit, and under the old fingerprint neither would revive
  the row. `threshold` stays excluded: it is a scoring input, not a collection input.
  Claiming, cadence, backoff, and catch-up semantics are unchanged.
- Rename `EvidenceCollectorFrequency` to `CollectorFrequency` and `EvidenceCollectorRow`
  to `CollectorRow`; the cadence vocabulary, interval helper, and staleness rule are
  unchanged in behaviour.
- Web AND CLI read models ship together; parity is an acceptance rule.
- Hand-migrate every in-repo authored document, doc example, and test fixture from
  `kind: EvidenceCollector` / `kind: AttestationTemplate` to `kind: Collector`, moving each
  template's `body`/`fields`/`pass_mark`/`quiz` and each integration collector's `checks`
  under `config`, and re-tokenizing the two attestation `type` values.

## Capabilities

### New Capabilities

- `collector-register`: the merged read-only collector register - one web page at
  `/settings/collectors` and one `freeboard collector list` CLI command - covering both
  the data-source collectors and the attestation forms the two legacy registers showed
  separately, with the training quiz answer redacted.

### Modified Capabilities

- `gitops-config-format`: the `EvidenceCollector` and `AttestationTemplate` kinds
  collapse into one `Collector` kind with the re-tokenized `type` set and a required
  `provider` on `type: integration`; `config` becomes schema-validated per
  `(type, provider)` with unknown keys rejected and the attestation form carried inside
  it; the two legacy kinds and their authoring and validation requirements are removed;
  and the documentation, asset-authoring, and integration-authorship requirements re-term
  their `EvidenceCollector` references onto the merged kind so no ratified requirement is
  left naming a kind that no longer exists. The registry is the single owner of the key
  set, so no `config` key rule is stated twice: the requiredness of an integration
  collector's `checks` lives only in the `(integration, fleet)` registry row, and the
  collector-validation requirement defers to it rather than hard-coding a rule against
  `type: integration` alone that a second provider's schema would contradict.
- `compliance-persistence`: the `evidence_collectors` and `attestation_templates` tables
  merge into one `collectors` table with a `provider` column and a single `config` JSON
  column that absorbs the template form columns and the `checks` column; migration `021`
  copies both source tables, re-tokenizes the two attestation
  `type` values, derives `provider` for integration rows from the referenced connection and
  refuses a row that cannot derive one, refuses a source row whose `checks` sits on a type
  that may not carry it or is missing or malformed on one that must,
  folds the old `checks` column into the merged `config` without carrying the old
  free-form `config` map,
  re-points the credential foreign key, and drops both legacy tables; the stored `config`
  shape is stated as a contract the migration, the import plan, and the read store all
  compose identically; the importer upserts
  and prunes one collector set; the read store exposes one collector read whose `config`
  is answer-free, and the counts expose one collector count.
- `compliance-web-read`: `GET /evidence-collectors` and `GET /attestation-templates`
  collapse into one `GET /collectors` over the merged row, whose `config` carries an
  integration collector's `checks` alongside an attestation's form; the persisted-counts
  object reports one `collectors` key in place of the two removed keys.
- `gitops-cli`: `validate`/`sync` referential integrity and round-trip coverage re-terms
  `EvidenceCollector`/`AttestationTemplate` as the unified `Collector`, adds the
  provider-mismatch and config-schema rejections as representative command-surface cases,
  and the dry-run planned-state and summary output report one collector set.
- `collector-scheduler`: the scheduler reads the merged collector set through
  `IComplianceStore`; the non-scheduled type tokens become `script`, `agent`, `manual`,
  and `training`; the cadence helper is renamed to `CollectorFrequency` and the runner
  seam takes a `CollectorRow`; the durable-state requirement's "no foreign key to
  `evidence_collectors`" rule re-terms onto the merged table; and the schedule fingerprint
  widens to cover `provider`, `connection`, and the typed `config` so a config fix revives a
  dead or errored row.
  Claiming, cadence, backoff, and catch-up semantics are unchanged.
- `collector-credentials`: the credential table foreign-keys `collectors` instead of
  `evidence_collectors`, and the issue and revoke routes move to
  `/api/v1/freeboard/collectors/{id}/credentials`.
- `evidence-ingest`: identity resolution and cadence recording read the merged
  `CollectorRow` instead of `EvidenceCollectorRow`; behaviour, status codes, and the
  `freeboard.evidence.v1` payload contract are unchanged.
- `statement-of-applicability`: the drill-down projection builds a control's checks from
  one collector set, deriving the existing `collector`/`attestation` check tag from the
  collector's `type` (`manual`/`training` tag as an attestation, every other type as a
  collector) rather than from two input lists, and projecting no cadence on an
  attestation-tagged check even though every collector now has a required `frequency`. The
  snapshot, ordering, and answer-hiding behaviour are unchanged, and a check that came from
  an `AttestationTemplate` renders exactly as it does today. One rendering DOES change, and
  it is accepted: a pre-merge `manual-attestation` or `training-attestation`
  `EvidenceCollector` is tagged as a collector today, so it renders a cadence and an
  evidence status; after the merge it is `type: manual` / `type: training`, tags as an
  attestation, and loses both. The status half follows from the derived tag and the existing
  "only collector checks carry a status" rule; the cadence half is the same consequence of
  the same decision, and showing one without the other would be incoherent.
- `integration-connection`: the connection is referenced by a `Collector` of
  `type: integration` (which now also names the connection's `provider`); the schema
  requirement re-terms the `connection_id` column onto the merged `collectors` table and
  re-terms the `checks` column as one key inside that table's `config` JSON column, whose
  stored spelling is the config model's member name;
  the startup unresolvable-token warning scans the merged collector set; the "never
  stored in an `EvidenceCollector.config` map" rule re-terms onto `Collector.config`,
  which is now a closed schema in which no key holds credential material. Two sentences in
  that requirement are deliberately NOT re-termed, on the same rule the `asset-model` bullet
  below applies: the statements that the integration-connection migration created its
  `vendor_id` foreign key against `vendors(id)` and added the `connection_id` and `checks`
  columns to `evidence_collectors` without rewriting existing rows are a record of what that
  past migration did, and re-terming them would make them false. The post-merge position is
  stated alongside them instead. One sentence IS corrected beyond this change's own
  re-terms, and is disclosed here rather than left to be found: the importer-order sentence
  said connections are upserted "after vendors" and pruned "before any referenced vendor",
  which the vendor-to-asset merge invalidated - the importer upserts declared assets, not
  vendors. That is a live-behaviour statement, not a historical one, so leaving it while
  re-terming the same sentence's `evidence-collectors` half would republish a known-false
  clause.
- `web-app-shell`: the information-architecture route list replaces the two register
  routes with one `/settings/collectors`, and the nav map carries one `Collectors` entry
  in place of the two.
- `asset-model`: the declared-asset removal requirement's FK-safe prune enumeration
  re-terms `evidence-collectors` as the merged `collectors`, and records that
  `attestation_templates` no longer exists. The historical migration-`019` requirement,
  which describes the six foreign keys that migration re-pointed at the time it ran, is
  left untouched: it is a record of what one past migration did, not a description of the
  current schema, and the same requirement already names `requirement_scopes` and
  `vendor_scopes`, which the scope-generalization migration dropped and deliberately did not
  re-term. Re-terming it would make it false - migration `019` did not re-point a
  `collectors.vendor_id` that did not yet exist. No asset behaviour changes.
- `evidence-persistence`: the "no foreign key from `evidence_runs.collector_id`" rule
  re-terms onto the merged `collectors` table. No evidence behaviour changes.
- `scheduler-lease`: the "no foreign key to `evidence_collectors`" rule on
  `collector_scheduler_state` re-terms onto the merged `collectors` table. No lease
  behaviour changes.

### Removed Capabilities

- `evidence-collector-register`: superseded by `collector-register`. Its web page and CLI
  requirements are removed.
- `attestation-template-register`: superseded by `collector-register`. Its web page and
  CLI requirements are removed.

Both are removed in FULL, so each would leave a capability directory holding zero
requirements. Applying the deltas does not delete such a directory - it rewrites the file
with an empty requirements section and keeps the file's preamble - and a spec with no
requirements fails validation ("Spec must have at least one requirement"), so leaving them
would leave two permanently invalid specs in the source of truth that code review points
at. Both directories are therefore DELETED when the deltas are applied. Everything they
specified now lives in `collector-register`, and each removed requirement carries a
migration note saying where.

## Non-Goals

- The `Group` kind and group subjects (#123). Untouched by this change.
- Building the integration runner, the attestation grading runtime, or the script/agent
  execution paths. This change delivers the merged declared model, its validation, its
  persistence, and its read surfaces. The default `IScheduledCollectorRunner` stays the
  logging no-op.
- Registering any `(integration, fleet)` config key beyond `checks`. A fleet collector's
  remaining inputs are carried by its `connection` (base URL plus the out-of-band token);
  inventing a key the FleetDM collector (#52) has not yet asked for - a `team` key, say -
  would be speculative scaffolding. Adding one later is a one-line registry edit, not a
  mechanism change. `script` and `agent` register the empty schema for the same reason: no
  runner exists to consume a key.
- Nested unknown-key rejection inside a `fields`, `quiz`, or `checks` ITEM. The existing
  `AttestationTemplate` carve-out is preserved: unknown-key rejection applies to top-level
  document keys and to the keys of the `config` map itself, not to keys inside a nested
  form-field, quiz-item, or check mapping. Required nested keys are still enforced.
- An app-managed (non-GitOps) write path for collectors. Collectors stay gitops-write-only.
- Changing which collectors may ingest evidence. A `manual` or `training` collector with a
  registered vendor can still hold a credential and post evidence, exactly as a
  `manual-attestation` collector can today. What DOES widen, and is stated here rather than
  left to be found, is the set of ids those two surfaces can address. Credential issuance
  gates on nothing but the id existing in the collector read - no type test and no vendor
  test - so after the migration every id that came from an `AttestationTemplate` becomes a
  valid credential subject and a valid ingest `collector_id`, where before it was in neither
  set. That includes the vendor-less, form-carrying half of a migrated collector-plus-template
  pair. The blast radius is small and needs no new rule: a credential issued against a
  vendor-less collector fails on its first ingest POST with the existing missing-vendor 422,
  and the required `gitops sync` collapses the pair. The reachable id space widens; the rule
  about what may ingest does not.
- Merging `Integration` into `Collector`, or removing `Integration.provider` in favour of
  the collector's. Both keep their provider token; the collector's is validated to equal
  the connection's.

## Impact

- MIT work only, no Enterprise carve-out. Domain model, loader, validator, and the config
  schema registry in `Freeboard.Core` (`GitOps/`); schema, migration, stores, and importer
  in `Freeboard.Persistence`; read models, endpoints, register page, scheduler, and ingest
  in `Freeboard` (web); commands and the API client in `Freeboard.CLI`.
  `Freeboard.Agent` and `Freeboard.Enterprise` are untouched. No new package dependency.
- Schema: new migration `021_collector_merge.sql`; `evidence_collectors` and
  `attestation_templates` merge into one `collectors` table and both legacy tables are
  dropped; `collector_credentials` re-points its foreign key.
- API: `GET /evidence-collectors` and `GET /attestation-templates` are removed and
  replaced by `GET /collectors`; the credential routes move under `/collectors/{id}`; the
  `/compliance/status` `persisted` object changes shape. All are breaking public response
  or route changes. The merged response widens one read surface: an integration collector's
  `checks` become readable (under `config`) where they were previously persisted and never
  returned. They are non-secret git-authored data; only the training quiz `answer` stays
  redacted.
- Web: `/settings/attestation-templates` is removed and `/settings/evidence-collectors`
  becomes `/settings/collectors`, with one nav entry. Path-asserting web and E2E tests move
  with them; preserved test markers are unchanged. A check that came from a
  `manual-attestation` or `training-attestation` `EvidenceCollector` now tags as an
  attestation rather than as a collector, and that reaches THREE rendered surfaces, not one.
  On the Statement of Applicability page it loses its cadence and its evidence status. In the
  object drawer and on the control detail page it loses its status too: both project a
  control's proving checks through the one shared `ControlDetailProjection`, which attaches an
  evidence status and a status-derived note only to a collector-tagged check and renders a
  bare "Attestation" note otherwise. Neither of those two needs a code change or a
  `web-object-drawer` delta - the kind-based branch there is already correct, and the
  kind-based status rule lives in `statement-of-applicability`, which this change modifies -
  but their rendering changes, so it is stated here rather than left to be found. The same
  tag flip also moves such a check's POSITION in the list on all three surfaces: checks are
  ordered by kind then id and the collector kind sorts before the attestation kind, so a
  control carrying both an integration collector and a former `manual-attestation` collector
  whose id sorts first renders the two rows in the opposite order after the merge. The
  ordering rule itself is unchanged; the tag it reads is what moved. It compounds
  with a preserved behaviour: such a collector may still hold a credential and ingest, so
  evidence keeps accruing while all three surfaces stop showing its status. The register page
  drops the `data-config-key` attribute it rendered
  per free-form config entry, which has no successor once `config` is a closed key set; it
  is not a preserved marker.
- CLI: the `attestation-template` command group is removed; `collector list` absorbs its
  output.
- Config authors and every in-repo document under `docs/`, `examples/`, and the tests move
  from `kind: EvidenceCollector` / `kind: AttestationTemplate` to `kind: Collector`, with
  the attestation form and the integration `checks` moved under `config` and the two
  attestation `type` tokens re-written. An attestation authored as an `EvidenceCollector`
  plus an `AttestationTemplate` on one control becomes ONE `Collector` document, and the
  post-migration sync prunes the surplus row the migration left. A `Control` whose only
  proving mechanism was an `AttestationTemplate` gains an `evaluation` rule, and a collector
  and a template that share an id without being a pair need one renamed, because ids are now
  unique across the merged kind rather than per legacy kind. A half-migrated document
  fails loudly rather than loading in a legacy shape.
- MySQL integration tests must cover the merged schema, the merge migration (including the
  type re-tokenization, the derived `provider`, the `checks` column folded into `config`,
  the credential foreign key re-pointed before the legacy drops, and every refusal -
  a non-integration row carrying `checks` or a `connection_id`, an integration row whose
  `checks` is absent or not a non-empty JSON array of objects with well-typed known members,
  a template row whose `fields` or `quiz` is not a JSON array of objects with well-typed known
  members, and a checks-carrying
  integration row
  with a NULL connection - alongside the shapes the guards deliberately let through, and the
  unnamed primary-key abort when the two source tables share an id),
  per-`(type, provider)` config validation, the FK-safe prune order, and the answer
  redaction on read.
