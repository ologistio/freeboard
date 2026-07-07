## Context

Freeboard's GitOps config is declarative YAML (Kubernetes-style `apiVersion` +
`kind`) loaded into a typed model in `Freeboard.Core`, validated to a diagnostic
list, and synced into per-kind MySQL tables by an importer in
`Freeboard.Persistence`. Existing kinds are `Standard`, `Requirement`, `Control`,
`Organisation`, `Scope`, `RequirementScope`, `Vendor`, `VendorScope`, and
`EvidenceCollector`. The pipeline is pure and testable: the loader and validator
never throw and never write output; the importer replaces the whole persisted set in
one transaction.

This change adds one kind, `AttestationTemplate`. It mirrors the just-merged
`add-evidence-collector-kind` change at every layer, so the shapes below match the
real current code, not a fresh design. Unlike the collector change, it modifies no
existing kind: a template only attaches to a `Control`, so `Control` is unchanged and
there is no `ALTER TABLE`.

Templates studied in the current tree:

- `EvidenceCollector`
  (`Freeboard.Core/GitOps/{ConfigModel,ConfigLoader,ConfigValidator}.cs`): a record
  that binds to a typed foreign key with reference resolution, closed-enum fields
  parsed against fixed token sets, a raw-text numeric field range-checked by the
  validator (`Threshold`), and a free-form map stored as JSON (`Config`).
  `AttestationTemplate` mirrors this machinery and extends it to nested lists.
- Persistence: `Migrations/012_evidence_collectors.sql` (DDL template; highest
  existing migration is `012`), `GitOps/ImportPlan.cs`,
  `GitOps/MySqlGitOpsImporter.cs`, `ComplianceReadModels.cs`, `IComplianceStore.cs`,
  `MySqlComplianceStore.cs`.
- Read surfaces: `Compliance/ComplianceEndpoints.cs`,
  `Pages/Compliance/EvidenceCollectors.cshtml(.cs)`, `Pages/Shared/_Layout.cshtml`
  (nav), `Freeboard.CLI/{CollectorCommands.cs, ApiCommandRunner.cs,
  IFreeboardApiClient.cs, HttpFreeboardApiClient.cs, GitOpsCommands.cs, Program.cs}`.

## Goals / Non-Goals

**Goals:**

- `AttestationTemplate` parses, validates, and syncs through the existing pipeline
  with no new machinery beyond one migration and the per-kind wiring.
- Referential integrity: `template -> control` resolves; a field `type` outside
  `boolean`/`single-choice`/`short-text` is rejected with a clear diagnostic; the
  training-vs-manual conditional rules hold; `pass_mark` is range-checked.
- `body`, `fields`, `pass_mark`, and `quiz` are persisted and exposed on both read
  surfaces (web SSR + CLI) for the later attestation runtime.
- A register on web SSR and CLI, in one PR, listing controls and their templates.

**Non-Goals:**

- Attestation runtime: form render, response capture, quiz grading, pass/fail
  computation. Config only.
- Coupling to `EvidenceCollector`. A template attaches to a `Control` only.
- App-managed template CRUD or a visual builder (GitOps-only authoring in V1).

## Decisions

### D1: AttestationTemplate is a static GitOps kind; the template -> control edge lives on the template

Match every other kind. `AttestationTemplate` is a YAML kind parsed in
`Freeboard.Core`, validated to diagnostics, synced by the importer; no app write path
in V1. A template names its `control`; the control does not list its templates. A
control MAY have several templates (for example a manual form and a training quiz on
the same control), so identity is keyed on `id` only, mirroring `EvidenceCollector`.
`Control` is not modified.

Files: `ConfigModel.cs` (add `KindAttestationTemplate`; add the `AttestationTemplate`
record with nested `AttestationField` and `QuizItem` records; add
`List<AttestationTemplate> AttestationTemplates` to `GitOpsConfig`), `ConfigLoader.cs`
(one `SchemaKeys` entry; the `apiVersion` `WithAttributeOverride` line; one `switch`
case that normalizes null nested collections; extend the unknown-kind message),
`ConfigValidator.cs` (add `ValidateAttestationTemplates` called after controls are
validated, so the control id set exists).

### D2: AttestationTemplate field schema

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: AttestationTemplate
id: attest-firewall-manual
title: Firewall change attestation
control: ctrl-firewall
type: manual                       # manual | training
body: |                            # optional markdown
  Confirm the boundary firewall ruleset was reviewed this period.
fields:                            # optional list of form fields
  - id: reviewed
    label: Ruleset reviewed?
    type: boolean                  # boolean | single-choice | short-text
  - id: outcome
    label: Review outcome
    type: single-choice
    options: [pass, pass-with-notes, fail]
  - id: notes
    label: Notes
    type: short-text
---
apiVersion: freeboard.dev/v1alpha1
kind: AttestationTemplate
id: attest-phishing-training
title: Phishing awareness
control: ctrl-training
type: training                     # requires pass_mark AND >= 1 quiz item
body: Read the guidance, then answer.
pass_mark: 80                      # integer percent 0..100
quiz:
  - id: q1
    prompt: What should you do with an unexpected attachment?
    options: [Open it, Report it, Forward it]
    answer: Report it
```

- `id`, `title`: identity + display, like every kind.
- `control`: required; references a `Control` id. This is the attach point.
- `type`: required; exactly `manual` or `training`. Case-sensitive parse, consistent
  with the existing token-set fields. An unknown value yields a clear diagnostic.
- `body`: optional markdown string, stored verbatim. Rendered downstream, not here.
- `fields`: optional ordered list. Each field has `id` (unique within the template),
  `label`, `type` (one of the three tokens), and `options` (a list of choice labels)
  used only by `single-choice`.
- `quiz`: optional ordered list. Each item has `id` (unique within the template),
  `prompt`, `options` (>= 2 answer labels, unique within the item), and `answer` (must
  equal one of `options`). `answer` is a value reference; option-label uniqueness (D3,
  Plan synthesis item 4) makes that value unambiguous. `answer` is persisted but is a
  quiz secret: it is redacted from every read surface (D8, Plan synthesis item 1).
- `pass_mark`: optional integer percent in `[0, 100]`. Carried in the Core model as
  raw authored text (`string PassMark = string.Empty`), NOT `int?`, exactly like
  `EvidenceCollector.Threshold`: typing it `int?` would make a malformed value (for
  example `pass_mark: high`) a YamlDotNet binding error instead of a clean validation
  diagnostic. The validator parses and range-checks the text; `ImportPlan` converts
  it to `int?` only after validation passes (blank stays null).

The `type` and field-`type` tokens are lowercase-hyphenated, matching the collector's
new-kind convention rather than the older PascalCase `In`/`Out` enums.

### D3: The three field types plus the single-choice options rule

`type` on a field is exactly one of `boolean`, `single-choice`, or `short-text` (the
task's fixed set). The validator rejects any other value with a diagnostic naming the
template and field, mirroring how an unknown collector `type` is rejected.

`options` is meaningful only for `single-choice`. The validator requires a
`single-choice` field to carry an `options` list of at least two labels, and rejects a
`boolean` or `short-text` field that declares a non-empty `options` list. The rule is
non-empty, not presence: the Core model defaults `Options` to `[]` and the loader
normalizes a present-but-null `options:` to `[]`, so an absent key, `options:`, and
`options: []` are indistinguishable and all allowed on a non-choice field; only a
non-empty list is rejected (see Plan synthesis item 5). A single-choice field with one
option is a disguised `boolean`, so its minimum is two, matching the quiz-item minimum
(D4). Labels within one field's `options` MUST be unique (see Plan synthesis item 4).
Rejecting a non-empty `options` list on a non-choice field (rather than silently
ignoring it) surfaces an authoring mistake early, matching the pipeline's
fail-with-a-clear-diagnostic ethos. Alternative considered: ignore a stray non-empty
`options` list on non-choice fields - rejected because a silently dropped list would
read as a working config that loses data.

### D4: Training vs manual conditional rules

The one net-new conditional cluster of this change, directly parallel to
VendorScope's `justification`-when-`Out` and the collector's
`evaluation`-when-collectors:

- `type: training` REQUIRES a `pass_mark` (present, integer `[0,100]`) AND a non-empty
  `quiz`. A training template with no quiz cannot be graded, and with no pass mark has
  no bar; both are validation failures naming the template.
- `type: manual` must NOT declare a `pass_mark` or a non-empty `quiz`. A manual
  attestation has no quiz to grade, so those fields are meaningless on it; declaring
  them is an authoring mistake and is rejected. Alternative considered: allow and
  ignore them on manual - rejected for the same reason as D3 (silent data loss reads
  as success).

`fields` is optional for both types (a training template may be quiz-only; a manual
template may be a body-only acknowledgement). No rule forces a minimum field count.

### D5: Referential integrity reuses the existing id-set machinery

`ValidateAttestationTemplates` runs after `ValidateControls`, receiving the control id
set. It checks: `control` resolves against control ids (required); `type` in the
closed set; each field's `type` in the closed set with the options rule (D3, including
unique option labels and the two-option minimum for single-choice); each quiz item's
shape (D4) including unique option labels and `answer in options`; `pass_mark` range
when present;
`id` unique within the kind; field ids unique within a template; quiz ids unique
within a template. No requirement id set is needed: the transitive
`template -> control -> requirement` path is already guaranteed by the existing
non-empty `maps_to` check on controls. No pair-uniqueness rule: a control may have
several templates, so the only uniqueness is on `id` (and the intra-template field/
quiz id uniqueness).

Validation of the `type` token uses a `HashSet.Contains` check, matching how the
collector `type`/`frequency` and control `evaluation` tokens are validated (not a
`TryParse` helper). The field-`type` set is a second `HashSet`.

### D6: Persistence - one migration adds one table, no ALTER

`Migrations/013_attestation_templates.sql` (forward-only; auto-discovered as an
embedded resource, no code registration; the highest existing migration is `012`):

`CREATE TABLE IF NOT EXISTS attestation_templates` (utf8mb4_bin ids, `DATETIME(6)`
timestamps, InnoDB, matching `012_evidence_collectors.sql`): `id` PK, `api_version`,
`title`, `control_id` NOT NULL FK -> `controls` RESTRICT, `type` VARCHAR(16), `body`
TEXT NULL, `fields` JSON NULL, `pass_mark` INT NULL, `quiz` JSON NULL, `created_at`,
`updated_at`, with a secondary index on `control_id`. No secondary unique key
(uniqueness is on `id` only), so the importer upserts by id and prunes absent rows
(the `Upsert*`+`DeleteAbsent` pattern the collector table uses, not the whole-set
`Replace*` the scope tables use). The `fields` and `quiz` lists use MySQL's native
JSON type (8.4 baseline), storing each as a JSON array; the store round-trips them
back into typed lists. This mirrors the collector `config` JSON precedent and avoids
normalized child tables that nothing queries in V1. Alternative considered: normalized
`attestation_fields` / `attestation_quiz_items` child tables (Plan B) - rejected as
more DDL, more importer code, and more FKs for data no query needs to filter or join
on in this change; see Plan synthesis item 3.

The full quiz item, including its `answer`, is serialized into the `quiz` JSON. The
answer is stored (the later grading runtime needs it) but never returned on a read
surface: the answer-stripping happens at the read-store projection boundary (D8), not
by omitting it from storage. Stripping at read is secure by construction (no surface
can leak what the store never hands it) and costs less code than actively removing the
answer before serialize.

Importer (`MySqlGitOpsImporter`):

- Add `UpsertAttestationTemplatesAsync` (insert/update by id) in the FK-safe upsert
  phase, after controls are upserted (its FK points at controls). Its `fields` and
  `quiz` values are serialized to JSON strings in `ImportPlan` via `System.Text.Json`
  (null when the list is empty).
- Prune absent `attestation_templates` in the delete phase BEFORE pruning absent
  `controls`: the `control_id` FK is RESTRICT, so a still-referenced control cannot be
  deleted while a stale template points at it. Placement is alongside the
  `evidence_collectors` prune (both must precede the `controls` delete; their order
  relative to each other does not matter).

### D7: The template content holds no credential material

The existing schema rule "Config carries no credential material" now also covers
`AttestationTemplate`. `body`, `fields`, and `quiz` are git-tracked form and quiz
content and must not inline credential material (a token, key, password, or
equivalent). This change adds no credential field; the rule is documented and asserted
by convention and test, not by content scanning.

The quiz `answer` is not credential material and is not a prohibited secret under this
rule: it is confidential authoring data that IS stored in git-tracked config and
persisted (the later grading runtime needs it), but is redacted from every broad read
surface (see D6 for storage and D8 for read redaction). So D7 read alone does not imply
the answer must be kept out of the config or the store.

### D8: Read surfaces mirror the compliance stack; parity via SSR page + CLI-over-API

- Read store: add `AttestationTemplateRow(Id, Title, Control, Type, string? Body,
  IReadOnlyList<AttestationField> Fields, int? PassMark, IReadOnlyList<QuizItemView>
  Quiz)` to `ComplianceReadModels.cs`. `Fields` reuses the Core `AttestationField`
  value record (a field carries no secret), but the quiz uses a register-specific
  `QuizItemView(string Id, string Prompt, IReadOnlyList<string> Options)` that has NO
  `Answer` property - the correct answer is a quiz secret and MUST NOT reach the read
  surface (Plan synthesis item 1). `GetAttestationTemplatesAsync` deserializes the
  `quiz` JSON into the Core `QuizItem` (which does carry `Answer`) and projects each
  into a `QuizItemView`, dropping the answer at that boundary; nothing downstream can
  re-expose it because the read model has no field to hold it. Deserialize `fields`
  with the same `System.Text.Json` shape `ImportPlan` wrote (internal serialization,
  symmetric on both ends). Add the method to `IComplianceStore`/`MySqlComplianceStore`.
  Extend `ComplianceCounts` with a 10th field `AttestationTemplates` and add the
  correlated `COUNT(*)` subquery to `ReadCountsAsync`.
- API (`ComplianceEndpoints`): add `GET /api/v1/freeboard/attestation-templates`
  (GET-only, `RequireAuthorization`, 503 on unreachable store) with payload keys `id`,
  `title`, `control`, `type`, `body`, `fields` (array of `{id,label,type,options}`),
  `pass_mark`, `quiz` (array of `{id,prompt,options}`). The quiz items carry NO
  `answer` key - the endpoint projects the answer-free `QuizItemView` the store
  returns, so the correct answer never appears in the JSON. The endpoint reuses the
  broad `RequireAuthorization` (any authenticated user) that every compliance read
  uses; redacting the answer at the read model keeps the surface broad without leaking
  the answer key (Plan synthesis item 1). Add `attestationTemplates` (camelCase,
  matching the other count keys) to the `/compliance/status` `persisted` object in BOTH
  the reachable (integer) and unreachable (null) branches.
- Not org-narrowed. Like vendors and collectors, templates are org-independent
  reference data (no `organisation` dimension), so the endpoint and page skip the
  `IOrgAccess` narrowing. A zero-grant test pins this deliberate non-narrowing.
- Web SSR (`attestation-template-register`): new page
  `Pages/Compliance/AttestationTemplates.cshtml(.cs)` at
  `/compliance/attestation-templates`, injecting `IComplianceStore` in-process like
  the collector page. It groups templates by control id and renders each control that
  has templates with its templates beneath (type, body, fields, and for training the
  pass mark and quiz prompts/options). The `body` is authored markdown but is rendered
  as Razor-encoded text (`@template.Body`), NOT `@Html.Raw` and NOT a markdown-to-HTML
  step: raw HTML from a git-authored body would be a stored-XSS vector, and there is no
  sanitizer-backed renderer in the tree. V1 shows the encoded markdown source; a safe
  renderer is a later change (Plan synthesis item 2). The quiz renders each item's
  prompt and options but NOT the answer (the read model carries none). Controls with no
  templates are omitted - unlike the collector
  page, nothing about the control itself changes, so listing template-free controls
  would add noise. GET-only, any authenticated user, unaffected by read-only mode,
  in-page notice on an unreachable store. Add a nav link in `_Layout.cshtml`. Add the
  route to the parametrized `Pages` cases in
  `Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`.
- CLI (`attestation-template-register`): new `AttestationTemplateCommands` group
  (`attestation-template list`), modelled on `CollectorCommands`, calling the shared
  `ApiCommandRunner.Run`/`Translate`. It reads `/attestation-templates`, groups by
  control id, and prints each control with its templates (type, body indicator,
  fields, and for training pass mark and quiz prompts/options). It prints no quiz
  answer - the API returns none. It does NOT read `/controls`: a template already
  carries its control id and, unlike the collector command, no control-level property
  (there was `evaluation`) needs fetching, so the extra call is omitted. Add
  `ListAttestationTemplatesAsync` to `IFreeboardApiClient` (with the
  `ApiAttestationTemplate`/`ApiAttestationField`/`ApiQuizItem` wire records, where
  `ApiQuizItem` has no `Answer`) and
  implement it in `HttpFreeboardApiClient`. Register the `attestation-template` group
  in `Program.cs`. Exit codes follow the CLI convention (0 ok, 1 validation, 3
  operational). The CLI reads over HTTP, never the database.
- Sync summary parity: add the attestation-template count to `GitOpsCommands`
  `PrintSummary`, `PrintPlannedState`, and the `Sync` success line. This authoring
  output reads the raw `GitOpsConfig`, not the redacted read store. `PrintSummary` and
  the `Sync` success line stay count-only for attestation-templates, matching every
  other kind. `PrintPlannedState` prints a per-template line under the count header,
  mirroring `EvidenceCollector` (which prints a per-item line while omitting its opaque
  `config` map): each template line shows `id`, `title`, `control`, `type`, and (for a
  training template) the `pass_mark`, while OMITTING `body`, `fields`, `quiz`, and the
  quiz `answer`. None of the three outputs print `fields`, quiz prompts/options, or the
  quiz `answer`; the confidential quiz answer is never printed by gitops authoring
  output.

### D9: CLI group name is `attestation-template`, not `attestation`

The collector change shortened its group to `collector` (Q4). This change keeps the
full `attestation-template` name instead, diverging deliberately: the Non-goals
exclude a runtime attestation, and a future runtime feature will plausibly want an
`attestation` command for actual attestations (instances, responses). Naming this
config-register group `attestation` would squat that name and mislead
(`attestation list` reads as "list attestations", not "list attestation templates").
The longer name is unambiguous and future-proof; brevity loses here.

## Risks / Trade-offs

- [Nested `fields`/`quiz` bind to typed records via YamlDotNet] -> Mitigation: a
  malformed nested value (for example a scalar where a list is expected) surfaces as a
  loader diagnostic, never an uncaught exception, the same "not supported" signal the
  collector `config` bind boundary gives. A loader-diagnostic test pins this.
- [Explicit-null nested collections NRE downstream] -> Mitigation: the loader `switch`
  case normalizes `template.Fields ?? []`, `template.Quiz ?? []`, and each field's and
  quiz item's `Options ?? []`, mirroring the collector `Config ?? []` normalization,
  so ImportPlan serialization, page render, and CLI grouping never see a null list.
  An explicit-null test pins this.
- [`fields`/`quiz` stored as opaque JSON] -> Mitigation: nothing queries individual
  fields in V1, so a JSON column is the smaller model; the JSON type validates
  well-formedness at write and the store round-trips it symmetrically.
- [Training/manual conditional couples the two type branches] -> Mitigation: the rule
  runs in the same validator method where both `type` and the quiz/pass_mark values
  are in hand; it is a few branches, not new machinery.
- [Read-surface size: store, API, page, CLI is a lot of code] -> Mitigation: each
  layer is a thin mirror of the collector register; the parity rule is an explicit
  acceptance criterion; no new abstraction is introduced.
- [10th `ComplianceCounts` field breaks every constructor call site] -> Mitigation:
  the call sites are enumerated in tasks (FakeComplianceStore, OrgSelectionTests,
  MySqlIntegrationTests) and updated in the same change.

## Migration Plan

- Add `013_attestation_templates.sql`. Additive and forward-only: create
  `attestation_templates`; alter no existing table. Rollback is dropping the table (no
  data migration).
- Applied by the existing operator path (`freeboard system migrate` or
  `gitops sync --migrate`). The web app does not migrate at startup.
- Deploy order: apply the migration, then `gitops sync` a config that includes the new
  kind. Old configs without templates continue to sync unchanged (empty template set).

## Plan synthesis

This change was drafted from two independent plans: Plan A (the OpenSpec artifacts
authored here, mirroring the merged `add-evidence-collector-kind` change) and Plan B
(an independent Codex plan). They agree on the pipeline path and the read-surface
parity. They diverge on five points, resolved as follows.

1. Quiz-answer exposure (Plan B, High). The compliance read endpoints use only
   `.RequireAuthorization()` (verified in `ComplianceEndpoints.cs`: `MapGroup(...)
   .RequireAuthorization()`), so every authenticated user - including a zero-grant
   member under strict enforcement - reads the register. Plan A originally emitted the
   quiz `answer` on all three read surfaces, which would ship every training answer key
   to every user. Resolution: keep the broad read surface (Plan A) and redact the
   answer (Plan B's requirement). Redaction is structural, at the read-store boundary:
   `GetAttestationTemplatesAsync` returns a `QuizItemView` with no `Answer` property, so
   the API JSON, the SSR page, and the CLI cannot re-expose it. The answer is still
   persisted in the `quiz` JSON for the later grading runtime (D6). Chosen over gating
   the whole surface behind an admin role: gating would break the reference-data parity
   with vendors/collectors and hide the (non-secret) prompts, body, and fields for no
   reason. Tasks add a test on each surface asserting a sentinel answer value never
   appears in the default output.

2. Markdown body rendering (Plan B, High). Plan A said `body` is "rendered downstream"
   but its SSR page still renders it. Resolution: V1 renders `body` as Razor-encoded
   text (`@template.Body`), never `@Html.Raw` and never a markdown-to-HTML step. There
   is no sanitizer-backed markdown renderer in the tree (verified: no `Markdig`/
   `Html.Raw`/`MarkupString` usage), so raw rendering would be a stored-XSS vector from
   git-authored content. A page test asserts a `body` containing `<script>` renders as
   encoded text (`&lt;script&gt;`), not live HTML. The CLI prints body to a terminal
   (no HTML context), so the XSS concern is web-only; the CLI shows a body indicator.

3. Persistence shape - JSON vs relational (Plan A JSON vs Plan B relational child
   tables). Resolution: JSON (Plan A). Core parse/validate already enforces referential
   and ordering integrity before anything is persisted, so storage only round-trips an
   already-validated structure; nothing in V1 queries individual fields or quiz items;
   and the merged `EvidenceCollector` kind set the precedent of storing type-specific
   config as JSON. Relational child tables (Plan B) would add two tables, two FKs, and
   more importer/read code for data no query filters or joins on in this change.
   Tradeoff: if a later change needs to query nested rows (for example "every template
   whose quiz has > 10 items"), it will need a schema migration. Accepted; no such V1
   consumer exists.

4. Schema naming and answer representation (Plan A `quiz`/`prompt`/`answer`/`pass_mark`
   vs Plan B `quiz_items`/`question`/`correct_option`/`pass_mark` with options as
   `{id,label}` objects). Resolution: Plan A's names. `options` stays a plain list of
   string labels (consistent with the field `options` list and with every other kind's
   plain-scalar style; no kind in the repo models options as objects), and `answer` is
   a value reference into that list. Plan B's option-id form is unambiguous but forces
   options to become objects everywhere and adds a second identifier per option.
   Instead, the validator requires option labels to be unique within each field and
   each quiz item, which makes the value-based `answer` unambiguous without the
   object-structure cost. Least-ambiguity requirement (from Plan B) is met by the
   uniqueness rule, not by changing the representation.

5. single-choice minimum options (Plan A >= 1 vs Plan B >= 2). Resolution: >= 2. A
   single-choice field with one option is not a choice; a mandatory one-value
   acknowledgement is a `boolean`, so a one-option single-choice is a disguised boolean
   and is rejected. This matches the quiz-item minimum (>= 2) and removes a degenerate
   config shape.

Plan B's low-severity point (require `title`) needs no change: Plan A already requires
`id` and `title` on every resource, so the invariant holds.

### Tensions for reviewers

- The answer redaction is enforced by the read model shape, not by an authz check. A
  future grading runtime that reads answers must use a separate, privileged query path
  and must not widen `AttestationTemplateRow`/`QuizItemView` to carry the answer.
- V1 shows encoded markdown source, which is functional but not pretty. If a reviewer
  wants rendered markdown sooner, that is a separate change adding a sanitizer-backed
  renderer, not a loosening of this one.
