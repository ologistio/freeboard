# Tasks

Each group maps to one Conventional Commit. This is a BREAKING change (`!`): it collapses
two kinds and two tables into one, removes two document kinds, two read endpoints, two web
routes, and one CLI command group, and renames two API routes. Use the `breaking` label /
`BREAKING CHANGE:` footer on the model, schema, API, and route commits.

Groups 1-3 must land in order (Core defines the model the plan and importer consume, and
the migration must exist before the store is retargeted). Groups 4-6 depend on 1-3.

Groups 1-5 are a STACKED sequence, not five independently green commits, and the plan says
so rather than pretending otherwise. Group 1 renames the cadence helper and replaces the
`GitOpsConfig` collections and model records; `Freeboard.Persistence`, `Freeboard`, and
`Freeboard.CLI` all bind those types and are repaired in groups 3-5, and group 2 is SQL
only. So `dotnet build` and `dotnet test` are required green at the TIP of group 5, and
group 7 is what enforces it - not after each intermediate commit. Keep the groups as
separate Conventional Commits anyway: they make the change reviewable one layer at a time
and keep the release notes accurate. Do not add a temporary duplicate of the cadence helper
to make an intermediate commit compile; that is two systems doing one job for the length of
one branch.

## 1. Core: unified Collector model, loader, config schema registry, and validation

Commit: `feat(core)!: merge EvidenceCollector and AttestationTemplate into one Collector kind`

- [x] 1.1 In `src/Freeboard.Core/GitOps/ConfigModel.cs`: replace the `EvidenceCollector`
  and `AttestationTemplate` records with one `Collector` record (`apiVersion`, `kind`,
  `id`, `title`, `control`, `vendor`, `type`, `provider`, `frequency`, `threshold`,
  `connection`, `config`) - note there is NO top-level `checks`. Add a `CollectorConfig`
  record (`Body`, `Fields`, `PassMark` as raw text, `Quiz`, `Checks`). Keep
  `AttestationField`, `QuizItem`, and `Check` verbatim, re-homed under
  `CollectorConfig`. Replace `KindEvidenceCollector` and
  `KindAttestationTemplate` with `KindCollector` on `GitOpsSchema`. Carry one `Collectors`
  list on `GitOpsConfig`, dropping `EvidenceCollectors` and `AttestationTemplates`.
- [x] 1.2 Add `src/Freeboard.Core/GitOps/CollectorConfigSchema.cs`: a pure static registry
  mapping `(type, provider)` to an ordered list of `CollectorConfigKey(Name, Required)`,
  returning null when no schema is registered. Register `(manual, -)` = `body` optional +
  `fields` optional; `(training, -)` = `body` optional + `fields` optional + `pass_mark`
  required + `quiz`
  required; `(integration, fleet)` = `checks` required; `(script, -)` and `(agent, -)` =
  the empty schema. `body` and `fields` are registered on BOTH attestation types and
  optional on both, and only `pass_mark`/`quiz` are type-conditional: that is what the
  pre-merge template validation does today (a template with every optional omitted
  validates, and the form-field rules run for whichever type declares `fields`), and
  keeping it is the `manual`/`training` parity this change accepts as a criterion. Fix the
  two `Required` semantics, which are evaluated against DIFFERENT things. Requiredness is
  evaluated on the PARSED config member, not on YAML key presence: a required key is
  satisfied only by a non-empty list where the value is a list and a non-blank scalar where
  it is a scalar, so `quiz: []` on `training`, `checks: []` on `integration+fleet`,
  `pass_mark: ""` on `training`, and a null-valued `pass_mark` all still fail, as the
  pre-merge value rules make them fail. Do not test presence alone: today's
  `hasPassMark` is `!string.IsNullOrWhiteSpace(...)` and it guards BOTH the missing-field
  check and the range check, so a presence-only test would let a `training` collector with a
  blank pass mark validate and persist. Unregistered-key rejection stays evaluated on the
  authored mapping, because a key with an empty value parses to the same member as an absent
  one: an unregistered key is rejected whatever its value (so `quiz: []` on `manual` is now
  an unknown-key error where the pre-merge value test ignored it - the first of the three
  accepted parity differences). Document
  why the two empty schemas are deliberate and why `checks` is
  registered per provider rather than kept top-level. The registry is the SINGLE owner of
  the key set: do not also hard-code any of its rules in the validator (see 1.4).
- [x] 1.3 In `src/Freeboard.Core/GitOps/ConfigLoader.cs`: set the `Collector` allowed
  top-level key set to `{ apiVersion, kind, id, title, control, vendor, type, provider,
  frequency, threshold, connection, config }` - deliberately excluding `checks`, `body`,
  `fields`, `pass_mark`, and `quiz`, so a half-migrated document fails with an
  unknown-field diagnostic; keep one `Collector` switch arm;
  remove both legacy key sets and arms; drop both retired tokens from the unknown-kind
  message enumeration. Normalize the `config` NODE ITSELF FIRST: an explicit-null `config:`
  binds the whole member to null (overwriting the record default), so normalise it to an
  empty `CollectorConfig` before touching anything inside it. This is the half of the
  pre-merge `EvidenceCollector` arm that the nested-list clauses below do not cover - that
  arm writes `Config = collector.Config ?? []` alongside its `Checks` normalisation - and
  omitting it breaks the never-throw contract, because the natural post-merge shape
  (`collector with { Config = collector.Config with { ... } }`) dereferences null and throws
  inside the loader. An explicit null binds cleanly, so no `YamlException` is raised and
  nothing the loader catches would turn it into a diagnostic. The verdict is the same as
  today's for an explicit-null `config:` on an `EvidenceCollector`: the collector loads with
  an empty config and no diagnostic, exactly as if `config` were omitted. Then normalize
  explicit-null collections inside `config`
  (`fields`/`quiz`/`options`/`checks`) as the two legacy arms do today, and carry BOTH halves
  of what those arms do, because they normalise a null SEQUENCE ITEM differently and after the
  merge all three lists live in one `CollectorConfig` normalised in one place. A null `checks`
  ITEM is KEPT, as an empty `Check`, so the validator reports its missing
  `source_key`/`name`/`severity` rather than the loader silently dropping a malformed check; a
  null `fields` or `quiz` ITEM is DROPPED. The asymmetry is deliberate and must be carried,
  not resolved: generalising "keep" turns a document that validates today into a failing one,
  and generalising "drop" silently loses the check diagnostic. The `fields`/`quiz` drop is
  pinned today by `ExplicitNullFieldAndQuizItemsAreDroppedNotThrown` in
  `tests/Freeboard.Core.Tests/ConfigLoaderTests.cs`. Every list is still normalised to empty
  when the KEY itself is null, preserving the never-throw contract. Read the document's `type`
  and `provider` scalars from the representation
  model, look up the registry, and emit an unknown-key diagnostic for each `config` key the
  schema does not name; emit NO config-key diagnostic when no schema resolves, and none
  when the `config` node is not a mapping at all (a scalar or a sequence) - in that case
  the typed bind's diagnostic is the one diagnostic the author gets. The explicit null
  above is the exception to that last clause: it is a null scalar node, so the key diff is
  skipped for it too, but it binds cleanly and so yields NO diagnostic from either path -
  which is the correct verdict, not a gap.
- [x] 1.4 In `src/Freeboard.Core/GitOps/ConfigValidator.cs`: replace
  `ValidateEvidenceCollectors` and `ValidateAttestationTemplates` with one
  `ValidateCollectors` enforcing: required `id`/`title`/`control`/`type`/`frequency` (on
  EVERY type - see the design's D11); the
  five type tokens; the frequency tokens; threshold 0..100; control and vendor-asset
  reference resolution; `provider` required, in `IntegrationProvider.Tokens`, and equal to
  the referenced connection's provider for `type: integration`; `connection` required and
  resolvable for `type: integration`; `provider` and `connection` absent otherwise; check
  shape, severity token, and
  name/source-key uniqueness; duplicate id; the control-evaluation rule. Do NOT write a
  dedicated rule requiring a non-empty `config` `checks` list on `type: integration`. Run
  the registry's required-key check instead: it rejects a missing or empty `checks` on
  `(integration, fleet)` on its own, because requiredness is evaluated on the parsed member.
  A dedicated rule would be a second system doing the registry's job, hard-coded against
  `type` alone, which a second integration provider registering a different key set would
  contradict. Note the two registry-driven paths reject different things and neither
  substitutes for the other: the VALIDATOR's required-key check rejects a registered key
  that is absent or empty, while the LOADER's unregistered-key diff (1.3) is what rejects a
  `checks` key on a non-integration collector, since no such pair registers it.
  Keep `ValidateAttestationFields`,
  `ValidateAttestationQuiz`, `ValidateCollectorChecks`, and
  `CheckDuplicateOptions` verbatim in behaviour APART FROM the kind noun in their messages,
  which re-terms onto `Collector`: all four interpolate a kind constant that 1.1 deletes
  (`KindAttestationTemplate` in the first, second, and fourth, `KindEvidenceCollector` in
  the third), so the diagnostic wording necessarily changes and the compiler forces it.
  Which rules fire, on what, and in what order is what stays verbatim. Read the form and
  the checks from `Collector.Config` and report against the collector id.
- [x] 1.5 Rename `src/Freeboard.Core/GitOps/EvidenceCollectorFrequency.cs` to
  `CollectorFrequency.cs` and the type with it; the token set, windows, grace, `Interval`,
  and `IsStale` behaviour are unchanged.
- [x] 1.6 Rewrite the Core unit tests onto the merged kind: fold
  `tests/Freeboard.Core.Tests/EvidenceCollectorValidationTests.cs` and
  `AttestationTemplateValidationTests.cs` into `CollectorValidationTests.cs` covering every
  rule in 1.4, including the retired `manual-attestation`/`training-attestation` tokens now
  being rejected. Add `CollectorConfigSchemaTests.cs` covering an unknown key per
  `(type, provider)`, a missing required key, `pass_mark` rejected on `manual`, `checks`
  rejected on `manual`/`training`/`script`/`agent`, `checks` missing or empty on
  `integration+fleet`, any key
  rejected on `script`/`agent`, omitted `config` accepted where nothing
  is required, no cascade when the type or provider is unknown, and a nested unknown key
  inside a `fields`/`quiz`/`checks` item being ignored. Add a REGISTRY COMPLETENESS test to
  the same file: assert `CollectorConfigSchema.For` returns non-null for every pair the
  token sets can produce - `(integration, p)` for each `p` in `IntegrationProvider.Tokens`,
  and `(t, absent)` for each non-integration type token - enumerating the token sets rather
  than listing the five pairs by hand, so adding a provider to the shared token set without
  a registry row fails the build. Without it that pair is VALID (so no bad-token
  diagnostic) and resolves no schema (so, by the no-cascade rule, no unknown-key and no
  required-key diagnostic either), which silently reopens free-form `config` for that
  provider - the exact hole this change closes. Cover the `manual`/`training`
  parity, which is the acceptance criterion: a `manual` collector with `config` omitted
  entirely validates, a `manual` collector with `body` but no `fields` validates, and a
  `training` collector authoring `fields` alongside `pass_mark`/`quiz` validates with the
  form-field rules applied. Pin both halves of the empty-value rule from 1.2: `quiz: []` on
  a `manual` collector is rejected as an unregistered key, while `quiz: []` on a `training`
  collector still fails as a missing required key; add the blank-scalar case with them -
  `pass_mark: ""` and a null-valued `pass_mark` on a `training` collector each fail as a
  missing required key, not as a range error and not silently. Pin the SECOND accepted parity
  difference in `CollectorValidationTests.cs`: a `Control` whose ONLY attached collector is a
  `manual` or a `training` one and which declares no `evaluation` is REJECTED, naming the
  control and the missing rule. That is a widening - the pre-merge
  `ValidateAttestationTemplates` never recorded a control as having something attached, so a
  template-only control validated with no `evaluation` - so the test is what keeps it a
  decision rather than an accident. Pin the THIRD there too: a collector-shaped and an
  attestation-shaped document sharing one id is now a duplicate-id error, where per-kind
  detection let the pair validate. The existing two-script-collectors case does not show it,
  because that pair collided before the merge as well. Add the half-migration cases: a
  top-level `checks`, `body`, `fields`, `pass_mark`, or `quiz` on a `Collector` rejected as
  an unknown field, and `provider`/`connection` rejected on a non-integration collector.
  Add the wrong-shape cases, which must be diagnostics and never exceptions: `config`
  authored as a scalar and as a sequence (one diagnostic, no per-key cascade, other
  documents still load), and `config.fields`, `config.quiz`, and `config.checks` each
  authored as a scalar - the merged-kind successors of today's non-list `fields`/`quiz`
  tests. Add the explicit-null `config:` case to that list, which is the one entry that is
  NOT a diagnostic: `config:` authored with no value loads as a collector with an empty
  `CollectorConfig` and NO diagnostic, which is today's verdict for an explicit-null
  `config:` on an `EvidenceCollector`. It needs its own test because it is the only
  wrong-shape-adjacent case the loader catches nothing for - a scalar or a sequence raises a
  `YamlException` the loader already turns into a diagnostic, while an explicit null binds
  cleanly and would throw only once something dereferences it - so this test is what pins
  the 1.3 `config`-node normalisation the never-throw contract depends on.
  Author each wrong-shape list on a type whose schema REGISTERS it - `fields` on `manual`,
  `quiz` on `training`, `checks` on `integration` - and assert the diagnostic is the BIND
  FAILURE, not merely that diagnostics are non-empty. On a type that does not register the
  key, the loader's unregistered-key diagnostic satisfies a bare non-empty assertion before
  the typed bind is ever reached, so the test would pass without testing anything. Cover a
  MAPPING-shaped list alongside the scalar one: either is a non-list where a list is required.
  Pin BOTH halves of the null-sequence-item rule from 1.3 by name, so the deliberate
  asymmetry is carried rather than re-derived: a null `config.checks` item is KEPT as an empty
  `Check` and reported by the validator as a missing `source_key`/`name`/`severity`, while a
  null `config.fields` or `config.quiz` item is DROPPED. Author the dropped half where the key
  is REGISTERED and assert `IsValid` alongside the emptied or shortened list - the reason the
  drop must be carried is that generalising "keep" would fail a document that VALIDATES today,
  so a fixture that does not validate either way proves only "does not throw". That means two
  tests, not one: a `manual` collector with a null `fields` item, and a `training` collector
  with a valid quiz item plus a null one. Add the no-cascade case for an ABSENT `provider`
  beside the unknown-token ones: a `type: integration` collector with no `provider` gets the
  missing-provider diagnostic and NEITHER an unknown-config-key nor a missing-required-config-key
  one, because requiredness is a property of the pair and reporting `(integration, fleet)`'s
  required `checks` without a provider would hard-code one provider's key set. And pin that a
  connection declaring no `provider` is reported once: the collector gets no
  provider-disagreement diagnostic naming the connection's empty value.
  Update `ConfigLoaderTests.cs` for the merged
  kind, the two retired-kind diagnostics, and the valid-kinds enumeration in the
  unknown-kind message no longer naming either retired kind; update
  `IntegrationConnectionValidationTests.cs`, which authors `EvidenceCollector` documents,
  onto the merged kind; rename `EvidenceCollectorFrequencyTests.cs`.
  Run `dotnet test tests/Freeboard.Core.Tests`.

## 2. Persistence: collector merge migration

Commit: `feat(persistence)!: merge evidence_collectors and attestation_templates into one collectors table`

- [x] 2.1 Add `src/Freeboard.Persistence/Migrations/021_collector_merge.sql` with a header
  documenting: forward-only, NOT atomically replay-safe (matching `015`/`018`/`019`/`020`),
  operational recovery is restore-and-rerun, and the disjoint-id assumption across the two
  source tables - and, with it, that a collision is REACHABLE FROM A VALID pre-merge config,
  because duplicate-id detection runs per kind (`ValidateEvidenceCollectors` and
  `ValidateAttestationTemplates` each open their own `seenIds` set, with no set spanning both)
  and each legacy table has its own primary key, so `kind: EvidenceCollector, id: x` plus
  `kind: AttestationTemplate, id: x` validates, syncs, and persists today. State where it
  fails and how to repair it: 2.3's insert aborts on the `collectors` primary key with an
  UNNAMED `ERROR 1062 Duplicate entry '<id>' for key 'collectors.PRIMARY'` (a key name, not a
  rule name, unlike the four refusals), both legacy tables are intact, and a partially
  created `collectors` table holds 2.2's rows; the repair is to rename one of the two ids in
  the AUTHORED config, run `gitops sync` against the pre-merge schema, `DROP TABLE
  collectors`, and re-run. Note that the drop is valid only because this abort precedes 2.4's
  credential re-point: it is not idempotent, and after the re-point it would fail on the live
  foreign key. That an operator should dump `evidence_collectors` and
  `attestation_templates` before running it, because the authored YAML remains the source of
  truth but the dropped `evidence_collectors` table is the only remaining copy of the
  pre-merge free-form `config` map an operator may still be hand-migrating from; that the
  migration REFUSES four classes of already-invalid source row - a non-`integration` row
  carrying `checks` or a `connection_id`, a `type: integration` row whose `checks` is not
  a non-empty JSON array of objects with well-typed known members, and an
  `attestation_templates` row whose `fields` or `quiz` is not a JSON array of objects with
  well-typed known members (all three see 2.1a, all three fail
  before anything is created), and a
  `type: integration` row that carries its `checks` but has a NULL `connection_id` (see 2.2,
  the only REFUSAL that leaves a partially created `collectors` table to drop - the id
  collision above is not a refusal but leaves one too) - and
  how to repair each; and that `gitops sync` is a REQUIRED follow-on step, not an optional one,
  because until it runs the merged table holds three states no authored document backs: the
  placeholder `frequency` on every migrated attestation, which every read surface displays;
  nothing where an
  operator had authored free-form `config` keys, which are not carried across; and TWO
  collector rows for every attestation authored as a collector-plus-template pair on one
  control, which the sync collapses by pruning whichever id the hand-migrated document does
  not keep (2.3).
  `CREATE TABLE IF NOT EXISTS collectors` with `id`, `api_version`, `title`,
  `control_id`, `vendor_id`, `connection_id`, `type`, `provider`, `frequency`, `threshold`,
  `config JSON`, `created_at`, `updated_at` - and NO `checks` column, because checks are a
  `config` key; `utf8mb4_bin` on every id and
  reference column; indexes on the three foreign-key columns; the three
  `ON DELETE RESTRICT` foreign keys named `fk_collectors_control` (to `controls`),
  `fk_collectors_vendor` (to `assets`), and `fk_collectors_connection` (to
  `integration_connections`) - fresh names, because InnoDB constraint names are schema-wide
  and the source tables' constraints live until step 2.4; and a check constraint
  `ck_collectors_integration_provider CHECK (type <> 'integration' OR provider IS NOT NULL)`.
  The check is the migration's refusal guard for a row that cannot derive a provider and it
  keeps the invariant true for every later importer write; `020`'s `ck_scopes_single_target`
  is the precedent. Do NOT pin the reverse half (provider absent on the other four types):
  validation owns it, neither the migration nor the importer can violate it, and pinning it
  would make a future provider-bearing type a migration.
- [x] 2.1a At the TOP of the same migration, before the `CREATE TABLE`, add the
  pre-copy source guard:

  ```sql
  ALTER TABLE evidence_collectors
      ADD CONSTRAINT ck_evidence_collectors_premerge_payload CHECK (
          (type =  'integration'
               AND checks IS NOT NULL
               AND JSON_SCHEMA_VALID(
                       '{"type":"array","minItems":1,"items":{"type":"object","properties":{"SourceKey":{"type":"string"},"Name":{"type":"string"},"Severity":{"type":"string"}}}}',
                       checks))
       OR (type <> 'integration' AND checks IS NULL AND connection_id IS NULL));
  ALTER TABLE evidence_collectors DROP CHECK ck_evidence_collectors_premerge_payload;
  ```

  This is today's `ValidateEvidenceCollectors` type-conditional rule enforced at rest, at the
  LIST level. `ADD CONSTRAINT ... CHECK` validates the existing rows, so a violating row fails
  the migration with `ERROR 3819 Check constraint
  'ck_evidence_collectors_premerge_payload' is violated.` - the failure names the rule, as
  `ck_collectors_integration_provider` does. `JSON_SCHEMA_VALID` is a deterministic built-in
  and is accepted in a MySQL 8.4 CHECK expression (confirmed by adding this exact constraint
  against MySQL 8.4.10 with a row of every refused and every copied shape seeded in turn).
  Keep the
  `checks IS NOT NULL` conjunct: without it a SQL NULL `checks` makes `JSON_SCHEMA_VALID`
  return NULL and the whole branch UNKNOWN, and an UNKNOWN CHECK counts as satisfied, so the
  pre-`018` row would pass. Record in the migration header that this statement is what sets
  the server floor: enforced CHECK is MySQL 8.0.16+ and `JSON_SCHEMA_VALID` is 8.0.17+, so
  `021` requires MySQL 8.0.17 or later. The repo targets 8.4 everywhere and claims support for
  nothing older, so the floor moves by one patch release and breaks nothing - state it anyway
  rather than claiming the migration raises no floor.

  The guard refuses TWO classes, and the step's comment must say so rather than naming only
  one. The non-integration branch is needed because 2.2 composes
  `JSON_OBJECT('Checks', ec.checks)` and carries `connection_id` for EVERY row it copies,
  with no type test: without it a `manual-attestation` row with a stray `checks` column
  lands as a `manual` collector carrying a `Checks` key that `(manual, -)` does not
  register, and a stray `connection_id` lands as a `manual` collector with a connection the
  merged validation forbids. The integration branch refuses a `type: integration` row whose
  `checks` is not a non-empty JSON array OF OBJECTS WITH WELL-TYPED KNOWN MEMBERS - SQL NULL,
  `[]`, a JSON null, a non-array
  value, an array carrying a non-object item (`[1]`, `["x"]`, `[null]`, `[[]]`), or an array
  carrying an object whose `SourceKey`, `Name`, or `Severity` is present with a JSON type
  other than string (`[{"SourceKey": 123, "Name": "MFA", "Severity": "Hard"}]`, `[{"Name": null}]`).
  Each would land a row the target contract cannot hold: a SQL NULL copies as `config = NULL`
  where `(integration, fleet)` makes `checks` required; `[]` or a JSON null copies as a member
  the storage contract says must be omitted; a non-array copies as a `Checks` member the typed
  read cannot bind, which is a store-boundary exception and a 503 on every collector read;
  a non-object item lands the same outcome one level in - a scalar item does not bind to a
  `Check` at all, and a JSON null item binds to a list with a NULL element that the endpoint
  projection and the register page dereference; and a wrong-typed known member lands it one
  level further in still, because `JsonSerializer` IGNORES an unmatched property but THROWS on
  a present one it cannot convert, so `{"SourceKey": 123}` is the same `JsonException` as
  `[1]`, while a JSON-null member binds `null` into a `string` member every downstream
  projection reads as non-null.
  SQL NULL is also the pre-`018` shape - `018` added `connection_id` and `checks` in one
  `ALTER TABLE` and rewrote no rows, so an older integration row has BOTH columns NULL - so
  that row is refused HERE, not later by `ck_collectors_integration_provider`. Say that in the
  comment, because it is the row a reader will expect the provider guard to catch.

  The three member names in the schema are the STORED PascalCase spelling, not the authored
  snake_case one: `ImportPlan` serializes `Check` with default naming and `ImportPlanTests`
  asserts `"SourceKey":"12"`, so the schema must match the column, not the YAML.

  The guard deliberately does NOT enforce a check item's FIELD RULES: it declares no
  `required`, so `[{"nope": 1}]` and `[{}]` pass; no `enum`, so a `Severity` of `"Banana"`
  passes; and no uniqueness, so duplicate `Name` or `SourceKey` values pass. Those travel
  inside a value
  2.2 carries verbatim, they still bind on read, and `gitops validate` names them before and
  after in the same terms. Keep the schema literal to `array` + `minItems: 1` +
  `items` = an object typing only those three members; do not grow it into a restatement of
  the check-item rules, and do
  not add a `JSON_TABLE` re-projection.

  **This predicate is finished; do not tighten it a fourth time.** With the item's type and
  its three known members' token types pinned, and no `required`, no `enum`, and no
  uniqueness, everything left to constrain is a domain rule the config registry and the
  validator own. The residual is acceptable because the required post-migration `gitops sync`
  re-authors every one of these values and `gitops validate` reports the domain rules
  identically before and after the migration. If a future edit finds it cannot express a
  read-boundary rule without also encoding a domain rule, accept the residual and document it,
  or move the check into the sync step - do not grow this SQL again. Say that in the comment.

  The item-type and member-type halves are not the same kind of rule as the field-rule half
  and must not be
  dropped as if they were, so say why in the comment. The claim that a mis-shaped item "still
  binds on read" holds only for an OBJECT item whose present known members carry the right
  token type, where a missing JSON member leaves the
  `Check` record's own empty-string default in place. It does NOT hold for a scalar or a JSON
  null item, nor for an object with a wrong-typed or null known member, and there is no
  store-side normalization to catch any of them: `ConfigLoader` repairs a
  null `checks` item to `new Check()` on the AUTHORING path, but `MySqlComplianceStore` reads
  its JSON columns with a bare `JsonSerializer.Deserialize<List<T>>` and the new
  `DeserializeConfig` binds the same way, with no converter and no normalization. The guard is
  what confines the column to the shapes that bind.

  Do NOT instead add a type or shape test to 2.2's expressions: that
  silently strips the value, which is the disposition this plan already refused when it
  declined to carry the free-form `config` map. The constraint is added and immediately
  DROPPED because its whole job is done the instant the `ALTER` succeeds - nothing else
  writes `evidence_collectors` during the migration - and dropping it keeps the file
  replayable after a repair: a failed `ADD` leaves no constraint behind and a successful one
  leaves none either, so a re-run cannot fail on a duplicate constraint name. Do NOT pin
  this rule permanently on `collectors`: which `(type, provider)` pair may carry a `checks`
  key, and whether that key is required, are the config registry's rules, so a permanent
  constraint would make a future registry
  edit a migration - the same reasoning 2.1 gives for not pinning the reverse provider half.
  State in the step's comment that every refused row is already invalid under the pre-merge
  rules and is repaired by a `gitops sync` against the pre-merge schema or by deleting it,
  and that because this guard runs before the `CREATE TABLE` a refused migration leaves the
  schema untouched and needs no cleanup before the re-run.

  In the SAME step, add the matching guard on `attestation_templates`, because 2.3 re-shapes
  `fields` and `quiz` into `config` by exactly the mechanism 2.2 re-shapes `checks` - the
  column value is carried across verbatim - so `[1]`, `[null]`, `[[]]`, and
  `[{"Label": 123}]` in either column reach the same typed read and fail it the same way, for
  the same 503 on every collector read. Guarding one re-shaped column and not the other two
  would make the read-boundary guarantee hold for integration collectors alone, and it would
  contradict the refusal rule's own clause 1, which scopes refusal to the columns the merge
  RE-SHAPES:

  ```sql
  ALTER TABLE attestation_templates
      ADD CONSTRAINT ck_attestation_templates_premerge_payload CHECK (
          (fields IS NULL OR JSON_SCHEMA_VALID('{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Label":{"type":"string"},"Type":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}', fields))
      AND (quiz  IS NULL OR JSON_SCHEMA_VALID('{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Prompt":{"type":"string"},"Answer":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}', quiz)));
  ALTER TABLE attestation_templates DROP CHECK ck_attestation_templates_premerge_payload;
  ```

  Same scope and the same terminus: item type plus each known member's JSON token type, no
  `required`, no `enum`, no uniqueness. Type the nested `Options` list - it is the one nested
  member that is itself a collection, and a non-string element or a JSON null there does not
  bind either. Two deliberate differences from the `checks` predicate, both following from the
  merged rules rather than from taste: NO `minItems`, because neither list is required by any
  registered schema, so an empty array reads back cleanly and only carries the cosmetic defect
  of a member the storage contract would have omitted; and NO `IS NOT NULL` conjunct, because
  SQL NULL is the normal valid state of both columns. `body` and `pass_mark` need no guard -
  `TEXT` binds to `string?` for any content and `INT` to `int?`.

  Say in the comment that this guard is also what makes 2.3's empty-config `CASE` exact: a
  JSON null LITERAL is not SQL NULL, so without it such a value would pass the
  all-columns-NULL test and then be deleted by the merge patch, landing `config = '{}'` where
  the storage contract requires SQL NULL. Only `fields` and `quiz` are JSON columns and an
  array schema rejects a JSON null, so do NOT also widen 2.3's `CASE` with a
  `JSON_TYPE(...) = 'NULL'` test - that is a second expression guarding a shape this guard has
  already made unreachable.
- [x] 2.2 In the same migration: `INSERT ... SELECT` from `evidence_collectors`
  `LEFT JOIN integration_connections ic ON ic.id = ec.connection_id`, carrying `id`,
  `api_version`, and `title` (all NOT NULL on `collectors`), re-tokenizing `type`
  with a `CASE` (`manual-attestation` -> `manual`, `training-attestation` -> `training`,
  else unchanged) and setting `provider = CASE WHEN ec.type = 'integration' THEN ic.provider
  ELSE NULL END`; compose the new `config` from the old `checks` column ALONE, as
  `CASE WHEN ec.checks IS NULL THEN NULL ELSE JSON_OBJECT('Checks', ec.checks) END` - the
  `CASE` is required because `JSON_OBJECT` writes a JSON null for a NULL value instead of
  returning NULL, and a NULL `config` with a non-NULL `checks` is the common integration
  shape, not an edge case. The expression carries no `type` test and no shape test, and must
  not gain either:
  2.1a has already refused every non-integration row with a non-NULL `checks`, so a
  non-NULL `checks` here implies `type = 'integration'`, and it has refused every
  integration row whose `checks` is not a non-empty array of objects with well-typed known
  members, so the composed `Checks`
  member is always a non-empty array whose items bind. Either test here would silently
  strip a value the migration is meant to refuse. Carry `frequency`, `threshold`, both timestamps, and ALL THREE
  foreign-key columns - `control_id` and `vendor_id` from `012`, `connection_id` from `018` -
  verbatim. Name the three explicitly: a shorthand drops `connection_id`, and
  `ck_collectors_integration_provider` cannot catch that, because `provider` comes from the
  join rather than from the carried column - every integration collector would land with a
  null connection, the startup unresolvable-token scan would see nothing, and the widened
  fingerprint (4.5) would hash a null connection. With step 2.3, the two inserts must account
  for all thirteen columns of `collectors`; check them off column by column against the
  `012`/`018` source schema before moving on. Do NOT carry the old free-form `config` map: it accepts any ad-hoc
  key, so persisting it would seed the new closed column with unregistered and possibly
  credential-shaped keys, and a carried string value colliding with a typed config member
  (a `pass_mark` of `"90"`, or a key named for the checks/fields/quiz list) would fail the
  typed read of the column and take every collector read to a 503 until a sync repaired the
  row. Operators hand-migrate those keys into their authored documents. Note the key name
  `'Checks'` is the config model's member name, matching what `evidence_collectors.checks`
  already stores per item, so the column value is carried across verbatim. Expect this step
  to FAIL on `ck_collectors_integration_provider` when a source row is
  `type = 'integration'` with a NULL `connection_id` AND a `checks` that passed 2.1a - a
  hand-edited row, since the pre-`018` shape has no `checks` either and 2.1a has already
  refused it. That is the intended refusal, not a
  defect. Such a row is already invalid under the pre-merge rules (an integration collector
  requires a `connection`), the operator repairs it with a `gitops sync` against the
  pre-merge schema or by deleting the row, and because this is the first copy step nothing
  has been dropped when it fails. Of the four REFUSALS this is also the only one that leaves
  a partially
  created `collectors` table to drop before the re-run - say "the only refusal", not "the only
  way `021` can abort with `collectors` populated", because the id collision in 2.3 leaves one
  too. Note in the same comment that dropping that table is valid only before 2.4's credential
  re-point; afterwards the live foreign key blocks it. State all of that in the step's comment
  so a future reader does not "fix" the guard away.
- [x] 2.3 In the same migration: `INSERT ... SELECT` from `attestation_templates`, carrying
  `id`, `api_version`, `title`, `control_id`, `type`, `created_at`, and `updated_at` (all
  NOT NULL on `collectors`, and all present on the source table - `type` is already `manual`
  or `training` on the source, and carrying it verbatim is what keeps
  `ck_collectors_integration_provider` from firing on this path), composing
  `config` from `body`/`fields`/`pass_mark`/`quiz` with `JSON_OBJECT` under the member-name
  keys `'Body'`/`'Fields'`/`'PassMark'`/`'Quiz'`, dropping the members whose source column
  is NULL (an absent member must be omitted, not written as JSON null - either a
  `JSON_REMOVE` with a per-column conditional path or, more simply,
  `JSON_MERGE_PATCH(JSON_OBJECT(), JSON_OBJECT(...))`, whose merge-patch semantics delete
  every null-valued member) and wrapping the whole composition in a `CASE` that yields SQL
  NULL when all four source columns are NULL. Without that wrapper the row lands with `{}`,
  which contradicts the storage rule that an empty config is SQL NULL - the same JSON
  NULL-boundary defect the `CASE` in 2.2 exists to avoid, one level further out. Carry
  `pass_mark` as the INT the source column holds so it stores as a JSON number, matching
  what `ImportPlan` writes (3.1) and what the read model binds to; do not cast it to a
  string. Leave `vendor_id`/`connection_id`/`provider`/`threshold`
  NULL, and writing `frequency = 'annual'` with a comment stating it is a placeholder the
  next `gitops sync` overwrites, that the sync is required rather than optional because
  until it runs the read API, the register page, and the CLI listing all display a cadence
  no authored document backs (the SoA drill-down does not: an attestation-tagged check is
  projected with a null cadence, per 4.4), and that `annual` is
  chosen as the longest window so the pre-sync display is permissive rather than
  alarm-generating. Do NOT claim in the comment that the placeholder is observable through
  ingest staleness: only template rows receive it, they have no vendor and can hold no
  credential, and ingest requires a vendor - the collectors that can ingest come through
  2.2 with their own authored cadence.
  State in the same comment that this step deliberately does NOT try to merge a template
  with the `manual-attestation`/`training-attestation` collector on the same control. Nothing
  in the source schema links them - neither table references the other, the pairing is the
  shared `control_id` alone, and neither side is required - so a control with two templates
  and one collector has no pairing the data expressed and the surviving id would be an
  arbitrary choice, while that id is what credentials, scheduler state, and evidence runs are
  keyed on. The pair therefore lands as two rows and the required sync collapses it. Do not
  refuse the rows either: both are valid under the pre-merge rules, and a migrated
  `training` row lands with no `pass_mark` and no `quiz` only because `evidence_collectors`
  has no column that could carry a form - refusing would block a healthy database. The
  form-less row still reads cleanly (an absent `config` deserializes to an empty typed
  config), is never scheduled, and has no runtime that reads the missing form, which is
  what makes copying it acceptable. Do not write "is never acted on" in the comment: a row
  copied by 2.2 keeps its vendor and its credential, so it MAY still ingest evidence -
  unchanged behaviour that reads the vendor, the control's mapped requirements, and the
  cadence, never the form.
- [x] 2.4 In the same migration: drop and re-add the `collector_credentials` collector
  foreign key against `collectors (id) ON DELETE CASCADE` - this MUST precede the drops
  below, or `DROP TABLE evidence_collectors` fails on the live constraint; then
  `DROP TABLE evidence_collectors; DROP TABLE attestation_templates;`. Add NO foreign key
  from `collector_scheduler_state.collector_id` or `evidence_runs.collector_id`.

## 3. Persistence: merged row plan, importer, read store, and read models

Commit: `feat(persistence)!: read and import one merged collector set`

- [x] 3.1 In `src/Freeboard.Persistence/GitOps/ImportPlan.cs`: replace
  `EvidenceCollectorRowPlan` and `AttestationTemplateRowPlan` with one `CollectorRowPlan`
  (`Id`, `ApiVersion`, `Title`, `Control`, `Vendor?`, `Connection?`, `Type`, `Provider?`,
  `Frequency`, `Threshold?`, `ConfigJson?`) - no separate `ChecksJson`, since checks are a
  `config` key; collapse the two id lists into `CollectorIds`.
  `ConfigJson` is the storage contract, not a plain serialization of `CollectorConfig`: the
  authored record holds `PassMark` as raw text and holds every member non-null, while the
  column omits absent members and stores `PassMark` as a number. So add one internal
  storage record in `Freeboard.Persistence` - nullable `Body`, `Fields`, `PassMark` as
  `int?`, `Quiz` (keeping each item's `Answer`), `Checks`. Declare ONE shared
  `JsonSerializerOptions` alongside that record - default naming, no policy,
  `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` - and use that same
  instance on both the write side here and the read side in 3.4, so the two cannot drift;
  these are deliberately NOT the serializer defaults, so do not describe them as such
  anywhere. Map an empty list to null AND a blank string to null before serializing, exactly
  as today's `SerializeList` returns null for an empty list and `NullIfBlank(t.Body)`
  returns null for a blank body - `Body` is the only scalar member that can be blank, since
  `PassMark` is already `int?` - so the storage rule and the wire rule stay one rule.
  `ConfigJson` is null when every
  member is absent. Parse `PassMark` to `int?` here with the same helper `Threshold` uses.
  That record is the single shape 3.4's `DeserializeConfig` also binds, which is what makes
  writer and reader agree by compilation rather than by comment. Keep the default
  `JsonSerializer` naming (no policy): the stored keys are the C# member names,
  which is exactly what the pre-merge `checks`/`fields`/`quiz` columns already hold and what
  migration `021` composes, and it is what lets the migration carry those column values
  across without rewriting each item's keys. Snake_case is a wire concern, applied by the
  endpoint projection in 4.1, not a storage concern.
- [x] 3.2 In `src/Freeboard.Persistence/GitOps/MySqlGitOpsImporter.cs`: replace the two
  upserts with one `UpsertCollectorsAsync` (upsert by id, ordered after controls, declared
  assets, and integration-connections) and the two `DeleteAbsentAsync` calls with one over
  `collectors`, keeping it before the integration-connection prune, the declared-asset
  prune, and the control/requirement/standard deletes. Update the class doc comment's
  order narrative.
- [x] 3.3 In `src/Freeboard.Persistence/ComplianceReadModels.cs`: replace
  `EvidenceCollectorRow` and `AttestationTemplateRow` with `CollectorRow` and
  `CollectorConfigView` (`Body`, `Fields`, `PassMark`, `Quiz`, `Checks`); keep
  `QuizItemView` (no `Answer`) unchanged - its absent `Answer` is the whole redaction
  boundary; carry one `Collectors`
  list on `SoaDrilldownInputs` and one `Collectors` count on `ComplianceCounts`. Give
  `CollectorConfigView`'s five members explicit `[JsonPropertyOrder]` values with a comment
  saying why: the scheduler fingerprint (4.5) serializes this record and is persisted and
  compared across upgrades, and the serializer's reflection member order is not guaranteed.
  Nothing else serializes this record - the endpoint hand-writes its own projection - so the
  attributes affect the fingerprint alone. Pinning the view's own five members is NOT
  enough, and stopping there is the defect to avoid: `Fields`, `Quiz`, and `Checks` are
  lists of `AttestationField`, `QuizItemView`, and `Check`, emitted by the same
  reflection-based resolver, and on a fingerprinted (integration) collector the nested
  `Check` items are the ONLY part of the input that varies. So pin every member of every
  record the serialization reaches: `QuizItemView` here (3 members), and
  `AttestationField` (4) and `Check` (3) in `src/Freeboard.Core/GitOps/ConfigModel.cs`.
  The two Core records take a `using System.Text.Json.Serialization;` that
  `Freeboard.Core` does not have today - a BCL attribute, no package dependency - and the
  comment on them should say plainly that their member ORDER is pinned because their
  serialization feeds a persisted hash, next to the existing fact that their member NAMES
  are already the persisted `config` column's key literals. Do NOT instead hand-build the
  fingerprint's hash input: that trades a declarative attribute for imperative code whose
  failure mode is worse - a member left out of a hand-written builder silently drops an
  operator's repair out of the recovery trigger, while a missing attribute only
  re-fingerprints once and self-heals.
- [x] 3.4 In `src/Freeboard.Persistence/IComplianceStore.cs` and `MySqlComplianceStore.cs`:
  replace the two reads with `GetCollectorsAsync` selecting from `collectors` ordered by
  `id`; move the existing `DeserializeQuiz` redaction into a `DeserializeConfig` that
  deserializes the `config` column and projects every quiz item to `QuizItemView`, keeping
  it the single store-boundary redaction; update the drill-down snapshot read and the counts
  query to the one table. `DeserializeConfig` binds the same storage record `ImportPlan`
  writes (3.1) through the same shared `JsonSerializerOptions` instance declared there - not
  the serializer defaults - then projects it to
  `CollectorConfigView` - dropping each quiz item's `Answer` and taking `PassMark` as the
  `int?` it is stored as - so the column round-trips without a converter and without a
  second hand-written key mapping.
- [x] 3.5 In `src/Freeboard.Persistence/MySqlEvidenceStore.cs`: update the
  `EvidenceCollectorFrequency.IsStale` call site to `CollectorFrequency.IsStale`. Rename
  only - the staleness rule is unchanged.
- [x] 3.6 Update `tests/Freeboard.Persistence.Tests/ImportPlanTests.cs` for the merged row
  plan (including that the serialized `config` uses the C# member names, omits absent
  members - covering a blank `body` and an empty list alike - is null when every member is
  absent, and writes `PassMark` as a JSON number
  rather than the authored string, plus a write-then-read round trip through
  `DeserializeConfig`), and add MySQL integration coverage in `MySqlIntegrationTests.cs` (and a new
  merge test) for: the merged schema, its three foreign keys, and the ABSENCE of a `checks`
  column; round-trip of an `integration`,
  `script`, `manual`, and `training` collector; the answer present in the stored JSON and
  absent from the read model; one collector count; the FK-safe prune order for a removed
  control, vendor asset, and connection; the credential cascade from `collectors`; and the
  `021` migration applied over seeded pre-merge rows in both legacy tables (re-tokenized
  types, derived provider, the old `checks` column folded under a `config` `Checks` key,
  the old template columns folded under their keys, the credential FK re-pointed before the
  drops, both legacy tables gone, no new foreign key on
  scheduler state or evidence runs). Seed the awkward source rows too, not just the happy
  ones: an `evidence_collectors` row with NULL `config` and non-NULL `checks` (the common
  integration shape - the checks must survive); one with both NULL (must land with `config`
  NULL, not `{"Checks": null}`); one whose old free-form `config` held keys (those keys must
  be ABSENT from the merged column); an `attestation_templates` row with a `pass_mark` (must
  land as a JSON number the read store binds to `int?`); and one with every optional column
  NULL (must land with `config` SQL NULL, not `{}` and not JSON-null members). Add four
  separate refusal tests, one per refused class. First: seed a `type: integration` row with a
  NULL `connection_id` AND a non-empty `checks` array, apply
  `021`, and assert it fails on `ck_collectors_integration_provider`, the migration version
  is not recorded, and both legacy tables and their rows still exist (the guard fires before
  any drop). The `checks` must be seeded: without it the row trips
  `ck_evidence_collectors_premerge_payload` at 2.1a instead, so the minimal seed would assert
  the wrong constraint and fail. Second: seed a `type: manual-attestation` row with a non-NULL
  `checks`, and separately a `type: script` row with a non-NULL `connection_id`, apply `021`,
  and assert
  each fails on `ck_evidence_collectors_premerge_payload`, the version is not recorded, the
  `collectors` table was never created (the guard runs before the `CREATE TABLE`), and the
  legacy rows are untouched - so a re-run after the repair needs no cleanup. Third: seed a
  `type: integration` row whose `checks` is SQL NULL (with `connection_id` NULL too - the
  pre-`018` shape), one whose `checks` is `[]`, one whose `checks` is a JSON null, one
  whose `checks` is a JSON object rather than an array, one whose `checks` is `[1]` (an array
  with a SCALAR item), one whose `checks` is `[null]` (an array with a JSON NULL item), and one
  whose `checks` is `[{"SourceKey": 123, "Name": "MFA", "Severity": "Hard"}]` (an OBJECT item
  with a wrong-typed KNOWN member);
  assert each fails on
  `ck_evidence_collectors_premerge_payload` with `collectors` never created, so the pre-`018`
  row is shown to be refused by the source guard rather than by the provider constraint. Name
  the `[1]`, `[null]`, and wrong-typed-member tests for the shape they refuse, because they
  are the three the guard
  gained specifically to make the read-boundary claim true: `[1]` does not bind to a `Check`
  at all, `[null]` binds to a NULL list element, and a present member of an incompatible JSON
  type is a `JsonException` where an UNMATCHED member is merely ignored.
  Pin what the guard deliberately lets through alongside it: a `type: integration` row whose
  `checks` is `[{"nope": 1}]` - a non-empty array of OBJECT items whose known members are
  simply ABSENT -
  migrates, lands with the array carried verbatim under the `config` `Checks` key, and reads
  back through `GetCollectorsAsync` without error as a `Check` with empty members - the guard
  types the three known members, it does not require them, restrict their token set, or
  enforce their uniqueness.
  Fourth, the template guard: seed an `attestation_templates` row whose `fields` is a JSON
  null, one whose `quiz` is a JSON object rather than an array, one whose `fields` is `[1]`,
  one whose `quiz` is `[null]`, and one whose `fields` is
  `[{"Id": "f1", "Label": 123, "Type": "boolean"}]`; assert each fails on
  `ck_attestation_templates_premerge_payload` with `collectors` never created. Pin what it
  lets through with them: a row whose `fields` is `[]` and one whose `quiz` is `[{"nope": 1}]`
  both migrate, land with the column value carried verbatim under its `config` key, and read
  back through `GetCollectorsAsync` without error - the guard has no `minItems` because
  neither list is required by any registered schema, and it types the known members without
  requiring them. Pin the JSON-null consequence explicitly, since it is what makes 2.3's
  empty-config `CASE` exact rather than approximate: a template row whose `fields` and `quiz`
  are both the JSON null LITERAL and whose `body` and `pass_mark` are SQL NULL is REFUSED
  here, so no row can reach 2.3 and land `config = '{}'`.
  Pin the happy
  path of the same guard with them: a `manual-attestation` row with NULL `checks` and NULL
  `connection_id` alongside an `integration` row carrying a non-empty `checks` array and a
  `connection_id` migrates normally, and
  `evidence_collectors` carries no leftover constraint if the migration later fails, so the
  file replays. Add a separate split test for the accepted transient (2.3): seed a
  `training-attestation` `evidence_collectors` row and an `attestation_templates` row on the
  SAME control, apply `021`, and assert the migration SUCCEEDS and leaves two `type:
  training` rows on that control - one with the authored `frequency`, its vendor, and
  `config` SQL NULL, one with the form and the placeholder `frequency` - and that the
  form-less row reads back through `GetCollectorsAsync` without error. Pin it so a future
  reader sees the split was decided, not missed. Add one more for the ID COLLISION, which is
  an abort rather than a refusal and needs its own test precisely because it is reachable from
  a VALID pre-merge config: seed an `evidence_collectors` row and an `attestation_templates`
  row that SHARE an id, apply `021`, and assert the migration FAILS, the migration version is
  not recorded, and both legacy tables and all their rows are intact. Assert the failure by
  its OUTCOME - those three facts - and by nothing else: unlike the four refusals it names a
  key (`collectors.PRIMARY`), not a rule, so there is no stable name to assert. The test must
  NOT match the message text at all, not even a substring of the duplicate-entry wording. The
  `1062` text is the volatile part - the key-name spelling in it has already changed across
  MySQL majors - so a text assertion is the brittle option here and the outcome assertion is
  the durable one. Also move
  `IntegrationConnectionIntegrationTests.cs`, `CollectorCredentialIntegrationTests.cs`, and
  `AssetUnificationIntegrationTests.cs` onto the merged kind and table - each seeds or
  asserts against `evidence_collectors` today. Run
  `dotnet test tests/Freeboard.Persistence.Tests` with `FREEBOARD_TEST_DB` set.

## 4. Web: merged endpoint, register page, nav, SoA, scheduler, ingest, credentials

Commit: `feat(web)!: serve one collector register, endpoint, and credential route`

- [x] 4.1 In `src/Freeboard/Compliance/ComplianceEndpoints.cs`: replace the two list
  endpoints with `GET /api/v1/freeboard/collectors` projecting `id`, `title`, `control`,
  `vendor`, `type`, `provider`, `frequency`, `threshold`, and the typed `config` (with
  `pass_mark` and `source_key` snake_case, an integration collector's `checks` included,
  no quiz `answer`, and no `connection`). Inside `config`, write a key only when the member
  is present - a non-blank `body`, a non-empty `fields`, a non-null `pass_mark`, a non-empty
  `quiz`, a non-empty `checks` - so a `script` or `agent` collector serializes
  `"config": {}` and a `training` collector carries no `checks` key. Default serialization
  of the fixed five-member view would emit all five keys on every collector, which
  contradicts the requirement that `config` be empty where the schema accepts no key. Do
  NOT extend the omission to the top level: `vendor`, `provider`, and `threshold` stay
  explicit `null` when unset. Update the
  `/compliance/status` `persisted` object to one `collectors` key in both the healthy and
  the all-null degraded shapes.
- [x] 4.2 Rename `src/Freeboard/Pages/Compliance/EvidenceCollectors.cshtml(.cs)` to
  `Collectors.cshtml(.cs)` with `@page "/settings/collectors"`, fold the attestation
  rendering (body HTML-encoded, fields, pass mark, quiz prompts and options) into the
  per-collector block, add an integration collector's `config` checks (name and severity)
  to the same block, and delete `AttestationTemplates.cshtml(.cs)`. Keep the file in the
  `Pages/Compliance` folder so `AuthorizeFolder("/Compliance")` still gates it.
- [x] 4.3 In `src/Freeboard/Navigation/ShellNavCatalog.cs`: replace the two nav items with
  one `ShellNavItem("collectors", "Collectors", "/settings/collectors", "Platform")`.
- [x] 4.4 In `src/Freeboard/Compliance/StatementOfApplicability.cs`: take one collector list
  in `ResolveDrilldown`, deriving `SoaCheckKind` from `type` (`manual`/`training` ->
  `Attestation`, else `Collector`); keep the wire values, the `(Kind, Id)` ordering, and the
  "only collector checks carry a status" rule. Derive `SoaCheckNode.Frequency` in the SAME
  expression as the tag: null for an attestation-tagged check, the collector's own
  `frequency` otherwise. Without this, every collector now having a required `frequency`
  would start rendering a cadence on attestation checks, which the page currently never
  shows; a cadence beside a check that carries no evidence status is a collection promise
  the page cannot back. Keep `Vendor` uniform for both tags - it is metadata that plays no
  part in status interpretation, and a `manual` collector with a vendor already renders it.
  `ControlDetailProjection.cs` needs no
  behaviour change (it reads a check's title, kind, and status, never its cadence); update
  it only where it names the removed types. Its RENDERING does change on two surfaces, and
  4.8 pins that: it attaches a status and a status-derived note only where
  `check.Kind == SoaCheckKind.Collector` and falls through to a bare `Note: "Attestation"`
  otherwise, and both the list page's inline drawer templates and the full-page control detail
  call it, so a check from a pre-merge `manual-attestation`/`training-attestation` collector
  loses its status in the drawer and on the control detail page as well as on the SoA page.
  Do not "fix" that by widening the branch - the derived tag is the decision (see D7), and no
  `web-object-drawer` delta is needed because that spec sources per-check status from the
  per-collector evidence-status read and states no kind rule.
- [x] 4.5 In `src/Freeboard/Scheduler/CollectorSchedulerService.cs` and
  `IScheduledCollectorRunner.cs`: read `GetCollectorsAsync`, keep the `type == "integration"`
  claim filter, take a `CollectorRow` on the runner seam, and widen the fingerprint from
  `SHA-256("{Type}\n{Frequency}")` to a SHA-256 over type, frequency, provider, connection,
  and the row's typed `CollectorConfigView` (excluding
  `threshold`, a scoring input). Pin the layout: SHA-256 over the UTF-8 bytes of
  `"{type}\n{frequency}\n{provider}\n{connection}\n{configJson}"` - those five parts in that
  order joined by `\n`, extending the existing two-part form rather than inventing a second
  one - with a null `provider` or `connection` rendering as the empty string. Serialize the
  view with default `JsonSerializer` options (every member emitted). This depends on the
  `[JsonPropertyOrder]` values 3.3 adds to `CollectorConfigView` AND to every nested item
  record the serialization reaches (`Check`, `AttestationField`, `QuizItemView`): the
  reflection-based resolver's member order is unspecified, and this fingerprint is persisted
  in `collector_scheduler_state.config_fingerprint` and compared across restarts and
  upgrades, so an order that changed between builds would change the comparison. The nested
  pinning is the load-bearing half here, because on an integration collector the nested
  `Check` items are the only part of the input that varies. Do NOT reuse the
  endpoint's omit-absent projection (4.1) or add a third projection: a hash input is not a
  document, nothing reads it, and binding it to the response shape would let a cosmetic
  response change revive every dead row once. Hash the typed view the read model already
  carries - do NOT
  add a raw config-JSON member to `CollectorRow` and do NOT read the column text: the store
  deserializes and discards it, and a raw member would carry the training quiz `answer` into
  the scheduler for no gain. Nothing is lost, because only integration collectors are
  fingerprinted and their whole registered config is `checks` - which is also why the
  always-emitted absent members cannot collide here. Update the method
  comment to say why the scope widened: an operator fixing a bad `source_key` or repointing
  a `connection` must revive a dead or errored row, and both are now `config`/`connection`
  edits.
- [x] 4.6 In `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs`: move the two routes to
  `/api/v1/freeboard/collectors/{id}/credentials[/{credId}]`, unchanged in permission,
  read-only behaviour, and status codes. In `EvidenceIngestEndpoints.cs`: read the merged
  collector; behaviour unchanged.
- [x] 4.7 In `src/Freeboard/Program.cs`: point the startup unresolvable-token warning scan at
  the merged collector read, still filtering `Type == "integration"` with a non-empty
  connection and logging only the connection id.
- [x] 4.8 Update the web tests: fold `EvidenceCollectorsPageTests.cs` and
  `AttestationTemplatesPageTests.cs` into `CollectorsPageTests.cs` (render, empty state,
  anonymous redirect, read-only mode, store-unreachable notice, zero-grant visibility,
  HTML-encoded body, no answer in the markup, an integration collector's checks rendered,
  retired routes unmapped). Record in that file that the old per-entry
  `data-config-key="<key>"` attribute is gone: it existed to label an open, page-unknown key
  set, the merged block renders five named sections instead, and it is not one of the
  preserved markers - so this is a deliberate markup deletion, not a missed assertion.
  Update
  `ComplianceEndpointTests.cs` (adding the omit-absent `config` cases: a `script`
  collector's `config` is `{}`, a `training` collector's carries no `checks` key, an
  `integration` collector's carries no attestation key, and the top-level nullables are
  still explicit `null`), `CollectorCredentialEndpointTests.cs`,
  `CollectorSchedulerServiceTests.cs` (adding the widened-fingerprint cases: a `config`,
  `provider`, or `connection` edit revives a dead/error row, changing ONLY a check's
  `SourceKey` inside `config` changes the fingerprint, a `threshold` edit does not,
  and a healthy row's `next_due_at` is untouched), `EvidenceIngestEndpointTests.cs`,
  `IntegrationConnectionsTests.cs` (it authors `EvidenceCollector` documents today),
  `FakeComplianceStore.cs`, `FakeEvidenceStores.cs`, `FakeScheduledCollectorRunner.cs`,
  `ControlDetailPageTests.cs` and `ObjectDrawerRenderTests.cs` - and name the status-loss case
  in both, because these two surfaces change what they render even though
  `ControlDetailProjection` changes no behaviour: a check derived from a pre-merge
  `manual-attestation` or `training-attestation` collector now tags as an attestation, so the
  shared projection gives it the bare "Attestation" note instead of the evidence status and
  status-derived note it gives a collector-tagged check. Assert that a `manual`/`training`
  collector's proving-checks row carries no status in the drawer and on the full page, and
  that an `integration` collector's still does, so the loss is a pinned decision rather than a
  test that quietly stopped covering it -
  `StatementOfApplicability*Tests.cs` (adding the derived-tag cases AND the null cadence on
  an attestation-tagged check: a `manual` or `training` collector with a `frequency`
  projects none, an `integration` collector projects its own; plus one ordering assertion,
  because the kind-then-id sort makes the derived tag move a row: on a control carrying an
  `integration` collector and a `manual` collector whose id sorts FIRST, the integration
  check is projected first, where the same pair authored pre-merge as an integration and a
  `manual-attestation` collector ordered the other way), `ShellNavCatalogTests.cs`,
  `ShellRouteReachabilityTests.cs`, `ShellChromeRenderTests.cs`,
  `RouteAuthzMetadataTests.cs`, and `OrgSelectionTests.cs`. Run
  `dotnet test tests/Freeboard.Web.Tests`.
- [x] 4.9 Update `tests/Freeboard.WebE2E` (`AccessibilityAuditE2ETests.cs`,
  `DrawerE2ETests.cs`, `StatementOfApplicabilityE2ETests.cs`) for the single
  `/settings/collectors` route, leaving the preserved markers intact.

## 5. CLI: one collector command and one API client shape

Commit: `feat(cli)!: fold attestation-template list into collector list`

- [x] 5.1 In `src/Freeboard.CLI/IFreeboardApiClient.cs` and `HttpFreeboardApiClient.cs`:
  replace `ApiEvidenceCollector` and `ApiAttestationTemplate` with one `ApiCollector`
  carrying an `ApiCollectorConfig` (`body`, `fields`, `pass_mark`, `quiz`, `checks`); keep
  `ApiAttestationField` and `ApiQuizItem` and add an `ApiCheck` (`source_key`, `name`,
  `severity`) - every `ApiCollectorConfig` member must tolerate being absent from the
  response, since the endpoint omits absent members (4.1); replace the two list methods with
  `ListCollectorsAsync` calling `GET /collectors`; point the credential methods at
  `/collectors/{id}/credentials[/{credId}]`.
- [x] 5.2 In `src/Freeboard.CLI/CollectorCommands.cs`: print each control with its
  evaluation and, under it, each collector's type, provider, vendor, frequency, and
  threshold, plus - from `config` - an integration collector's tracked checks (name and
  severity) or a manual or training collector's `has body` / `no body` indicator, fields,
  pass mark, and quiz prompts and
  options; print no answer. The body indicator is what the retired
  `attestation-template list` prints today and what the requirement it satisfies names, so
  carry it over rather than dropping it - it is also the only consumer of
  `ApiCollectorConfig.body`, since the markdown itself is rendered by the register page and
  not by the CLI. Delete `src/Freeboard.CLI/AttestationTemplateCommands.cs` and
  its group registration in `Program.cs`.
- [x] 5.3 In `src/Freeboard.CLI/GitOpsCommands.cs`: report one collector count in the
  validate summary and the sync line, and one `Collectors` section in the
  `apply --dry-run` planned state (id, title, control, vendor, type, provider, frequency),
  still omitting body, fields, quiz, and answers.
- [x] 5.4 Update the CLI tests: fold `AttestationTemplateCommandTests.cs` into
  `CollectorCommandTests.cs` (merged listing incl. a training collector's quiz prompts, the
  has-body / no-body indicator on a collector with and without a body, and
  no answer, the removed command group, the exit-code matrix), and update `Fakes.cs`,
  `GitOpsCommandTests.cs`, and `SyncMySqlIntegrationTests.cs` onto the merged kind,
  adding representative provider-mismatch and unknown-config-key rejections at the command
  surface. Run `dotnet test tests/Freeboard.CLI.Tests`.

## 6. Docs and in-repo fixtures

Commit: `docs(gitops)!: document the merged Collector kind and its typed config`

- [x] 6.1 Rewrite the `EvidenceCollector` and `AttestationTemplate` sections of
  `docs/gitops.md` into one `Collector` section: the field list, the five type tokens, the
  rule that top-level fields are identity/attach point/references/cadence while every
  type-specific payload lives in `config`, the `provider`/`connection` top-level
  integration rules plus the `config` `checks` the registered `(integration, fleet)` schema
  requires, a per-`(type, provider)` table of
  accepted and required `config` keys with the "any other key is rejected" statement, and
  worked `integration`, `manual`, and `training` examples. Update the supported-kinds list,
  the `kind` enumeration under Format, and the Fleet/Freeboard noun table (one `collectors`
  row replacing the two).

  Close the section with the operator hand-migration list, because `gitops sync` is a
  REQUIRED migration step and will refuse a config that still carries any of these:
  both retired kinds become `kind: Collector`; `manual-attestation`/`training-attestation`
  become `manual`/`training`; a template's `body`/`fields`/`pass_mark`/`quiz` and an
  integration collector's `checks` move under `config`; a former template gains a
  `frequency`; an integration collector gains a `provider` matching its connection's; a
  collector-plus-template PAIR on one control becomes ONE document (keep the collector's id
  where a machine credential hangs off it); **a `Control` whose only proving mechanism was an
  `AttestationTemplate` must gain an `evaluation` rule**, because `evaluation` is now required
  on a control with at least one attached collector of ANY type and such a control needed
  none pre-merge; and ids must be unique across the merged kind, so a collector and a
  template that share an id without being a pair need one renamed.
- [x] 6.2 Update `docs/evidence-ingest.md` for the renamed credential route, and
  `collectors/README.md` where it names the retired kinds or routes.
- [x] 6.3 Sweep the repo for any remaining `EvidenceCollector`, `AttestationTemplate`,
  `evidence_collectors`, `attestation_templates`, `evidence-collectors`,
  `attestation-templates`, `evidence-collector`, or `attestation-template` occurrence and
  migrate or remove it. The two SINGULAR hyphenated nouns are on the list deliberately: they
  are the form used in prose and in doc comments (`collectors/README.md` uses only the
  singular, as do several Core, web, and CLI doc comments), so a sweep without them is not
  the complete grep it looks like. EXCLUDING four paths that
  must keep the old names:
  - `src/Freeboard.Persistence/Migrations/0*.sql` for every ordinal below `021`, and their
    file names. `012`, `013`, `014`, `016`, `018`, and `019` create, alter, and reference
    the two legacy tables by name, and `015`'s file name carries one. They are the history
    `021` runs on top of: a fresh install applies them in order, so renaming a table in them
    would make `021` fail on a table that no longer gets created. A migration that has
    shipped is never edited.
  - The REQUIREMENT BODIES under `openspec/specs/**`, which are regenerated from this
    change's deltas when they are applied and must not be hand-edited; a hand-fix there
    would be overwritten and would also diverge from the deltas that are the actual source.
    The exclusion stops at requirement bodies: everything ABOVE the first
    `### Requirement:` header - the title and the `## Purpose` paragraph - is NOT
    regenerated from any delta and IS in scope for the sweep (6.5 carries the one
    occurrence).
  - `openspec/changes/archive/**`, the record of what past changes did.
  - This change's own directory, which necessarily names both retired kinds while removing
    them.
  Confirm `examples/` needs no change (it authors neither kind today) or migrate
  it if that has changed; `collectors/README.md` names the evidence-collector noun in prose
  and is covered by 6.2.
- [x] 6.4 Run `npx markdownlint-cli2 "**/*.md"`.
- [x] 6.5 When the spec deltas are applied, re-term the one surviving occurrence of a
  retired kind that no delta can reach: the `## Purpose` paragraph of
  `openspec/specs/integration-connection/spec.md` opens "an `Integration` that an
  `EvidenceCollector` of `type: integration` references". Applying a delta rewrites
  requirement bodies only, so this sentence is never regenerated and would be left naming a
  kind that no longer exists. Change it to name the merged `Collector` of
  `type: integration`. A sweep of every other capability's Purpose finds no second
  occurrence, so this is the only one; re-run that sweep to confirm before editing.
- [x] 6.6 When the spec deltas are applied, DELETE
  `openspec/specs/evidence-collector-register/` and
  `openspec/specs/attestation-template-register/`. Both deltas are REMOVED-only and remove
  every requirement in their base specs, and applying the deltas does not delete the
  directory - it rewrites each file with an empty requirements section and keeps the
  preamble. A spec with no requirements fails validation ("Spec must have at least one
  requirement"), so leaving them in place leaves two permanently invalid specs in the
  source of truth that code review points at as authoritative. Everything they specified is
  carried by `collector-register`, and each removed requirement already states where.

## 7. Verification

Commit: folded into the last functional commit; no separate commit unless a fix is needed.

- [x] 7.1 `dotnet build` at the repo root with no warnings introduced.
- [x] 7.2 `dotnet test` with `FREEBOARD_TEST_DB` unset: every gated MySQL, SMTP, and E2E
  test skips cleanly and the rest pass.
- [x] 7.3 `docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d`,
  then `dotnet test` with `FREEBOARD_TEST_DB` set: the merged-schema, per-type config
  validation, prune-order, credential-cascade, and `021` migration tests pass, including the
  refusal tests (a checks-carrying integration row with a NULL connection fails the migration
  and leaves both legacy tables intact; a non-integration row carrying `checks` or a
  `connection_id`, and an integration row whose `checks` is absent, empty, a JSON null, a
  non-array, an array with a non-object item, or an array with an object item carrying a
  wrong-typed known member, and an `attestation_templates` row whose `fields` or `quiz` is a
  JSON null, a non-array, or an array with a non-object or wrong-typed item, each fail before
  `collectors` is created), the id-collision abort
  (a shared id across the two source tables fails on the unnamed primary key with both legacy
  tables intact), and the split test (a paired attestation collector and template
  on one control migrate to two rows and the form-less row still reads).
- [x] 7.4 Boot the web app against a migrated database and confirm `/settings/collectors`
  renders, `/settings/evidence-collectors` and `/settings/attestation-templates` 404,
  `GET /api/v1/freeboard/collectors` returns the merged shape with no quiz `answer`, and
  `GET /api/v1/freeboard/compliance/status` reports one `collectors` count.
- [x] 7.5 Run `freeboard gitops validate` against a hand-migrated fixture directory and
  confirm a retired-kind document, a provider mismatch, an unknown config key, a top-level
  `checks`/`body`/`fields`/`pass_mark`/`quiz`, and an integration collector with no `config`
  `checks` each fail with a naming diagnostic and a non-zero exit.
- [x] 7.6 Confirm the widened fingerprint behaves: with a collector's scheduler-state row
  forced to `error`, edit only its `config` (a check `source_key`) and confirm the next
  ensure revives the row; repeat with only `threshold` changed and confirm it does not.
- [x] 7.7 Confirm the architecture test pinning the one-way EE rule still passes and that
  neither `Freeboard.CLI` nor `Freeboard.Agent` gained a `Freeboard.Enterprise` reference.
- [x] 7.8 Run `openspec validate "object-model-v2-collector-merge" --strict`.
- [x] 7.9 After the spec deltas are applied (6.5, 6.6): run `openspec validate --specs` and
  confirm every spec passes, then re-run the 6.3 sweep across `openspec/specs/**` and
  confirm the only remaining hits are inside requirement bodies that the deltas themselves
  author. The two emptied capability directories must be gone and no Purpose may still name
  a retired kind.
