# Design

## Context

Freeboard's GitOps layer declares eight kinds. Two of them attach a proving mechanism to
a `Control`:

- `EvidenceCollector` (`src/Freeboard.Core/GitOps/ConfigModel.cs`): `control`, `vendor?`,
  `type` in `{integration, script, manual-attestation, training-attestation, agent}`,
  `frequency`, `threshold?`, `config` (a free-form `Dictionary<string,string>`),
  `connection?`, `checks[]`. Persisted in `evidence_collectors` (migration `012`, extended
  by `015` and `018`, re-pointed at `assets` by `019`).
- `AttestationTemplate`: `control`, `type` in `{manual, training}`, `body?`, `fields[]`,
  `pass_mark?`, `quiz[]`. Persisted in `attestation_templates` (migration `013`).

The overlap is already encoded in the type token set: `manual-attestation` and
`training-attestation` name exactly what an `AttestationTemplate` is. The RFC's Collector
section (the agreed v2 baseline, superseded on the config question by the typed-config
requirement) merges them into one kind whose attestations are `type: manual` /
`type: training` with the form carried in `config`.

Current downstream consumers of the two shapes, all of which move in this change:

| Layer | File | What it does |
| --- | --- | --- |
| Core | `GitOps/ConfigModel.cs` | `EvidenceCollector`, `AttestationTemplate`, `AttestationField`, `QuizItem`, `Check`, `GitOpsSchema` kind constants, `GitOpsConfig` lists |
| Core | `GitOps/ConfigLoader.cs` | per-kind allowed-key sets, kind routing, null-collection normalization |
| Core | `GitOps/ConfigValidator.cs` | `ValidateEvidenceCollectors`, `ValidateCollectorChecks`, `ValidateAttestationTemplates`, `ValidateAttestationFields`, `ValidateAttestationQuiz`, `CheckDuplicateOptions` |
| Core | `GitOps/EvidenceCollectorFrequency.cs` | cadence tokens, `Interval`, `IsStale` |
| Persistence | `GitOps/ImportPlan.cs` | `EvidenceCollectorRowPlan`, `AttestationTemplateRowPlan`, id lists |
| Persistence | `GitOps/MySqlGitOpsImporter.cs` | two upserts, two prunes, FK-safe order |
| Persistence | `ComplianceReadModels.cs` | `EvidenceCollectorRow`, `AttestationTemplateRow`, `QuizItemView`, `SoaDrilldownInputs`, `ComplianceCounts` |
| Persistence | `IComplianceStore.cs`, `MySqlComplianceStore.cs` | two reads, drill-down read, counts, `DeserializeQuiz` (the redaction boundary) |
| Persistence | `MySqlEvidenceStore.cs` | calls the cadence helper's `IsStale`; moves with the rename only |
| Web | `Pages/Compliance/EvidenceCollectors.cshtml(.cs)`, `AttestationTemplates.cshtml(.cs)` | two register pages |
| Web | `Compliance/ComplianceEndpoints.cs` | two list endpoints plus the counts endpoint |
| Web | `Compliance/StatementOfApplicability.cs` | `SoaCheckKind`, `ResolveDrilldown(collectors, templates, vendors)` |
| Web | `Pages/Compliance/ControlDetailProjection.cs` | the drawer/full-page "Proving checks" section |
| Web | `Navigation/ShellNavCatalog.cs` | two nav entries |
| Web | `Scheduler/CollectorSchedulerService.cs`, `IScheduledCollectorRunner.cs` | integration-only claim filter, fingerprint, runner seam |
| Web | `Evidence/EvidenceIngestEndpoints.cs`, `CollectorCredentialEndpoints.cs` | collector lookup, credential routes |
| Web | `Program.cs` | startup unresolvable-token warning scan |
| CLI | `CollectorCommands.cs`, `AttestationTemplateCommands.cs`, `Program.cs` | two command groups |
| CLI | `IFreeboardApiClient.cs`, `HttpFreeboardApiClient.cs` | `ApiEvidenceCollector`, `ApiAttestationTemplate`, `ApiAttestationField`, `ApiQuizItem`, two paths |
| Docs | `docs/gitops.md` | kind list, noun table, two authoring sections |

There are zero on-disk YAML fixtures of either kind; the authored documents live in
`docs/gitops.md` (3 documents) and inline in tests (40 `EvidenceCollector`,
41 `AttestationTemplate` documents across seven test files).

Constraints: pre-production hard cutover (no data contract, forward-only migration, no
compat layer); `apiVersion` stays `freeboard.dev/v1alpha1`; the loader and validator never
throw and never print; ids are exact-byte (`utf8mb4_bin`); the training quiz answer is
persisted and redacted at the store boundary.

## Plan synthesis

This design is the merge of two independently written plans for issue #128 - Plan A (the
first pass at this change directory) and Plan B (written without sight of Plan A). Both
reached the same architecture unprompted, which is the strongest signal in the record:

**Agreed by both, no divergence.** One `Collector` kind replacing both legacy kinds; one
`Collectors` collection on `GitOpsConfig`; one `collectors` table with both legacy tables
dropped; the typed-config mechanism lives in `Freeboard.Core` (shared by the CLI and web
import paths) with no new package dependency and no JSON Schema library; the registry is
keyed on `(type, provider)`; `provider` is authored top-level, required only for
`type: integration`, and cross-checked against the connection's provider; `connection`
stays top-level because it is a relational reference, not payload; training answers are
redacted at the persistence read boundary so every surface inherits it, not in the page or
CLI; hard cutover with no aliases, no legacy routes, no legacy tables; and
`Dictionary<string,string>` is unfit for the merged config. Plan B independently derived
the migration hazard (re-point `collector_credentials` before dropping
`evidence_collectors`) that Plan A had also caught.

**Divergences, and how each was resolved.** Five real disagreements are recorded below at
the decision they belong to: config payload placement (D1a, resolved to Plan B), the config
representation mechanism (D1b, resolved to Plan A), `frequency` on non-scheduled types
(D11, resolved to Plan A on a rationale neither plan had), the scheduler fingerprint scope
(D9, resolved to Plan B), and explicit rejection of legacy top-level fields (D12, resolved
to Plan B). Two of the five went to Plan B, two to Plan A, and one - `frequency` - kept Plan
A's position while correcting the reasoning Plan A had given for it.

**Net effect on the unified plan.** Plan B moved integration `checks` into `config`, which
made the provider axis load-bearing in V1 instead of speculative, and that in turn forced
two consequential changes Plan A did not have: `checks` becomes visible on the read
surfaces, and the scheduler fingerprint has to widen or an operator's config fix can no
longer revive a dead collector. Plan A kept the config representation small and kept
`frequency` required, and its liability argument survived Plan B's challenge on the first
and its rationale was replaced on the second.

## Goals / Non-Goals

**Goals:**

- One `Collector` kind, one `collectors` table, one read model, one read endpoint, one
  register page, one CLI command.
- `Collector.config` validated against a schema registered for its `(type, provider)`
  pair, with unknown keys rejected: no uniformly free-form config anywhere in the format.
- Parity of the `manual`/`training` value rules with today's `AttestationTemplate`
  validation, and of the training-answer redaction on every read surface. TWO deliberate
  differences are accepted and recorded in the risks: a key the pair's schema does not
  register is now rejected even when its authored value is empty or blank, where the
  pre-merge value test treated an empty one as absent; and a `Control` whose only attached
  proving mechanism is an attestation now needs an `evaluation` rule, where a control
  carrying only an `AttestationTemplate` did not.
- Web and CLI read surfaces land in the same change as the model and schema.
- MIT throughout; nothing enters `Freeboard.Enterprise`, and neither `Freeboard.CLI` nor
  `Freeboard.Agent` gains a reference to it.

**Non-Goals:**

- Any runner: integration collection, attestation grading, script or agent execution.
- Any `(integration, fleet)` config key beyond `checks` (a `team` key, say), and any config
  key at all for `script` or `agent`.
- Nested unknown-key rejection INSIDE a `fields`, `quiz`, or `checks` item.
- An app-managed write path for collectors.

## Decisions

### D1: One `Collector` record; every type-specific payload lives in `config`

`Collector` in `Freeboard.Core/GitOps/ConfigModel.cs`:

```yaml
apiVersion: freeboard.dev/v1alpha1
kind: Collector
id: <stable id>
title: <display>
control: <control id>            # required attach point
vendor: <asset id>               # optional; must be a Vendor-type Asset
type: integration | script | agent | manual | training
provider: fleet                  # required iff type: integration, else absent
frequency: continuous | daily | weekly | monthly | quarterly | annual
threshold: 0..100                # optional
connection: <integration id>     # required iff type: integration, else absent
config:                          # keys closed by the (type, provider) schema
  checks:                        # integration/fleet: required, non-empty
    - source_key: "42"
      name: mfa-enforced
      severity: Hard
  body: |                        # manual, training: optional
    markdown
  fields: [...]                  # manual, training: optional
  pass_mark: 90                  # training: required
  quiz: [...]                    # training: required
```

The organising rule for the merged kind: **a top-level field is identity, the attach point,
a cross-document reference, or the collection cadence; everything type-specific is payload
and lives in `config`.** So `id`, `title`, `control`, `vendor`, `type`, `provider`,
`frequency`, `threshold`, and `connection` stay top-level, and `body`, `fields`,
`pass_mark`, `quiz`, and `checks` all move under `config`.

#### D1a: Integration `checks` moves into `config` (divergence - resolved to Plan B)

Plan A kept `checks` top-level and registered the EMPTY schema for `(integration, fleet)`.
Plan B moved `checks` into `config` and gave `(integration, fleet)` a schema requiring a
non-empty `checks` list, arguing that leaving it top-level "preserves a second schema path
and weakens the typed-config cutover". **Resolved to Plan B: `checks` moves into `config`.**

Plan B's own framing does not carry the decision, and the record should say so. Top-level
`checks` is not the free-form escape the issue exists to close: it is already a closed,
typed list with per-item validation (`source_key`/`name`/`severity` required, severity a
closed token set, name and source-key uniqueness). Moving it relocates an already-closed
field; it shuts no hole. Every top-level field has its own validation rules - that is the
norm for a typed schema, not a defect.

What does carry the decision is the provider axis. The issue's whole point is a schema
registered per `(type, provider)`. With `checks` top-level, the V1 registry is
`(manual, -)`, `(training, -)`, and three empty entries - so the *provider* component of
the key is exercised by nothing at all, and the two entries that do carry keys belong to
types that have no provider. The mechanism the issue asks for would ship as scaffolding
validated only by the attestation half. `checks` is the single most provider-shaped field
in the model: a `source_key` is defined as "the provider-native id, for example a Fleet
policy id". It is exactly the data a second provider (`intune`, say) would need to shape
differently. Registering it under `(integration, fleet)` makes the provider axis
load-bearing on day one and gives #52 a non-empty schema to extend rather than a first key
to invent.

The consistency argument seconds it: `body`/`fields`/`pass_mark`/`quiz` move into `config`
because they are payload that applies to two of five types. `checks` is payload that
applies to one of five types. Applying the rule to the attestation half and not the
integration half would leave the merge half-done and keep the "top-level field that only
some types may use" shape the merge exists to remove.

`connection` does NOT move, and this is Plan B's carve-out, kept: it is a cross-document
reference with a real `connection_id` column, a `RESTRICT` foreign key, and a place in the
importer's prune ordering. It is relational, not payload.

**Costs accepted, both new to this design:** `checks` becomes visible on the read surfaces
(D5), reversing Plan A's non-goal; and the scheduler fingerprint has to widen (D9), because
correcting a mistyped `source_key` is now a `config` edit.

#### D1b: `config` binds to a typed record, not a preserved YAML node (divergence - resolved to Plan A)

Both plans reject `Dictionary<string,string>`. They differ on the replacement. Plan A binds
`config` to a fixed typed record and keeps a static key-name registry. Plan B preserves the
`YamlMappingNode` through the loader into the model and dispatches to an
`ICollectorConfigSchema` implementation per `(type, provider)`, each with its own
`Validate(collector, YamlMappingNode, context)` and `ToStorageJson(...)`.
**Resolved to Plan A**, with D1a's `Checks` added to the record:

```csharp
public sealed record CollectorConfig
{
    public string Body { get; init; } = string.Empty;
    public List<AttestationField> Fields { get; init; } = [];
    public string PassMark { get; init; } = string.Empty;  // raw authored text
    public List<QuizItem> Quiz { get; init; } = [];
    public List<Check> Checks { get; init; } = [];
}
```

Plan B's two stated blockers were that the representation "cannot support typed nested
config" and "cannot reject nested unknown keys reliably". Both are true of
`Dictionary<string,string>` and neither is true of this record: it holds the nested manual,
training, and integration shapes directly, and nested unknown-key rejection inside a
`fields`/`quiz`/`checks` ITEM is an explicit non-goal carried over verbatim from the
pre-merge `AttestationTemplate` carve-out. So the blockers do not distinguish the two
proposals, and the choice falls to `code-as-liability.md`.

On that test Plan A wins clearly. Its mechanism is one record (five members, every one an
existing shape) plus one static table. Plan B's is an interface, a key struct, a validation
result type, a context type, a registry, and five schema classes - roughly six new types
before a single rule is written, with a hand-rolled `ToStorageJson` per schema where one
serializer call over one storage projection covers the whole record (D1c states that
projection; an earlier draft of this paragraph claimed a bare
`JsonSerializer.Serialize(config)` covered it, which is wrong and is retracted - the record
is the AUTHORED shape and the stored shape differs from it in two stated ways). There is
also a placement cost Plan B
did not price: `YamlMappingNode` on `GitOpsConfig` puts YamlDotNet's representation model
into the domain model that `Freeboard.Persistence`, `Freeboard`, and `Freeboard.CLI` all
consume. Today only `Freeboard.Core` references YamlDotNet, and `ImportPlan` would have to
walk a YAML node to write a JSON column.

Plan B's mechanism is the right one when the config vocabulary is large, open, or genuinely
per-provider divergent. V1's is five keys with five stable shapes. The registry (D2) keeps
the *key set* declarative and open to extension; the *value shapes* stay typed C# because
they are few. Adding a sixth key later is a member plus a registry row.

`CollectorConfig` is deliberately a union across types: `Checks` is meaningless on a
`manual` collector and `PassMark` on an `integration` one. The record's job is only to be
able to HOLD whatever any pair may author; the registry, not the record, is the authority
on what each pair may legally author. `AttestationField`, `QuizItem`, and `Check` are kept
verbatim from today's model - only their owner changes. `PassMark` stays raw authored text
and is parsed in `ImportPlan`, matching how `Threshold` and the old
`AttestationTemplate.PassMark` already behave, so a malformed value is a clean diagnostic
rather than a YAML binding error.

**Alternative rejected (both plans agree):** keeping `config` as `Dictionary<string,string>`
with `body`/`fields`/`pass_mark`/`quiz` back as top-level `Collector` fields. That keeps
the free-form escape hatch the change exists to close and re-creates the
"fields that only apply to some types" shape at the top level.

#### D1c: The stored `config` JSON is its own contract, stated in full

The stored object is a PROJECTION of `CollectorConfig`, not the record itself, because two
things change on the way in: an absent member is omitted, and `PassMark` is stored as the
parsed integer rather than as the raw authored text `CollectorConfig` holds. That is why
the storage shape is stated here as a contract of its own rather than left to be inferred
from the record.

**A note on spelling, because the specs mix two vocabularies deliberately.** A `config`
key is named by its AUTHORED, snake_case spelling everywhere the subject is YAML authoring
or the HTTP wire (`body`, `fields`, `pass_mark`, `quiz`, `checks`), and by its STORED
spelling - the C# member name - everywhere the subject is the database column (`Body`,
`Fields`, `PassMark`, `Quiz`, `Checks`). They name the same key; only the surface differs.

**The stored contract.**

- **Keys are the C# member names - PascalCase - and there is no naming policy.** That is
  not a new convention; it is exactly what is persisted today. `ImportPlan` calls
  `JsonSerializer.Serialize` with default options, so the current
  `evidence_collectors.checks` column stores
  `[{"SourceKey":"12","Name":"...","Severity":"Hard"}]` and the current
  `attestation_templates.quiz` column stores items keyed `Id`/`Prompt`/`Options`/`Answer` -
  `ImportPlanTests` asserts both.
- **`PassMark` is stored as a JSON NUMBER**, not as the raw authored string. `Body` is a
  JSON string; `Fields`, `Quiz`, and `Checks` are JSON arrays of the existing item shapes,
  each item keeping the key names it already has on disk.
- **An absent member is OMITTED**, never written as JSON null and never as an empty array.
  An empty authored list counts as absent, and so does a blank scalar: `Body` is the only
  scalar member that can be blank (`PassMark` is already `int?`), and a blank or
  whitespace-only body is absent, exactly as today's `NullIfBlank(t.Body)` treats it.
- **A config with no member present is stored as SQL NULL**, not as `{}`. This is what the
  importer already does (`ImportPlanTests` pins it), and migration `021` must match it on
  both copy paths.
- **Each stored quiz item RETAINS its `Answer`.** Redaction happens on the way out, at the
  store's read boundary (D5), never in the column.

So the merged `config` object stores as:

```json
{ "Body": "...", "Fields": [ { "Id": "f1", "Label": "...", "Type": "boolean", "Options": [] } ],
  "PassMark": 90,
  "Quiz": [ { "Id": "q1", "Prompt": "...", "Options": ["a","b"], "Answer": "a" } ],
  "Checks": [ { "SourceKey": "42", "Name": "mfa-enforced", "Severity": "Hard" } ] }
```

Only the five OUTER keys are new. Every nested item keeps the key names it already has on
disk, which is what lets migration `021` carry the old `checks`, `fields`, and `quiz`
column values across as opaque JSON values rather than rewriting them item by item.

**Why `PassMark` is a number and not the authored text.** Three reasons, all of them
existing behaviour rather than a new choice. `attestation_templates.pass_mark` is
`INT NULL` (migration `013`), so a number is what the column migration `021` reads already
holds, and `JSON_OBJECT('PassMark', at.pass_mark)` carries it with no cast. `ImportPlan`
already parses the authored `PassMark` text to `int?` before storage, through the same
helper `Threshold` uses, precisely so a malformed value is a Core diagnostic rather than a
storage-layer failure - storing the raw text would push that parse to the READ boundary,
where a bad value becomes a `JsonException` from the store. And `CollectorConfigView.PassMark`
is already `int?` (D5), so the read deserializes into the type the read model wants with no
converter. Storing the string would make all three disagree.

**Who has to agree on these member names, and how they stay in step.** Five parties, and
the criterion is agreement on the member NAMES, not on the column:

- Three compose or read the COLUMN and must agree on the stored key literals: `ImportPlan`
  writes it, the store's `DeserializeConfig` reads it, and migration `021` composes the
  identical shape in SQL.
- Two consume the read model derived from it and must agree on the member SET: the endpoint
  projection maps each member to its snake_case wire key (D5), and the schedule fingerprint
  serializes the whole view (D9).

The writer and the reader share ONE internal storage record in `Freeboard.Persistence` -
five nullable members (`Body`, `Fields`, `PassMark` as `int?`, `Quiz` carrying `Answer`,
`Checks`). The record declares ONE shared `JsonSerializerOptions` - default naming, no
policy, `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` - and both the writer
and the reader use that same instance, so "the writer and the reader use the same options"
is literally one object rather than two call sites that have to be kept alike. They are NOT
the serializer defaults, and the plan says which they are rather than saying "default". The
writer maps an empty list and a blank string to null before serializing, exactly as today's
`SerializeList` returns null for an empty list and `NullIfBlank` returns null for a blank
body; the ignore condition then drops them. That one type is worth its liability: it turns
"every writer and reader agrees on one shape" from prose into something the compiler checks,
and it replaces per-member hand-built JSON on the write side and a second hand-written
mapping on the read side. Migration `021` is the only composer that cannot share it, so its
`JSON_OBJECT` key literals are the contract in SQL form and a future rename of a member is a
migration, not just a C# rename.

The fingerprint is on that list but plays by different rules, and the difference is worth
naming rather than pretending it is not a party at all: it emits every member rather than
omitting absent ones, it applies the read model's redaction, and its output is read by
nothing, so a member rename cannot desynchronise it against a counterparty - it only
re-fingerprints once and revives dead/error rows once. D9 states its serialization in full.

The wire keys stay snake_case (`pass_mark`, `source_key`) and are produced by the endpoint's
hand-written projection, exactly as the two legacy endpoints produce them today. Storage
shape and wire shape are deliberately different: the wire shape is a public contract this
change is already breaking once, and the storage shape is private.

**Alternative rejected: store snake_case so storage and wire match.** It reads better, but
it buys nothing a consumer can observe and it forces migration `021` to rewrite the keys of
every carried `checks`, `fields`, and `quiz` ITEM (a `JSON_TABLE` re-projection per array)
instead of moving the column value verbatim. That is real migration complexity and real
migration risk for a cosmetic gain in a column nothing but the store reads.

### D2: `(type, provider)` config schema registry in Core

New file `src/Freeboard.Core/GitOps/CollectorConfigSchema.cs`, a pure static table:

```csharp
public sealed record CollectorConfigKey(string Name, bool Required);

public static class CollectorConfigSchema
{
    // null means "no schema registered for this (type, provider)"; the caller has
    // already reported the bad type or provider and must not also report every key.
    public static IReadOnlyList<CollectorConfigKey>? For(string type, string provider);
}
```

V1 table:

| type | provider | allowed keys |
| --- | --- | --- |
| `manual` | (none) | `body` optional, `fields` optional |
| `training` | (none) | `body` optional, `fields` optional, `pass_mark` required, `quiz` required |
| `integration` | `fleet` | `checks` required (non-empty) |
| `script` | (none) | (empty) |
| `agent` | (none) | (empty) |

**The two attestation rows are the pre-merge rules expressed as a key set, not a
tightening.** Today `AttestationTemplate.body`, `fields`, `quiz`, and `pass_mark` are ALL
optional on the record; `ValidateAttestationFields` runs for every template regardless of
type; and only `pass_mark` and `quiz` are type-conditional (required on `training`,
rejected on `manual`). So `fields` is registered as OPTIONAL on `manual` and is registered
on `training` too. An earlier draft of this table made `fields` required on `manual` and
omitted it from `training`; both were wrong and would have broken the acceptance
criterion, which is `manual`/`training` parity - a manual template with every
optional omitted validates today and must keep validating, and a training template that
also collects form fields is accepted today and must stay accepted.

Two semantics of the `Required` flag are fixed here so parity holds on the other side too,
and they are deliberately evaluated against DIFFERENT things.

**Requiredness is evaluated on the parsed config member, not on the YAML key.** A required
key is satisfied only by a member that is non-empty: a non-empty list where the value is a
list, and a non-blank scalar where it is a scalar. An earlier draft tested presence alone
for a scalar, which would have changed a verdict: today `ValidateAttestationTemplates`
computes `hasPassMark` as `!string.IsNullOrWhiteSpace(template.PassMark)`, so
`pass_mark: ""` and a null-valued `pass_mark:` on a `training` template are both rejected as
a missing required field. Under a presence-only test the key is there, the required check
passes, and the range check is skipped as well (it is guarded by the same blank test), so a
`training` collector with no pass mark would validate and persist. Evaluating the parsed
member keeps `training` failing on `quiz: []`, on `pass_mark: ""`, and on a null-valued
`pass_mark`, and keeps `(integration, fleet)` failing on `checks: []` - exactly as the
pre-merge value rules do.

**Unregistered-key rejection is evaluated on the YAML mapping, not on the parsed member**,
because a key with an empty value binds to the same empty member as an absent one and the
parsed record cannot tell them apart. An UNregistered key is therefore rejected whatever its
value, so `quiz: []` on a `manual` collector is now an unknown-key error where the pre-merge
value test ignored it. That is the first of the three accepted parity differences; all three
are in the risks with their rationale. Within the config key rules nothing that carried meaning
changes verdict: the values whose verdict does change are exactly the ones that could never
have had an effect.

Per D1a, `(integration, fleet)` carries `checks` rather than the empty schema Plan A had,
so the provider component of the key is exercised in V1 by the field whose values are
provider-native by definition. No FURTHER fleet key is registered: a fleet collector's
remaining inputs are carried by its `connection` (base URL plus the out-of-band token), and
inventing a `team` key the FleetDM collector (#52) has not asked for would be speculative
scaffolding. That stays a one-line registry edit when #52 needs it.

Registering `script` and `agent` with the EMPTY schema is a deliberate decision, not an
omission: no script or agent runner exists to consume a key. An empty schema still enforces
the acceptance criterion - any `config` key on those collectors is rejected.

The registry is the single owner of the key set, and "single" is meant literally: no rule
that the registry expresses is ALSO hard-coded somewhere else. An earlier draft of the
`gitops-config-format` delta carried a dedicated validator clause and scenario requiring a
non-empty `config` `checks` list on every `type: integration` collector, duplicating the
`(integration, fleet)` registry row. Both are removed. Requiredness is value-based (an
empty list counts as absent), so the registry row alone already rejects a missing `checks`,
an empty `checks`, and a `checks` key on any pair that does not register it. Worse, the
dedicated clause was unconditional on `provider`, so a second integration provider whose
schema registered a different key set would have been contradicted by a hard-coded rule -
defeating the whole point of keying on `(type, provider)`. Where a requirement needs to
mention the fleet check list, it does so by naming the registered schema rather than by
restating the rule.

The registry must also be COMPLETE over the pairs the token sets can produce, and that is
pinned by a Core unit test rather than by a runtime diagnostic. If `IntegrationProvider.Tokens`
gains a provider before the registry gains a row for it, the pair is VALID - so the
validator reports no bad token - and no schema resolves - so, by the no-cascade rule above,
the loader emits no unknown-key diagnostic and the validator no required-key diagnostic.
`config` would be silently free-form for that provider, which is precisely the hole this
change exists to close. The design anticipates a second provider, so this is a near-term
path. The pin is a test asserting that `CollectorConfigSchema.For` returns non-null for
every reachable pair - `(integration, p)` for each `p` in `IntegrationProvider.Tokens`, and
`(t, absent)` for each non-integration type token - which is five assertions today and
grows with the token sets. Making an unregistered-but-valid pair a diagnostic in its own
right was the alternative; it costs a runtime diagnostic path and a message for a condition
that is a compile-time-constant property of two static token sets, so the test is the
cheaper pin for the same guarantee.

Two consumers use it:

- `ConfigLoader` reads the document's `type` and `provider` scalars from the
  representation model (it already does this for `kind`), looks up the schema, and diffs
  the `config` mapping's keys against it, emitting the same `Unknown field '<k>' on
  Collector config.` diagnostic shape it emits for unknown top-level keys. When no schema
  is registered the loader emits NO config-key diagnostics -
  the validator will already name the bad token, and cascading one diagnostic per key on
  top of it is noise.
- `ConfigValidator` enforces the `Required` flag (a required key absent from the parsed
  config is an error) and all the value rules. It skips the required-key check on the same
  condition, for the same reason.

**"No schema resolves" covers an ABSENT `provider`, not only an unknown token, and that has a
consequence worth stating rather than discovering.** A `type: integration` collector that omits
`provider` resolves nothing, so it gets no missing-`checks` diagnostic - where the pre-merge
`ValidateEvidenceCollectors` reported a missing non-empty `checks` on every integration
collector unconditionally. The author fixes the provider, re-runs, and only then learns the
`checks` are missing too.

This is a consequence of the no-cascade rule, not a further accepted parity difference, and the
distinction is exact rather than convenient. The three accepted differences are VERDICT changes -
a document that validates today fails after the merge, or the reverse. This is not: the
document is rejected before the merge and after it, and only the diagnostic SET differs.
Reporting the missing `checks` anyway was the alternative and is rejected on the registry's own
terms: requiredness is a property of the PAIR, so with no provider there is no requiredness to
report that would not be `(integration, fleet)`'s key set hard-coded against `type` alone -
exactly the second system D2 removes below, and exactly what a second integration provider with
a different key set would contradict. Unlike an unknown provider token, an absent one is not
even a value the author can be told is wrong; it is the missing input the lookup needs. A Core
test pins the no-cascade verdict for the absent-provider case alongside the unknown-token ones,
so the reasoning is carried rather than re-derived.

**The mirror case - a STRAY `provider` on a non-integration type - resolves on `type` alone, and
that is not an exception to the no-cascade rule but an application of it.** A `type: training`
collector that also declares `provider: fleet` names a pair no row registers. A plain pair lookup
would return nothing, so the collector would silently lose its missing-`pass_mark`,
missing-`quiz`, and unknown-key diagnostics on top of the stray-`provider` one it correctly gets.
`For` therefore retries the lookup with no provider component when the pair misses.

The two cases are decided on the same principle, not opposite ones. Suppression is right when the
provider is the missing or wrong INPUT the lookup needs - on the integration path the provider IS
the selector, so without a good one there is no key set to speak of. Off that path the model
forbids a provider outright, every row such a type registers is a no-provider row, and the key set
is therefore fully determined by `type`: the stray token selects nothing, so it must not conceal
anything either. Deleting it - the only repair available - would not change which schema applies,
which is exactly what distinguishes it from supplying an absent provider.

The retry is safe on the integration path by construction: no `(integration, -)` row is
registered, so an integration collector with an absent or unknown provider still resolves to null
through both lookups. Both verdicts are pinned by Core tests.

**Why a required-key flag rather than leaving requiredness to the validator:** it puts
"which keys does `training` need" in one table next to "which keys may `training` have",
so the two cannot drift. The rule that `manual` must not declare `pass_mark`/`quiz` then
falls out of the key set for free - those keys are simply not registered for `manual`, so
they are rejected by the same unknown-key path as a typo. That is a diagnostic MESSAGE
change from today's dedicated "only valid for a training template" wording, but the
behaviour (reject) is identical, which is the parity the acceptance criterion asks for.

**Why the key is `(type, provider)` and not just `type`:** the issue fixes it, and it is
the right axis. A future second provider (`intune`, say) under `type: integration` will
need different keys from `fleet`. Keying on `type` alone would force the union of every
provider's keys onto every provider.

### D3: `provider` is authored on the collector and cross-checked against the connection

`type: integration` requires `provider`, drawn from `IntegrationProvider.Tokens` (the
shared set that `Integration.provider` already uses; V1 = `{ fleet }`). `provider` MUST be
absent on any other type. A collector's `provider` MUST equal the `provider` of the
`Integration` its `connection` names; a mismatch is an error naming the collector, its
provider, and the connection's.

**Why author it rather than derive it from the connection:** the config schema key must be
determinable from the document alone. If `provider` were derived, a collector whose
`connection` is dangling (already an error) would have no resolvable schema, so its
`config` keys could not be checked and the author would get one error now and a second
error after fixing the first. Authoring it also makes the collector readable in isolation -
you can see which adapter runs it without following the connection reference - and it is
what the shared-token-set decision in #126 is for. The duplication is bounded by the
cross-check, so the two can never disagree silently.

**Alternative rejected:** derive from the connection and skip config validation when the
connection does not resolve. Cascading, order-dependent diagnostics; and it makes the
`(type, provider)` schema lookup depend on cross-document resolution, which the loader
(which owns unknown-key detection) has no access to.

### D4: One `collectors` table, one `config` JSON column

Migration `src/Freeboard.Persistence/Migrations/021_collector_merge.sql`, forward-only and
NOT atomically replay-safe, matching the `015`/`018`/`019`/`020` convention (MySQL DDL
implicit-commits per statement and the runner records `schema_migrations` only after the
whole file succeeds; recovery is restore-and-rerun, always available pre-production).

```sql
CREATE TABLE IF NOT EXISTS collectors (
    id VARCHAR(190) ... utf8mb4_bin NOT NULL,
    api_version VARCHAR(64) NOT NULL,
    title VARCHAR(512) NOT NULL,
    control_id VARCHAR(190) ... utf8mb4_bin NOT NULL,
    vendor_id VARCHAR(190) ... utf8mb4_bin NULL,
    connection_id VARCHAR(190) ... utf8mb4_bin NULL,
    type VARCHAR(32) NOT NULL,
    provider VARCHAR(32) NULL,
    frequency VARCHAR(16) NOT NULL,
    threshold INT NULL,
    config JSON NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_collectors_control_id (control_id),
    KEY ix_collectors_vendor_id (vendor_id),
    KEY ix_collectors_connection_id (connection_id),
    CONSTRAINT ck_collectors_integration_provider CHECK (type <> 'integration' OR provider IS NOT NULL),
    CONSTRAINT fk_collectors_control    FOREIGN KEY (control_id)    REFERENCES controls (id)                ON DELETE RESTRICT,
    CONSTRAINT fk_collectors_vendor     FOREIGN KEY (vendor_id)     REFERENCES assets (id)                  ON DELETE RESTRICT,
    CONSTRAINT fk_collectors_connection FOREIGN KEY (connection_id) REFERENCES integration_connections (id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

Identity is keyed on `id` only (a control MAY have several collectors), so there is no
secondary unique key - carried forward from both source tables. Constraint names are fresh
(`fk_collectors_*`), so they cannot collide with the source tables' `fk_evidence_collectors_*`
constraints, which are schema-wide in InnoDB and still live until the drops at the end.

`ck_collectors_integration_provider` is the migration's refusal guard, and it is what
settles what `021` will and will not migrate (see "What the migration refuses" below). It
is enforced (MySQL 8.0.16+), it names itself in the failure message, it costs one line, and
`020`'s `ck_scopes_single_target` is the in-repo precedent for expressing an invariant this
way. It carries beyond the migration: it also stops the importer ever writing a
`type: integration` row with no provider. It is deliberately one-directional. The reverse
half of the rule - `provider` absent on every other type - is enforced by validation, and
the migration cannot produce a violation of it (step 1 sets `provider` only for
`type = 'integration'`, step 2 sets it NULL), so pinning it in DDL would buy nothing now and
make a future type that wants a provider a migration.

**One `config` JSON column and no `checks` column.** The old
`attestation_templates.body`/`fields`/`pass_mark`/`quiz` columns and the old
`evidence_collectors.checks` column all become keys inside `config`, matching the model
after D1a. That drops five columns and keeps the "config is whatever the
`(type, provider)` schema says" story true at the storage layer too: adding a key for a
new provider is a JSON change, not a migration. The cost is that no query can filter on
`pass_mark` or reach into `checks` by column; nothing does, and MySQL JSON path
expressions cover it if something ever needs to.

Steps, assuming the two source id spaces are DISJOINT (pre-production, no data
contract; a collision fails on the duplicate primary key rather than merging two
collectors), mirroring `019`/`020`:

0. Guard the source table, BEFORE the `CREATE TABLE`, so a refusal here leaves the schema
   completely untouched:

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

   This is the pre-merge type-conditional rule at the LIST level, enforced at rest, and the
   guard is stated by its predicate rather than by one refused class because it refuses more
   than one. Both branches are load-bearing:

   - The non-integration branch refuses a `checks` or a `connection_id` on a type that may
     carry neither.
   - The integration branch refuses a `checks` that is not a non-empty JSON array OF
     OBJECTS. The non-empty-array half is what today's `ValidateEvidenceCollectors` requires
     (`collector.Checks.Count == 0` is an error on `type: integration`), and every way to fail
     it lands a merged row
     the target contract rejects: a SQL NULL `checks` copies as `config = NULL` on a collector
     whose `(integration, fleet)` schema makes `checks` required; an empty array or a JSON
     null copies as `{"Checks": []}` / `{"Checks": null}`, neither of which the storage
     contract permits for an absent member; and a non-array value copies as a `Checks` member
     the typed read cannot bind, which is a `JsonException` at the store boundary and a 503 on
     every collector read until a sync repairs the row. The SQL NULL case is also the
     pre-`018` shape: `018` added `connection_id` and `checks` in one `ALTER TABLE` and
     rewrote no rows, so an integration row that predates it has BOTH columns NULL and is
     refused HERE, at step 0, not later by `ck_collectors_integration_provider`.
   - The items-are-objects half is what makes the read-boundary premise below TRUE rather
     than merely plausible. A scalar item (`[1]`, `["x"]`), a JSON null item (`[null]`), and
     a nested-array item (`[[]]`) each fail the typed read: `[1]` and `["x"]` are a
     `JsonException` binding a number or a string to a `Check`, and `[null]` binds to a list
     whose element is NULL, which the endpoint projection and the register page dereference.
     Both are the same 503-on-every-collector-read outcome as a non-array value, so they
     belong on the same side of the guard as it does.
   - The known-member TYPE half closes the last shape that reaches the typed read and does
     not bind. `Check.SourceKey`, `Name`, and `Severity` are `string` members, and a PRESENT
     member of an incompatible JSON type is not ignored the way an unmatched one is:
     `[{"SourceKey": 123, "Name": "MFA", "Severity": "Hard"}]` is a
     `JsonException` ("The JSON value could not be converted to System.String") at the same
     store boundary as `[1]`. A JSON NULL on one of the three does not throw, but it binds
     `null` into a member declared non-nullable, which every downstream projection reads as
     non-null. So the three members are constrained to `"type": "string"`, which refuses both
     the wrong token type and the null. Nothing else about them is constrained.

   **What the guard does NOT check, stated so the prose does not over-claim, and why this is
   the terminus rather than a fourth tightening.** The `items` schema names the three known
   members and gives each only a JSON token type. It declares NO `required`, so
   `[{"nope": 1}]` and `[{}]` still pass; NO `enum`, so a `Severity` of `"Banana"` still
   passes; NO `uniqueItems` and no cross-item rule, so duplicate `Name` or `SourceKey` values
   still pass; and it says nothing about any UNKNOWN member of a check item. Everything it
   omits is a validator-owned FIELD rule (`source_key`/`name`/`severity` present, the severity
   token set, name and source-key uniqueness). Those travel inside the column value VERBATIM
   and still bind on read, so they fall under the scope limit the general rule states below.
   The residual is acceptable for two reasons that are checkable rather than hopeful: the
   post-migration `gitops sync` is a REQUIRED step and re-authors every one of these values
   from the authored document, and `gitops validate` names each of these defects in exactly
   the same terms before the migration and after it, so refusing here would add a second
   reporting surface for a defect the first one already reports. With the item's type pinned
   and its three known members' token types pinned, there is nothing further this predicate
   can constrain WITHOUT restating a domain rule the registry and the validator own - which
   is why the guard stops here and should not be tightened again. If a future edit finds it
   cannot express a read-boundary rule without also encoding a domain rule, the right move is
   to accept the residual and document it, or move the check into the sync step - not to grow
   this predicate.

   **The read-boundary premise, stated so it is true as written.** An OBJECT item whose known
   members are absent or well-typed still READS: `JsonSerializer` binds `{"nope": 1}` to a
   `Check` whose members keep their empty-string defaults, because a missing JSON member
   leaves the record's own default in place. That guarantee holds for an object item whose
   present known members carry the right token type, and for nothing else - an unmatched
   property is IGNORED while an incompatible one THROWS - which is precisely why the guard,
   not an argument, is what confines the column to shapes that bind. Excluding item FIELD
   validation is therefore a scope decision; excluding item TYPE validation, at either level,
   would have been a correctness hole.

   The `JSON_SCHEMA_VALID` schema is the minimal expression of that boundary: `array` plus
   `minItems: 1` plus an `items` object schema that types only `SourceKey`, `Name`, and
   `Severity`. The member names are the STORED PascalCase spelling the storage contract fixes
   (D1c), which is what `evidence_collectors.checks` already holds per item - `ImportPlan`
   serializes `Check` with default naming, and `ImportPlanTests` asserts `"SourceKey":"12"` -
   so the schema matches the column it guards rather than the authored snake_case spelling.
   Restating the field rules in it would put validator-owned rules in DDL, in a file that is
   about to drop the table it constrains.

   **The loader normalizes; the store does not. That asymmetry is why the item-type rule has
   to be a guard and not an argument, so it is recorded here rather than left implicit.**
   `ConfigLoader` explicitly repairs the AUTHORING path before the validator ever sees a
   document: an explicit-null `checks:` item becomes `new Check()`, and null `fields`/`quiz`
   items are dropped, precisely so the validator reports the missing fields rather than
   throwing. Nothing on the STORAGE path does any of that. `MySqlComplianceStore` reads its
   `fields` and `quiz` columns with a bare `JsonSerializer.Deserialize<List<T>>` and projects
   the result straight into the read model, and the planned `DeserializeConfig` binds the
   storage record the same way. So a column value that never came through the loader carries
   no such guarantee, and reasoning from "the loaded model is always well-formed" to "the
   stored column always reads" borrows a property from a path the column did not take. The
   guard is what restores it: once the only item shape that can reach the read path is an
   OBJECT whose known members carry their declared token types, `JsonSerializer`'s own
   missing-member defaulting supplies the rest.

   **The same guard applies to `attestation_templates.fields` and `.quiz`, and an earlier
   draft's refusal to extend it there was inconsistent with this plan's own clause 1.** Step 2
   re-shapes both columns into `config` by exactly the mechanism step 1 re-shapes `checks` -
   the column value is carried across as an opaque JSON value - so `[1]`, `[null]`, `[[]]`, and
   `[{"Label": 123}]` in either column reach the typed read and fail it the identical way, for
   the identical outcome: a `JsonException` at the store boundary, or a NULL list element the
   endpoint projection and the register page dereference, and a 503 on every collector read
   until a sync repairs the row. The refusal table's "attestation rows are not read by the
   guard" line was true of the guard as written and false as a justification: clause 1 scopes
   refusal to the columns the merge RE-SHAPES, and these are exactly such columns. Guarding one
   re-shaped column and not the other two would leave the read-boundary premise true for
   integration collectors alone, which is a correctness hole rather than a scope decision:

   ```sql
   ALTER TABLE attestation_templates
       ADD CONSTRAINT ck_attestation_templates_premerge_payload CHECK (
           (fields IS NULL OR JSON_SCHEMA_VALID('{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Label":{"type":"string"},"Type":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}', fields))
       AND (quiz  IS NULL OR JSON_SCHEMA_VALID('{"type":"array","items":{"type":"object","properties":{"Id":{"type":"string"},"Prompt":{"type":"string"},"Answer":{"type":"string"},"Options":{"type":"array","items":{"type":"string"}}}}}', quiz)));
   ALTER TABLE attestation_templates DROP CHECK ck_attestation_templates_premerge_payload;
   ```

   Its scope is the same and it stops in the same place: item type, and the JSON token type of
   each known member when present. No `required`, no `enum`, no uniqueness. `Options` is typed
   because it is the one nested member that is itself a collection, and a non-string element or
   a JSON null there does not bind either. Two deliberate differences from the `checks` guard,
   both following from what the merged rules require rather than from taste. There is no
   `minItems`: neither list is required by any registered schema, so an empty array reads back
   cleanly and carries only the cosmetic defect of a member the storage contract would have
   omitted - refusing it would refuse a healthy database, where refusing `checks: []` refuses a
   row the `(integration, fleet)` schema rejects. And there is no `IS NOT NULL` conjunct, for
   the same reason: SQL NULL is the normal, valid state of both columns.

   `body` and `pass_mark` need no guard: `TEXT` binds to `string?` for any content and `INT`
   binds to `int?`, so neither can hold a shape the read cannot take.

   **This also makes step 2's empty-config test exact, which is the cheaper of the two fixes
   available for it.** That step yields SQL NULL only when all four source columns `IS NULL`,
   and the JSON null LITERAL is not SQL NULL: it would pass the test, then be deleted by
   `JSON_MERGE_PATCH` as a null-valued member, and land `config = '{}'` - which D1c says must
   never happen, since an empty config is SQL NULL. Only `fields` and `quiz` are JSON columns,
   and an array schema rejects a JSON null, so the guard closes it. Widening the `CASE` to test
   `JSON_TYPE(...) = 'NULL'` as well was the alternative; it is a second expression guarding a
   shape the guard has already made unreachable, and the guard is needed anyway for the item
   shapes the `CASE` cannot see.

   `ADD CONSTRAINT ... CHECK` validates the existing rows and fails the statement
   with `ERROR 3819 Check constraint 'ck_evidence_collectors_premerge_payload' is
   violated.` when any row breaks it, so each guard names itself exactly as
   `ck_collectors_integration_provider` does. It is added and immediately dropped because
   its whole job is done the instant the `ALTER` succeeds - nothing else writes
   `evidence_collectors` during the migration - and dropping it keeps the file replayable:
   a failed `ADD` leaves no constraint behind, and a successful one leaves none either, so
   a re-run after a repair cannot fail on a duplicate constraint name. `JSON_SCHEMA_VALID`
   is a deterministic built-in and is permitted in a MySQL 8.4 CHECK expression (confirmed
   against MySQL 8.4.10 by adding this exact constraint, with a row of every refused and
   every copied shape seeded in turn); the `checks IS NOT NULL` conjunct is
   not redundant, because a SQL NULL `checks` makes `JSON_SCHEMA_VALID` return NULL, the
   whole branch UNKNOWN, and an UNKNOWN CHECK is treated as satisfied, which would let the
   pre-`018` row through. See "What the
   migration refuses" below for why these rows are refused rather than copied or stripped.

   **This is the one place `021` moves the server-version floor, and it moves it by one patch
   release.** Enforced CHECK constraints are MySQL 8.0.16+ and `JSON_SCHEMA_VALID` is
   8.0.17+, so `021` requires MySQL 8.0.17 or later. Nothing in the repo is broken by that:
   every version statement here names 8.4 (the test compose pins `mysql:8.4`, and the
   migrations' own headers reason against 8.4), and no support claim exists for anything
   older. The floor is stated rather than left implicit so the sentence is exactly true.
1. Copy `evidence_collectors`, joining `integration_connections` to derive `provider`:
   `id`, `api_version`, and `title` carried verbatim (all three are NOT NULL on
   `collectors`); `type` re-tokenized with `CASE` (`manual-attestation` -> `manual`,
   `training-attestation` -> `training`, else unchanged); `provider` =
   `ic.provider` when `type = 'integration'`, else `NULL`; `config` =
   `CASE WHEN ec.checks IS NULL THEN NULL ELSE JSON_OBJECT('Checks', ec.checks) END`.
   That expression composes `Checks` for whatever row it copies and deliberately carries no
   type test and no shape test of its own: step 0 has already refused every non-integration
   row with a non-NULL `checks`, so by the time this runs a non-NULL `checks` implies
   `type = 'integration'`, and it has refused every integration row whose `checks` is not a
   non-empty array, so the composed `Checks` member is always a non-empty array. Adding a
   `type` test here instead would silently strip the
   checks, which is the one disposition both of this plan's precedents reject (see "What
   the migration refuses"). Carry
   `frequency`, `threshold`, `created_at`, `updated_at`, and ALL THREE foreign-key columns -
   `control_id` and `vendor_id` from `012`, `connection_id` from `018` - carried verbatim.
   Naming the three explicitly matters: `connection_id` is the one a shorthand drops, and
   `ck_collectors_integration_provider` would not catch the loss, because `provider` is
   derived from the JOIN rather than from the carried column. Every integration collector
   would land with a null connection, the startup unresolvable-token scan would see nothing
   to warn about, and the widened fingerprint would hash a null connection.
2. Copy `attestation_templates`, composing `config` from the `body`, `fields`, `pass_mark`,
   and `quiz` columns under the keys `'Body'`, `'Fields'`, `'PassMark'`, `'Quiz'` (D1c),
   with absent members dropped and the whole composition NULL when every one of the four
   source columns is NULL;
   `id`, `api_version`, `title`, `control_id`, `type`, `created_at`, and `updated_at`
   carried verbatim (`api_version`, `title`, `type`, and both timestamps are NOT NULL on
   both tables, and `attestation_templates.type` is already `manual` or `training`, so
   carrying it verbatim is what keeps `ck_collectors_integration_provider` from firing on
   this path); `vendor_id`/`connection_id`/`provider`/`threshold` NULL; `frequency` written
   as `'annual'`.

Between them the two steps account for all thirteen columns of `collectors`. Step 1:
`id`, `api_version`, `title`, `control_id`, `vendor_id`, `connection_id`, `frequency`,
`threshold`, `created_at`, `updated_at` verbatim; `type` re-tokenized; `provider` derived;
`config` composed. Step 2: `id`, `api_version`, `title`, `control_id`, `type`,
`created_at`, `updated_at` verbatim; `vendor_id`, `connection_id`, `provider`, `threshold`
NULL; `frequency` the placeholder; `config` composed.
3. Re-point `collector_credentials`: `DROP FOREIGN KEY fk_collector_credentials_collector`
   then re-add it against `collectors (id) ON DELETE CASCADE`. This MUST happen before the
   drop in step 4, or the drop fails on the live constraint.
4. `DROP TABLE evidence_collectors; DROP TABLE attestation_templates;`

**Step 1 carries only schema-owned data; the old free-form `config` map is NOT carried.**
An earlier draft merged `evidence_collectors.config` into the new `config`, priced as a
harmless transient stale key. That was wrong on two counts, and both are load-bearing:

- The old column is a `Dictionary<string,string>` serialized verbatim, so it can hold any
  ad-hoc key, including a credential-shaped one. Persisting it into the merged column would
  contradict this change's own requirement that no `config` key holds credential material
  and that an unregistered key is rejected - the change would ship a column that violates
  the rule it introduces, on day one, in the same migration.
- Every value in that map is a JSON string. A carried key that collides with a
  `CollectorConfig` member - `Checks`, `Fields`, `Quiz`, a `PassMark` of `"90"` - binds to
  the wrong CLR type when the new typed `DeserializeConfig` reads the column, and
  `JsonException` from the store is not a stale-key nuisance: it takes `GET /collectors`,
  `/settings/collectors`, and the SoA drill-down to a 503 until a sync happens to rewrite
  that row. A read outage is not a transient cosmetic defect.

So the migration composes the new `config` from schema-owned data only: the old `checks`
column in step 1, the four template form columns in step 2. An operator who authored
free-form `config` keys hand-migrates them into the authored document alongside the rest of
the hand-migration (see the Migration Plan), and `gitops sync` writes them. Nothing is
silently stripped in the sense that matters - the authored YAML is the source of truth and
is untouched; what is dropped is a database copy of it that the next sync overwrites
anyway.

**Alternative rejected: keep the carry-over and make the store tolerate a type mismatch on
a carried key.** That means a converter, or per-member `try`/`catch`, whose only purpose is
to survive a pre-production migration artefact for the minutes between `system migrate` and
`gitops sync`. It adds a permanently-live error-swallowing path to the read boundary to
paper over a one-off, and error-swallowing at a redaction boundary is exactly where it is
least welcome. Dropping the column is one fewer expression in the migration and no code at
all.

**Both `config` compositions have to be written around MySQL's JSON NULL boundary, and it
bites twice.** `JSON_OBJECT` does NOT propagate SQL NULL the way `JSON_SET` does:
`JSON_OBJECT('Checks', NULL)` yields `{"Checks": null}`, not NULL. Step 1 handles it with the
explicit `CASE` above. Step 2 has the SAME defect one level further out and it has to be
fixed the same way: dropping the four absent members from `JSON_OBJECT('Body', ..., 'Fields',
..., 'PassMark', ..., 'Quiz', ...)` leaves `{}` when all four source columns are NULL, and
`{}` is not what the storage contract says an empty config is - D1c and the importer both
say SQL NULL. So step 2 wraps the composition in a `CASE` that yields NULL when
`body`, `fields`, `pass_mark`, and `quiz` are all NULL. Within the non-NULL branch, the
member drop can be `JSON_REMOVE` with a per-column conditional path or, more simply,
`JSON_MERGE_PATCH(JSON_OBJECT(), JSON_OBJECT(...))`, whose merge-patch semantics delete
every null-valued member by definition. Both source expressions in the file are now
NULL-checked at the boundary; nothing else in `021` composes JSON. The common shape is the
one to get right in step 1: `evidence_collectors.config` is optional and rarely authored
while `checks` is what every integration collector carries, so the step-1 expression is
written around `checks` and is NULL-safe when `checks` is NULL (the row lands with
`config = NULL`, which is correct for a `script` or `agent` collector).

**What the migration refuses, and why refusing beats documenting.** FOUR classes of source
row are refused, by three named constraints at two different moments. An earlier draft
enumerated only two and named the pre-`018` row as the provider guard's example; the step-0
guard expresses the pre-merge type-conditional rule in FULL, so it catches that row first. A
later draft enumerated three and left `attestation_templates` unguarded, which contradicted
clause 1 below on columns the merge re-shapes.
The mapping is therefore stated as a table before the prose, because the guards' scopes
overlap on `type: integration` and "which constraint fires for this row" has to be
answerable without re-deriving it:

| Source `evidence_collectors` row | Outcome | Operator action |
| --- | --- | --- |
| `integration`, `checks` a non-empty JSON array of well-typed objects, `connection_id` set | copies; `provider` derived from the connection | none |
| `integration`, `checks` SQL NULL - the pre-`018` shape, in which `connection_id` is NULL too | step 0 REFUSES on `ck_evidence_collectors_premerge_payload` | repair, re-run; nothing was created, so nothing to clean up |
| `integration`, `checks` an empty array, a JSON null, or a non-array value | step 0 REFUSES on `ck_evidence_collectors_premerge_payload` | as above |
| `integration`, `checks` an array with a non-OBJECT item - `[1]`, `["x"]`, `[null]`, `[[]]` | step 0 REFUSES on `ck_evidence_collectors_premerge_payload` | as above |
| `integration`, `checks` an array of objects, one of which carries a KNOWN member of the wrong JSON type or a JSON null - `[{"SourceKey": 123, ...}]`, `[{"Name": null}]` | step 0 REFUSES on `ck_evidence_collectors_premerge_payload` | as above |
| `integration`, `checks` a non-empty JSON array of well-typed objects, `connection_id` NULL - the hand-edited row | passes step 0; step 1 REFUSES on `ck_collectors_integration_provider` | repair, `DROP TABLE collectors`, re-run |
| `integration`, `checks` a non-empty array of objects whose known members are ABSENT or well-typed but whose FIELD rules fail - `[{"nope": 1}]`, `[{"Severity": "Banana"}]`, a duplicate `Name` | copies; the guard types the three known members, it does not enforce their presence, their token set, or their uniqueness | none at migration time; `gitops validate` names it and the mandatory sync re-authors it |
| non-`integration`, `checks` SQL NULL and `connection_id` NULL | copies; `config` NULL | none |
| non-`integration` with any non-NULL `checks` or a non-NULL `connection_id` | step 0 REFUSES on `ck_evidence_collectors_premerge_payload` | repair, re-run; nothing to clean up |
| an `evidence_collectors` id equal to an `attestation_templates` id | passes step 0; step 2 FAILS on the `collectors` PRIMARY KEY, unnamed | rename one id, sync, `DROP TABLE collectors`, re-run (see the risks) |
| `attestation_templates`, `fields` or `quiz` not a JSON array of objects with well-typed known members - a JSON null, a non-array, `[1]`, `[null]`, `[[]]`, `[{"Label": 123}]` | step 0 REFUSES on `ck_attestation_templates_premerge_payload` | repair, re-run; nothing was created, so nothing to clean up |
| `attestation_templates`, `fields` or `quiz` an empty array, or an array of objects whose known members are ABSENT or well-typed but failing a FIELD rule | copies; neither list is required by any registered schema and the value binds | none at migration time; the mandatory sync re-authors it |
| any other `attestation_templates` row | step 2 | none |

The repair is the same for every refused row and needs only the pre-merge app: run
`freeboard gitops sync` against the pre-merge schema, which rewrites the row from the
authored document, or delete the stale row.

*The misplaced-payload row.* An `evidence_collectors` row whose `type` is not `integration`
and whose `checks` or `connection_id` is non-NULL is refused by the step-0 guard,
before anything is created. Step 1's `config` expression composes
`JSON_OBJECT('Checks', ec.checks)` for every row it copies and carries `connection_id`
verbatim for every row it copies, so without the guard a `type: manual-attestation` row with
a stray `checks` column would land as `type: manual` carrying a `Checks` config member that
the `(manual, -)` schema does not register, and a stray `connection_id` would land as a
`manual` collector with a connection the merged validation forbids at top level. An earlier
draft copied both. **That is reversed here: `021` REFUSES them.**

*The malformed-or-missing-`checks` integration row.* The same guard's other branch refuses a
`type: integration` row whose `checks` is not a non-empty JSON array of objects with
well-typed known members - SQL NULL, an
empty array, a JSON null, a value that is not an array at all, an array carrying a
non-object item, or an array carrying an object whose `SourceKey`, `Name`, or `Severity` is
present with the wrong JSON type or as a JSON null. This class was not disclosed by
an earlier draft, whose predicate tested only `checks IS NOT NULL` and whose prose described
only the misplaced-payload class; a second draft added the array and length tests but still
admitted `[1]`, `["x"]`, `[null]`, and `[[]]`; a third added the item-type test but still
admitted `[{"SourceKey": 123, ...}]`, which throws at the typed read for the same reason
`[1]` does. It is real and it is the one that catches the
pre-`018`
row, because `018` added `connection_id` and `checks` in one `ALTER TABLE` and rewrote no
rows, so a `type: integration` row that predates it has BOTH columns NULL. `checks` has been
required and non-empty on an integration collector since it landed, and the loader normalizes
every check item to a non-null `Check` before `ImportPlan` serializes it, so this refuses
nothing a
healthy pre-merge database can hold - and refusing it is CORRECT rather than merely harmless,
because each shape lands a merged row the target contract rejects: a NULL `checks` copies as
`config = NULL` where `(integration, fleet)` makes `checks` required, an empty array or a JSON
null copies as a member the storage contract says must be omitted, a non-array value copies as
a `Checks` member the typed read cannot bind - a `JsonException` at the store
boundary, which is a 503 on every collector read until a sync repairs it - and a non-object
ITEM lands the same outcome one level in: a scalar item is a `JsonException` on the same
boundary, and a JSON null item binds to a list with a NULL element that the endpoint
projection and the register page then dereference. A wrong-typed KNOWN MEMBER lands it one
level further in still, and for the same reason: `JsonSerializer` IGNORES an unmatched
property but THROWS on a present one it cannot convert, so `{"SourceKey": 123}` is the same
`JsonException` as `[1]` while `{"nope": 1}` binds to defaults, and a JSON-null member binds
`null` into a member the read model treats as non-null. Narrowing the branch
to let those rows through would be the wrong fix.

*The NULL-provider row.* The derived-provider
join reads `integration_connections` through `evidence_collectors.connection_id`, which
migration `018` added as NULLABLE. A `type: integration` row that carries its `checks` but
whose `connection_id` was never set - a hand-edited row, since the pre-`018` shape has no
`checks` either and is already gone at step 0 - passes the step-0 guard and would copy as
`type = 'integration', provider = NULL`, a combination the new validator forbids. An earlier
draft accepted that row and documented it. **That is reversed here: `021` REFUSES it.** The
`ck_collectors_integration_provider`
check constraint fires on the step-1 insert, the migration fails, the runner reports the
named constraint and does not record the version, and - because the guard fires on the FIRST
copy step, before the credential re-point and before either drop - both source tables are
still intact and no data has been lost. Recovery is `DROP TABLE collectors` and re-run after
the repair, or the restore-and-rerun the header already documents. That drop is valid only
because this abort precedes the step-3 credential re-point; after the re-point the drop would
fail on the live foreign key. Of the FOUR REFUSALS this is the only one that leaves a
partially created `collectors` table - a step-0 refusal never creates one - but it is not the
only way `021` can abort with `collectors` populated: an id shared between the two source
tables aborts step 2 on the unnamed primary key, with the same drop-and-re-run repair and the
same "only before the re-point" caveat (see the risks).

Four reasons this is the right side of the line for all three:

- It follows this plan's own precedent. An id collision between the two source tables is
  deliberately left to fail on the duplicate primary key rather than be quietly merged. A
  row the new contract forbids should fail for the same reason: a hard cutover should not
  leave rows at rest that the thing it is cutting over to would reject.
- It refuses a database that is ALREADY broken, not a valid one, and that is true of all
  four classes. `connection` has been a
  REQUIRED field on an integration collector since the connection reference landed, so a
  persisted `type = 'integration'` row with a NULL `connection_id` cannot come from a
  current, valid config: it is a hand-edited row. The
  misplaced-payload row is the mirror image and is broken the same way: today's
  `ValidateEvidenceCollectors` rejects a `connection` or a `checks` list on any
  non-`integration` collector, and the importer writes NULL for an empty list, so a
  non-`integration` row with a non-NULL `checks` or `connection_id` cannot come from a valid
  authored document either. The malformed-`checks` row is the same story on the integration
  side: `ValidateEvidenceCollectors` requires `Checks.Count > 0` on `type: integration`,
  `ConfigLoader` normalizes every check item to a non-null `Check` before that runs, and
  `ImportPlan.SerializeList` writes SQL NULL for an empty list and a JSON array of serialized
  `Check` records otherwise, so
  a valid importer path can only ever leave a non-empty array of objects whose three members
  are strings there - an empty
  array, a JSON
  null, a non-array value, a non-object item, a wrong-typed or null known member, or (the
  pre-`018` case) SQL NULL are all shapes
  only a stale or
  hand-edited row can hold. The malformed-`fields`/`quiz` template row is that same story once
  more: `ImportPlan` serializes typed `AttestationField` and `QuizItem` records or writes SQL
  NULL, and `013` has never altered either column, so a non-array value, a non-object item, or
  a wrong-typed known member there is reachable only by hand-editing. It has no pre-`018`
  analogue - the only class of the four that no schema history can produce - which is why an
  earlier draft passed over it, and why the argument for guarding it rests on the read
  boundary rather than on reachability.
- The repair is available before migrating and is the operator's to make: run
  `freeboard gitops sync` against the pre-merge schema, which rewrites the row from the
  authored document, or delete the stale row. Neither needs the merged schema.
- The alternative for the misplaced-payload row - dropping the offending value in the copy
  expression - is the one disposition BOTH of this plan's precedents argue against. It is
  the silent strip this plan refused when it declined to carry the free-form `config` map
  into a column it declares closed, and it is the "copy a row the merged contract rejects"
  the NULL-provider decision refused. Refusing is the only option consistent with both.

**Why the step-0 guards live on the SOURCE tables and are dropped immediately, while the
provider guard is a permanent constraint on `collectors`.** `ck_collectors_integration_provider`
earns its permanence: it also stops the importer ever writing a provider-less integration
row, which is a rule about the merged table's contents that outlives the migration. Neither
step-0 guard is like that. They express registry-owned rules: which
`(type, provider)` pair may carry a `checks`, a `fields`, or a `quiz` key at all, and that
`(integration, fleet)`
requires `checks` to be present and non-empty. The registry owns both - pinning either in
DDL would make a
future pair that registers `checks`, a future non-integration type that wants a
`connection`, or a future integration provider whose schema does not require `checks`, a
migration rather than a registry edit. That is exactly the reasoning D4
already gives for NOT pinning the reverse half of the provider rule (`provider` absent on
every other type) in the schema. A transitional constraint on a table the migration is about
to drop refuses the row without leaving that liability behind.

**The counter-position, considered and rejected.** It runs: the copied row is harmless
because `Checks` is a
member of the storage record, so it reads cleanly, it is never scheduled, and the
mandatory sync overwrites it - which puts it in clause 3. That is rejected on the clause
assignment. Clause 3's
first condition is that the source row was VALID pre-merge. This one is not: the pre-merge
validator rejects it. It is clause 1 verbatim, and reading it as clause 3 would put an
unstated exception in the middle of the rule the NULL-provider decision rests on. Its
consequences are also not nil: `CollectorConfigView.Checks` is carried by the read model and
the endpoint projection writes a `checks` key whenever it is non-empty regardless of type,
so a copied row would render a tracked-check list on `/settings/collectors`, in
`GET /collectors`, and in `freeboard collector list` for a `manual` collector until the sync.

**The general rule, because the same question is asked by every other transiently-invalid
state.** An earlier draft stated it in two clauses, which covered only rows that were
already broken. It needs a third, because the merged contract can reject a row whose source
was perfectly VALID:

1. `021` REFUSES a source row that is already invalid under the PRE-merge contract, where
   that invalidity is expressed over the columns the merge RE-SHAPES or DERIVES FROM, and
   the operator can repair it in the authored config before migrating. FOUR classes meet
   that description and all four are refused by a named check constraint before either
   legacy table is dropped: a non-`integration` row carrying a `checks` or a
   `connection_id` (the merge re-shapes `checks` into `config` and carries `connection_id`
   into a table whose validation reads it differently), a `type: integration` row whose
   `checks` is not a non-empty JSON array of objects with well-typed known members (the same
   re-shaping, from the other
   side: the
   merged `config` member is composed from that column and its absence or its wrong JSON type
   is what the merged contract or the typed read then rejects), an `attestation_templates` row
   whose `fields` or `quiz` is not a JSON array of objects with well-typed known members (the
   same re-shaping again, on the other two re-shaped columns, with the same read-boundary
   failure), and a `type: integration` row
   with no derivable `provider` (the merge derives
   `provider` from `connection_id`). The scope limit is deliberate and
   is what keeps the clause honest: a pre-merge-invalid value in a column the migration
   carries VERBATIM - a `type` token outside the set, a blank `frequency`, an out-of-range
   `threshold` - is copied as it stands, because the merge neither creates nor hides it. It
   was rejected by `gitops validate` before the migration and is rejected by it after, in
   the same terms, so refusing it would add a second reporting surface for a defect the
   first one already names. That limit is also why the step-0 guard stops at JSON TOKEN
   TYPES: an object item whose known members are absent, or present and well-typed but
   failing a FIELD rule (the severity token set, name and source-key uniqueness), travels
   inside the column value verbatim and still binds on read, so it is carried, not refused.
   Token type is a different matter at every level and is inside the clause, because a
   non-object item, and equally an object carrying a wrong-typed or null known member, does
   not bind on read at all.
2. It writes a documented placeholder where the source SCHEMA structurally cannot supply a
   value the merged schema requires AND a placeholder exists that is valid at rest.
3. It COPIES, without refusing and without inventing a value, a row that was VALID
   pre-merge and that the MERGED contract would reject, when the missing value is
   structurally absent from that source row and the operator has no pre-merge authoring
   shape in which to supply it. Refusing there would refuse a HEALTHY database, which is
   the opposite of what clause 1 exists for. The price is a row at rest that
   `gitops validate` would reject, and it is payable only on three conditions, all of which
   hold here:

   - **It still READS cleanly.** A NULL `config` deserializes to an empty typed config
     through exactly the path a `script` collector takes, so no store-side failure and no
     redaction gap follows from the copy.
   - **Nothing CONSUMES the missing value.** The row is not scheduled - only
     `type == "integration"` is claimed - and no grading runtime exists to read the absent
     `pass_mark`/`quiz`. This condition is deliberately narrower than "is not acted on at
     all", because that broader claim is FALSE and the difference matters: a migrated
     `manual`/`training` row copied from `evidence_collectors` keeps its `vendor_id`, and
     `collector_credentials` is re-pointed at `collectors`, so it can still hold a
     credential and POST evidence. Ingest is type-agnostic by an explicit rule of the
     evidence-ingest capability, which this change preserves (D9a). It reads only the
     collector's `vendor`, its control's `maps_to`, and its `frequency` - all three of
     which the copied row carries from its own authored source row - so it neither reads
     nor is misled by the missing form. An operator who ingests during the window before
     the sync observes exactly what they observed pre-merge: the run is appended against
     the collector's own authored cadence and its own vendor. The one thing to know is that
     it is the collector-half id that must survive the hand-migration - if the operator
     instead keeps the template id, the sync prunes the collector row, its credential
     cascades away, and the machine's next POST is a `401` (D4a says to keep the collector
     id for exactly this reason). Appended runs are unaffected either way:
     `evidence_runs.collector_id` is scalar with no foreign key.
   - **The mandatory post-migration sync RESOLVES it**, with the gap documented rather than
     discovered. It resolves by upsert-and-prune, not by repair in place: the sync writes
     the one hand-migrated document and prunes the surplus row of the pair as absent.

Applying the rule to all nine cases:

- `checks` or a `connection_id` on a non-`integration` row: already invalid pre-merge (the
  validator's `else` branch rejects both), repairable before migrating -> REFUSE at step 0
  (clause 1).
- A `checks` that is not a non-empty JSON array of objects with well-typed known members on a
  `type: integration` row - SQL
  NULL (the
  pre-`018` shape), an empty array, a JSON null, a non-array value, an array carrying a
  non-object item (`[1]`, `["x"]`, `[null]`, `[[]]`), or an array carrying an object whose
  `SourceKey`/`Name`/`Severity` is present with the wrong JSON type or as a JSON null
  (`[{"SourceKey": 123, ...}]`): already invalid
  pre-merge (the validator's integration branch requires `Checks.Count > 0` and typed
  non-empty fields on each item, and the loader
  normalizes every item to a non-null `Check` before it runs), repairable
  before migrating -> REFUSE at step 0 (clause 1).
- A `fields` or `quiz` on an `attestation_templates` row that is not a JSON array of objects
  with well-typed known members - a JSON null, a non-array value, an array carrying a
  non-object item, or an object item whose `Id`/`Label`/`Type`/`Prompt`/`Answer`/`Options`
  carries the wrong JSON type: already invalid pre-merge (the pre-merge template validation
  requires typed, non-empty members on each item, and the loader normalizes every item before
  it runs), repairable before migrating -> REFUSE at step 0 (clause 1).
- An empty `fields`/`quiz` array, or an OBJECT item whose known members are ABSENT or
  well-typed but failing a FIELD rule: invalid pre-merge in some cases, but it lives in a value
  the migration carries VERBATIM and it still binds on read, and neither list is required by
  any registered schema -> COPY, outside clause 1's scope limit exactly as the `checks`
  equivalent is.
- NULL `provider` on a `type: integration` row that does carry its `checks`: already invalid
  pre-merge, repairable before migrating -> REFUSE at step 1 (clause 1).
- An OBJECT check item whose known members are ABSENT, or present and well-typed but failing
  a FIELD rule (`[{"nope": 1}]`, a `Severity` of `"Banana"`, a duplicate `Name`), inside an
  otherwise well-formed `checks`
  array: invalid pre-merge,
  but it lives in a value the migration carries VERBATIM and it still binds on read, so it is
  outside clause 1's scope
  limit -> COPY, and `gitops validate` names it before and after in the same terms.
- The `frequency` on a migrated attestation: `attestation_templates` has no cadence column
  at all, so every valid template row lacks one and refusing would refuse a healthy
  database -> PLACEHOLDER, documented, overwritten by the required sync (clause 2).
- The form on a migrated `training-attestation` collector: valid pre-merge, but
  `evidence_collectors` has no `pass_mark` or `quiz` column to carry, and no placeholder
  quiz could be invented that is anything but a lie -> COPY AS IS (clause 3). D4a below.
- Config keys the hand-migration has not yet supplied: these live in the authored YAML, not
  in any source column, so `021` cannot see them and has nothing to refuse. They are a
  `gitops validate` concern, which is where they surface.

#### D4a: A migrated attestation pair lands as two rows, and `021` leaves it that way

Today an attestation is authored as a PAIR on one control: an `EvidenceCollector` of
`type: manual-attestation`/`training-attestation` carrying the cadence and the optional
vendor, and an `AttestationTemplate` carrying the form. The merge exists to make that one
`Collector`. But `021` copies the two source tables independently, so the pair lands as TWO
`manual`/`training` rows on the same control: one with the authored cadence, the optional
vendor and `config = NULL`, and one with the form and the placeholder cadence.

**What actually links the two today: nothing.** `AttestationTemplate` carries no collector
reference and `EvidenceCollector` carries no template reference; neither table has a column
pointing at the other. The pairing is the shared `control_id` and nothing else, and it is
not even a required pairing - both tables key identity on `id` alone with no secondary
unique key, and the validator requires neither side of the pair, so a control may legally
carry a template with no collector, a collector with no template, or several of either.

**Decision: accept the split. `021` neither merges the pair nor refuses the rows.**

*Why not merge by `control_id` in SQL.* The pairing is not a key, so there are configs for
which no correct merge exists: a control with two templates and one collector, or two
collectors and one template, has no pairing the source data expressed, and SQL would have to
pick one - inventing a link that was never authored. Which `id` survives is equally
arbitrary, and the `id` is the collector's identity: `collector_credentials`,
`collector_scheduler_state.collector_id`, and `evidence_runs.collector_id` are all keyed on
it. A merge would additionally have to decide the merged row's cadence and vendor when the
two sides disagree. That is a large amount of silently-wrong data bought to remove a state
the mandatory sync removes anyway.

*Why not refuse `*-attestation` source rows.* Clause 1's refusal is for rows that are
already broken and repairable before migrating. These are neither: a `training-attestation`
`EvidenceCollector` carrying only `id`/`title`/`control`/`type`/`frequency` is entirely
valid under today's rules - the required-field set is exactly those five and the
connection/checks rules special-case only `integration` - and there is no pre-merge shape in
which a `pass_mark` could be put on an `EvidenceCollector`, because that field does not exist
on the kind until this change lands. Refusing would block migration of every database that
uses attestations at all, which is the healthy case. Refusing the whole class would also
refuse the `manual-attestation` rows, which need no refusing: `(manual, -)` registers no
required key, so a migrated `manual` row with `config = NULL` is VALID at rest.

*What is observable in the gap, stated rather than left to be found.*

- The register page and `GET /collectors` list TWO collectors where the authored config
  declares one attestation: one showing the authored cadence and vendor with no form, one
  showing the form with the placeholder cadence.
- The SoA drill-down shows two checks under the control, both tagged `Attestation`, so both
  carry no status and (per D7) no cadence. The visible effect there is a doubled check
  count, not a wrong cadence.
- A migrated `training-attestation` row sits at rest in a state `gitops validate` would
  reject: `type: training` with neither `pass_mark` nor `quiz`. It satisfies clause 3's
  three conditions - a NULL `config` deserializes to an empty view through exactly the path
  a `script` collector takes, so no read fails; the scheduler claims only
  `type == "integration"`, so it is never scheduled, and no grading runtime exists to read
  the absent form; and the required sync resolves it. The exposure is a training entry that
  displays no quiz.
- Ingest is unchanged, and it is the one thing that DOES still act on such a row - which is
  why clause 3's second condition is "nothing consumes the missing value" rather than
  "nothing acts on it". A migrated `manual`/`training` row that came through step 1 keeps its
  vendor and its own authored cadence, so it can still ingest exactly as it could as a
  `manual-attestation` collector, and the placeholder cadence never reaches it (D4, D11).
  Ingest reads the vendor, the control's `maps_to`, and the cadence - never the form - so
  the missing `pass_mark`/`quiz` changes nothing an ingesting collector observes.

*How the gap closes.* The hand-migration folds each pair into ONE `Collector` document, so
`gitops sync` upserts that document's id and prunes the other row as absent. The operator
chooses which id survives, and there is a concrete reason to keep the `EvidenceCollector`
id where one exists: `collector_credentials` is `ON DELETE CASCADE`, so pruning the row a
credential hangs off revokes that credential. Appended evidence is unaffected either way -
`evidence_runs.collector_id` is scalar with no foreign key, so past runs survive the prune
by design.

**Why `'annual'` for a migrated attestation's frequency.** `frequency` is required on every
`Collector` (D11), and `attestation_templates` has no cadence column, so there is no value
to carry. `annual` is chosen because it is the conventional attestation cadence and the
longest window in the vocabulary.

Plan A originally justified this by asserting the value "has no runtime effect". A first
correction to that claimed the placeholder is observable through ingest staleness, because
a collector with a vendor may hold a credential and post evidence whose staleness cadence
is the collector's `frequency`. That correction over-stated the exposure and is itself
corrected here. The placeholder is written ONLY onto rows copied from
`attestation_templates`, and at migration time such a row cannot ingest anything:
`attestation_templates` has no `vendor_id` column at all, so every migrated template row
lands with a NULL vendor, and ingest rejects a collector with no vendor; and
`collector_credentials` foreign-keys `evidence_collectors` (migration `014`), so no
template row can hold a credential either. Migrated `manual-attestation` and
`training-attestation` collectors - the rows that CAN ingest - come through step 1 and
carry their own authored cadence, never the placeholder.

What the placeholder is genuinely observable through is the read surfaces: until the sync
runs, `GET /collectors`, the register page, and `freeboard collector list` report a cadence
for every migrated attestation that no authored document backs. The SoA drill-down is NOT
one of them, and an earlier draft that listed it was wrong: a migrated attestation tags as
`Attestation`, and D7 projects an attestation-tagged check with a null `Frequency`, so the
placeholder never reaches that page. **The conclusion is unchanged
on the corrected facts: `gitops sync` is a REQUIRED step of the migration, not an optional
follow-up, and the migration header must say so.** It is required because the merged table
must be re-authored from validated config before it is trusted - the placeholder cadence
and any free-form `config` the operator hand-migrated both resolve in the same sync. (The
provider derived in step 1 is no longer on that list: a row that could not derive one now
fails the migration rather than waiting for the sync.) `annual` being the longest window
keeps the pre-sync display permissive rather than alarm-generating, which is the right
failure direction for a value that is about to be overwritten. This mirrors `020`'s
precedent of writing a placeholder for a column the source table did not have.

`collector_scheduler_state.collector_id` and `evidence_runs.collector_id` are scalar with
NO foreign key by existing spec, so neither needs re-pointing; the migration must not add
one.

### D5: Read model, redaction, and the merged endpoint

```csharp
public sealed record CollectorRow(
    string Id, string Title, string Control, string? Vendor,
    string Type, string? Provider, string Frequency, int? Threshold,
    CollectorConfigView Config, string? Connection = null);

public sealed record CollectorConfigView(
    string? Body, IReadOnlyList<AttestationField> Fields, int? PassMark,
    IReadOnlyList<QuizItemView> Quiz, IReadOnlyList<Check> Checks);
```

`QuizItemView` is unchanged - it has no `Answer` member, and that absence IS the redaction
boundary. The store's existing `DeserializeQuiz` projection moves into a
`DeserializeConfig` that deserializes the `config` JSON column and projects its quiz items
to `QuizItemView`; the stored JSON keeps the answer for the grading runtime. Keeping one
store-boundary projection means no downstream surface can reconstruct the answer, exactly
as today. Both plans agree this is the right boundary: Plan B named "redacting training
answers only in web/CLI" as a high-severity risk, and this design keeps redaction in
persistence so every surface inherits it.

**`checks` becomes visible on the read surfaces.** This follows from D1a and reverses Plan
A's "checks stays persisted and unexposed" non-goal. Once `checks` is a `config` key and
the read model carries `config`, hiding it again would mean writing code to strip a
non-secret, git-authored field out of one projection. The register's purpose is to show
what proves a control, and the tracked check list is exactly that; only the quiz `answer`
is confidential. The cost is real but small: the page, the API projection, and the CLI
printer each render one more list (`name` and `severity` per check), and their tests grow
a case.

`Connection` keeps its "sourced only for the startup token warning, not rendered" note.

`IComplianceStore` gains `GetCollectorsAsync` and loses `GetEvidenceCollectorsAsync` and
`GetAttestationTemplatesAsync`. `SoaDrilldownInputs` carries one `Collectors` list.
`ComplianceCounts` carries one `Collectors` count.

`GET /api/v1/freeboard/collectors` replaces both endpoints, keeping every property of the
old ones: authenticated-any-user, no `IOrgAccess` narrowing (collectors are org-independent
reference data), GET-only, unaffected by read-only mode, RFC 7807 / 503 on an unreachable
store, ordered by `id`. Response for a training collector:

```json
{ "id": "...", "title": "...", "control": "...", "vendor": null, "type": "training",
  "provider": null, "frequency": "annual", "threshold": null,
  "config": { "pass_mark": 90,
              "quiz": [ { "id": "q1", "prompt": "...", "options": ["a","b"] } ] } }
```

**The wire `config` object omits absent members.** `CollectorConfigView` is a fixed
five-member record, so default serialization would emit all five keys on every collector -
a `training` collector carrying `"checks": []` and a `script` collector carrying all five -
which contradicts the requirement that `config` be empty for a pair whose schema accepts no
key. The projection therefore writes a key only when the member is present: a non-blank
`body`, a non-empty `fields`, a non-null `pass_mark`, a non-empty `quiz`, a non-empty
`checks`. A `script` or `agent` collector serializes `"config": {}`.

Two reasons to state it this way rather than "emit exactly the keys the
`(type, provider)` schema registers". First, it mirrors the storage rule already stated in
the persistence delta - a stored `config` object omits absent members - so the wire object
is the stored object with the answer removed and the keys re-cased, and there is one rule
to remember rather than two. Second, it keeps the registry out of the web layer: the
projection needs no schema lookup, so a registry edit cannot silently desynchronise the
endpoint.

Top-level nullable fields are unaffected: `vendor`, `provider`, and `threshold` are still
emitted as explicit `null` when unset, because the top level is a fixed public shape and
existing clients read those keys.

Wire keys stay snake_case (`pass_mark`, `source_key`), matching the existing client mappers,
and differ deliberately from the PascalCase storage keys (D1c). The page and the CLI apply
the same omit-absent rule by simply not rendering an empty section, which is what both
legacy surfaces already do.

### D6: One register page, one nav entry, one CLI command

`Pages/Compliance/EvidenceCollectors.cshtml(.cs)` is renamed to `Collectors.cshtml(.cs)`
with `@page "/settings/collectors"`; `AttestationTemplates.cshtml(.cs)` is deleted and its
rendering (body, fields, pass mark, quiz - all HTML-encoded, never `Html.Raw`) folded into
the per-collector block. The file stays in the `Pages/Compliance` folder so the existing
`AuthorizeFolder("/Compliance")` convention still gates it, matching the precedent set when
these pages first moved under `/settings`. `ShellNavCatalog` carries one
`ShellNavItem("collectors", "Collectors", "/settings/collectors", "Platform")`; the command
palette indexes it automatically from the same catalog, so no palette change is needed.

The per-collector block also renders an integration collector's `config` `checks` (each
check's `name` and `severity`), per D5.

One piece of markup goes with the free-form map: today's register renders each `config`
entry inside a `data-config-key="<key>"` element, which only makes sense when the key set is
open and unknown to the page. The merged block renders the five named sections instead, so
the attribute has no successor and is removed. It is not in the preserved-marker list and no
test asserts it, so nothing breaks - but it is a deletion of rendered markup and the page
tests should record it rather than let it look like an oversight.

CLI: `AttestationTemplateCommands.cs` and the `attestation-template` group are deleted;
`freeboard collector list` prints each control (with its `evaluation`) and, under it, each
collector's `type`, `provider`, `vendor`, `frequency`, `threshold`, and - from `config` -
either an integration collector's checks or a `manual`/`training` collector's has-body /
no-body indicator, fields, pass mark, and quiz prompts. It keeps its two calls
(`/controls`, `/collectors`) and its exit-code convention (0 / 1 on validation / 3 on
operational). `ApiEvidenceCollector` and `ApiAttestationTemplate` collapse into one
`ApiCollector` with an `ApiCollectorConfig`; `ApiAttestationField` and `ApiQuizItem` are
kept and re-homed under it.

The body indicator is carried over deliberately rather than dropped. The retired
`attestation-template list` prints one per template (`has body` / `no body`) and the
requirement it satisfies names the `body` indicator explicitly, so dropping it would be an
unrecorded regression on a merged command that is meant to absorb the other's output. It is
also what gives `ApiCollectorConfig.body` a consumer: the CLI never prints the markdown
itself (the register page does), only whether there is any.

### D7: SoA check tagging is derived, not a second input list

`ResolveDrilldown` takes one collector list instead of `(collectors, templates)`.
`SoaCheckKind` and its `collector`/`attestation` wire values are KEPT and derived:
`type` in `{manual, training}` tags as `Attestation`, every other type as `Collector`.
Keeping the derived tag preserves the rendered `data-check-kind` attribute, the
`(Kind, Id)` check ordering, the "only collector checks carry an evidence status" rule,
and the E2E markers that assert them. `ControlDetailProjection`'s "Proving checks" section
is unchanged for the same reason.

**The ordering EXPRESSION is preserved; the ordering RESULT changes for one class of row,
and "ordering unchanged" would be misread without this.** Checks sort by `Kind` then `Id`,
and `SoaCheckKind.Collector` is declared before `SoaCheckKind.Attestation`, so the tag
decides which group a check falls in. A pre-merge `manual-attestation` collector sorts in
the first group today and in the second after the merge. On a control carrying both an
integration collector and a former `manual-attestation` collector whose id sorts first, the
two rows therefore swap position - on the SoA drill-down, in the object drawer, and on the
control detail page, since all three consume the same ordered list. This is the tag flip
showing through an unchanged rule, not a second decision, and it needs no rule change: the
new order is the one the rule has always produced for an attestation-tagged check.

**Attestation-tagged checks carry no cadence, and this has to be stated rather than left to
fall out.** `SoaCheckNode` carries a nullable `Frequency` and the SoA page renders it
whenever it is non-null. Today the template branch of `ResolveDrilldown` passes `null`
because `attestation_templates` has no cadence column at all. After the merge every
collector has a required `frequency`, so a naive single loop would start showing a cadence
on attestation checks. The rule is: **a check tagged `Attestation` is projected with a null
`Frequency`.** It is derived in the same expression that derives the tag, so there is no
second code path to keep in sync.

The reason is the same one that already governs the status: the SoA page pairs a check's
cadence with its evidence status, and `Stale` is defined as "older than its cadence window
plus grace". An attestation-tagged check carries no evidence status by an explicit rule in
this capability, so a cadence next to it is a collection promise the page cannot back.
Showing one would be a new, unspecified claim on a surface this change is otherwise only
merging.

The `Vendor` display is NOT nulled the same way: it stays the collector's own optional
vendor for every check of either tag. Vendor is metadata that plays no part in status
interpretation, and today a `manual-attestation` collector already renders its vendor here;
nulling it would lose that. A migrated `attestation_templates` row has no vendor, so no
existing row's rendering changes.

One observable change follows and is accepted: a pre-merge `manual-attestation`
`EvidenceCollector` is tagged `Collector` today and so renders a cadence and a status; after
the merge it is `type: manual` and tags as `Attestation`, so it loses both. The status half
of that is already decided by the derived tag and already specced ("An attestation-tagged
check carries no evidence status"); the cadence half is the same consequence of the same
decision, and splitting them - status hidden, cadence shown - would be the incoherent
option.

**The status loss reaches THREE surfaces, not one, and an earlier draft of this paragraph
said otherwise.** It claimed nothing else was affected because `ControlDetailProjection`
never reads a check's cadence. The cadence half of that is right; the conclusion drawn from
it was wrong, because that projection DOES read the status. It branches on
`check.Kind == SoaCheckKind.Collector` to attach an evidence status and a status-derived
note and falls through to a bare `Note: "Attestation"` otherwise, and its own doc comment
records that both the list page's inline drawer templates and the full-page control detail
call that one helper. A `manual-attestation` collector is tagged `Collector` today, so its
proving-checks row carries a status in the object drawer and on the control detail page as
well as on the Statement of Applicability page, and after the merge it carries none on all
three.

That changes no code and no delta. `ControlDetailProjection` needs no behaviour change: the
kind-based branch is already correct, and the tag it reads is what moved. Nor is a
`web-object-drawer` delta required - that spec sources any per-check status from the
per-collector evidence-status read and states no rule keyed on the check's kind; the
kind-based rule ("Only checks tagged as a collector SHALL carry a status") lives in
`statement-of-applicability`, which this change already modifies. What it does change is the
RENDERING of two surfaces the plan had recorded as untouched, so the loss is named in the
proposal's Impact and in the task that covers the drawer and control-detail tests rather than
left to be discovered.

This compounds with a decision the plan deliberately preserves (D9a): a `manual`/`training`
collector with a vendor may still hold a credential and ingest, so evidence keeps accruing
for such a collector while all three surfaces that showed its status stop showing it. That is
the accepted cost of the derived tag, not an oversight.

### D8: Renames that travel with the kind

`EvidenceCollectorFrequency` -> `CollectorFrequency` (the spec names the type explicitly,
so it carries a `collector-scheduler` delta); `EvidenceCollectorRow` -> `CollectorRow`;
`EvidenceCollectorRowPlan` -> `CollectorRowPlan`; `IScheduledCollectorRunner.RunAsync`
takes a `CollectorRow`. Leaving `EvidenceCollectorFrequency` behind after deleting the
`EvidenceCollector` kind would be a stale name in the file the scheduler spec points at.
`ICollectorCredentialStore`, `ICollectorSchedulerStore`, and `CollectorSchedulerService`
are already collector-named and need no rename.

### D9: Scheduler claiming is unchanged; the fingerprint widens (divergence - resolved to Plan B)

The scheduler still claims only `type == "integration"`; only the enumeration of what it
does NOT claim changes (`script`, `agent`, `manual`, `training`).

The schedule fingerprint changes. Plan A kept `SHA-256("{Type}\n{Frequency}")`, excluding
`provider` and `config` on the reasoning that the fingerprint detects a change that
invalidates the schedule and a config edit does not. Plan B flagged that scope as a
medium-severity concern and would include type, provider, frequency, connection, and
canonical config. **Resolved to Plan B:** the fingerprint becomes `SHA-256` over
`type`, `frequency`, `provider`, `connection`, and the collector's `config` in a canonical
form, which is settled below.

Plan A's reasoning mis-stated what the fingerprint is for. Its actual job, per the comment
on the existing method, is that "a change to it revives a dead/error row via ensure" - it
is a recovery trigger, not a cadence input. Under Plan A's narrow fingerprint an operator
whose integration collector has failed into `error`/`dead` fixes the cause and nothing
happens: the row stays dead until someone changes the type or the cadence. D1a makes that
materially worse, because the two most likely fixes - correcting a mistyped Fleet policy
`source_key` and repointing a `connection` at the right instance - are now precisely a
`config` and a `connection` edit. Having moved `checks` into `config`, this change has to
widen the fingerprint or it ships a regression.

Only `threshold` stays out: it is a scoring input consumed at evaluation, not a collection
input, so changing it cannot fix a failed collection.

**The canonical form is the typed config view, not the column text.** An earlier draft
argued canonicality was free because the fingerprint could hash the raw `JSON` column text,
which MySQL normalises on storage. That contradicts D5: `CollectorRow.Config` is a
`CollectorConfigView`, the store deserializes the column and discards the text, and the
scheduler never sees raw JSON. So the fingerprint serializes the typed view the scheduler
already holds. No raw-JSON member is added to `CollectorRow`, no canonicaliser is written,
and the "MySQL renormalises on upgrade" risk this argument used to motivate does not exist.

**Member order is pinned with `[JsonPropertyOrder]` on EVERY record in the serialized
graph, because the guarantee this rests on is one System.Text.Json does not otherwise
make.** An earlier draft said the order was "fixed
by the record's declaration". It is not: the reflection-based resolver enumerates through
`Type.GetProperties()`, whose order is documented as unspecified, and only
`[JsonPropertyOrder]` guarantees an emission order. That matters more here than the bounded
blast radius suggests, because the fingerprint is PERSISTED in
`collector_scheduler_state.config_fingerprint` and compared across process restarts and
across app upgrades - an order that changed between builds would change the comparison, not
just the text.

A second draft pinned only the five members of `CollectorConfigView` and stopped there. That
was insufficient and is corrected here: `Fields`, `Quiz`, and `Checks` are lists of
`AttestationField`, `QuizItemView`, and `Check`, each emitted by the same reflection-based
resolver, so the nested items' member order was left unpinned - and the nested part is
exactly the part that varies. On every fingerprinted collector the four non-`Checks` outer
members are constant (see the collision note below), so the whole variable input was a
`Checks` array whose items had no pinned order. **So all four records carry explicit
`[JsonPropertyOrder]` values**: `CollectorConfigView` (5 members), `Check` (3),
`AttestationField` (4), and `QuizItemView` (3) - fifteen attributes and no logic.

**The placement cost, stated because it is real and this plan has priced placement costs
elsewhere.** `Check` and `AttestationField` live in `Freeboard.Core`'s GitOps model, which
uses no System.Text.Json today, so pinning them puts a JSON serialization attribute on the
shared domain model for a scheduler's hash. Three things make that the right trade anyway:

- The coupling already exists in kind, and this plan already states it. `ImportPlan`
  serializes both records into the persisted `config` column with default naming, so the
  member NAMES of these two Core records are already load-bearing in a persisted contract
  (D1c: "each nested item keeps the key names it already has"). Pinning the member ORDER
  extends a contract this change already writes down; it does not open a new one. The
  attribute is a BCL attribute, so no project gains a package dependency.
- The alternative - building the hash input explicitly instead of
  serializing the view - trades a declarative attribute for imperative code that has to be
  kept in step with the config model by hand, and its failure mode is worse. A member left
  out of a hand-written builder silently removes an operator's repair from the recovery
  trigger, which is the exact regression D9 exists to prevent; an attribute left off a new
  member only risks an emission-order change, which re-fingerprints and revives dead rows
  once and then self-heals.
- Pinning all fifteen rather than only the three on `Check` (the only nested type an
  integration collector can actually emit today) is what makes the normative wording
  unconditional. Pinning only what varies today would leave the same latent defect this
  correction is about, one registry edit away.

The alternative of rewording the spec to "an order fixed by the implementation"
keeps the code smaller but leaves a persisted comparison
resting on an unspecified enumeration order, which is the wrong saving.

**The concatenation is pinned too, since a hash input with an unstated layout is not a
contract.** The fingerprint is `SHA-256` over the UTF-8 bytes of
`"{type}\n{frequency}\n{provider}\n{connection}\n{configJson}"` - the five parts in that
order, joined by `\n`, extending the existing two-part `"{Type}\n{Frequency}"` form rather
than inventing a second layout. A null `provider` or `connection` renders as the empty
string, which is what interpolating a null already does; neither token nor the single-line
`configJson` can contain a `\n`, so the separator stays unambiguous.

Hashing the redacted view also settles the redaction interaction the raw-text option would
have created. A raw column member would carry the training `answer` into the scheduler and
into anything that later read `CollectorRow`, which would put confidential data one careless
projection away from a read surface for no gain. Nothing is lost by hashing the view: only
`type == "integration"` collectors are fingerprinted, and an integration collector's whole
registered config is `checks`, which the view carries in full.

**The serialization is `JsonSerializer.Serialize(CollectorConfigView)` with default
options, and it deliberately reuses NEITHER of the other two.** There are now three
serializations of one idea in this plan and they follow three different rules, so the
fingerprint's has to be named rather than left as "deterministically":

| Form | Rule | Owner |
| --- | --- | --- |
| Stored | absent members omitted, `PassMark` a number, `Answer` retained | D1c |
| Wire | absent members omitted, snake_case keys, `Answer` removed | D5 |
| Fingerprint | ALL five members emitted, member order pinned by `[JsonPropertyOrder]` on the view AND on every nested item record, `Answer` removed | here |

The fingerprint emits all five members - `"Checks":[...]` alongside `"Body":null` and three
empties - because it is a hash input, not a document: nothing reads it, so omitting an
absent member buys nothing and adding the omission rule would be a third projection to
write and keep in step. Its determinism comes from the pinned member order above, not from
any canonicaliser.

Reusing the WIRE projection instead was considered and rejected. It is a hand-written,
key-omitting projection that lives in the web layer for presentation reasons; binding the
fingerprint to it would mean a future cosmetic change to the response shape silently
re-fingerprints every collector and revives every dead row once. Hashing the view directly
has no such coupling.

**Why the difference cannot collide in V1, which is the only thing the difference could
cost.** A collision would need two DIFFERENT configs to serialize identically. Only
`type: integration` collectors are fingerprinted, and `(integration, fleet)` registers
exactly one key, `checks`, so on every fingerprinted collector the other four members are
always absent and always serialize to the same constant text. The whole variable part of
the hash input is therefore the `Checks` array, whose items are ordered and carry
`SourceKey`, `Name`, and `Severity` in a pinned order - so two configs hash the same only
when their check
lists are equal item for item, which is exactly the intent. It is also why `Check` in
particular MUST carry the order attributes: it is the one nested record a fingerprinted
collector emits today. The reasoning is recorded here
rather than left to the reader because it stops holding the moment a second key is
registered for an integration pair: at that point the "other four are always absent"
premise goes, and this note is the flag that the fingerprint input needs re-checking, not
re-deriving.

### D9a: Ingest behaviour is unchanged

Ingest resolves the collector, requires a non-null vendor, checks the requirement against
the control's `maps_to`, and records `collector_id` plus `frequency` - all unchanged apart
from the type name. A `manual`/`training` collector with a vendor can still ingest, exactly
as a `manual-attestation` collector can today; narrowing that is out of scope.

### D10: Placement and licensing

Everything is MIT. The domain model, loader, validator, config-schema registry, and cadence
helper stay in `Freeboard.Core`; schema, migration, import plan, importer, and stores in
`Freeboard.Persistence`; endpoints, page, scheduler, and ingest in `Freeboard` (web);
commands and API client in `Freeboard.CLI`. Nothing is added to `Freeboard.Enterprise`, and
neither `Freeboard.CLI` nor `Freeboard.Agent` gains a reference to it - the existing
architecture test that pins the one-way EE rule keeps this honest. No new package
dependency.

### D11: `frequency` is required on every collector type (divergence - resolved to Plan A)

Plan A requires `frequency` on all five types and synthesises an `annual` placeholder for
migrated attestations. Plan B would require it only for the machine types (`integration`,
`script`, `agent`) and REJECT it on `manual`/`training`, which deletes the placeholder
problem outright. **Resolved to Plan A: `frequency` stays required on every type** - but on
a rationale neither plan stated, and Plan A's own stated rationale is retracted.

Plan B's premise is that nothing schedules a `manual`/`training` collector, so its cadence
is inert. Plan A agreed with that premise and defended the field on schema-flatness
grounds. The premise is false. `EvidenceIngestEndpoints` stamps the resolved collector's
`frequency` onto every appended evidence run, and `MySqlEvidenceStore` computes staleness
from that stamp via `CollectorFrequency.IsStale`. A collector of ANY type that has a
registered vendor may hold a credential and ingest - the evidence-ingest capability states
this explicitly and this change preserves it (see the Open Questions). So an attestation
collector's `frequency` is the staleness cadence of the evidence it posts. Rejecting the
field would leave attested evidence with a null cadence, permanently un-stale-able: an
annual security-training attestation completed once would read as current forever. That is
a correctness defect, not a tidiness question, and it settles the divergence on grounds
stronger than either plan's.

The cost is the migration placeholder, which is kept and honestly documented (D4): it is
observable on the read surfaces, so the post-migration `gitops sync` is mandatory. Note the
scope of that exposure is narrower than D4 once claimed - a migrated template row has no
vendor and no credential, so it cannot ingest before the sync - but the requirement above
is about authored collectors going forward, not about migrated rows, and is unaffected.

If a later change removes credentials and ingest from `manual`/`training` collectors (Open
Question 2), Plan B's position becomes correct and `frequency` should be revisited with it.
The two questions are coupled and should move together.

### D12: Legacy top-level fields and misplaced `provider` are rejected explicitly (divergence - resolved to Plan B)

Plan B raised as a medium-severity concern that old top-level attestation fields must be
rejected on `Collector`, "otherwise the cutover silently supports two authoring shapes",
and that `provider` must be rejected on a non-integration collector. Plan A's design
satisfied both, but only as an emergent consequence: `body`/`fields`/`pass_mark`/`quiz` are
simply absent from the loader's `Collector` allowed-key set, so they fall out of the
generic unknown-field path. **Resolved to Plan B on the point that matters:** an invariant
that holds by omission is one a future edit can delete without noticing. These are lifted
into stated requirements with their own scenarios in the `gitops-config-format` delta:

- Top-level `body`, `fields`, `pass_mark`, or `quiz` on a `Collector` is an unknown field
  and is rejected. This is the shape a half-migrated `AttestationTemplate` produces, so it
  is the exact mistake an operator will make.
- Top-level `checks` on a `Collector` is now an unknown field and is rejected. This is new
  with D1a and is the shape a half-migrated `EvidenceCollector` produces.
- `provider` or `connection` on a collector whose `type` is not `integration` is rejected.
- `checks` INSIDE `config` on a collector whose `type` is not `integration` is rejected by
  the registry, because no non-integration pair registers the key. This is a different
  code path from the two above and gets its own scenario.

## Risks / Trade-offs

- **The merge migration is not replay-safe** (a crash between a committed DDL statement and
  the recorded `schema_migrations` row leaves a half-applied file) -> Mitigation: matches
  the established `015`/`018`/`019`/`020` convention; the file header documents
  restore-and-rerun, which is always available pre-production. Statement by statement, the
  file order is the step-0 `ADD CHECK`/`DROP CHECK` pair, `CREATE TABLE IF NOT EXISTS`, the
  two `INSERT ... SELECT` copies, the credential re-point, then the two `DROP TABLE`s. The
  step-0 pair re-runs cleanly - it leaves no constraint behind whether the `ADD` succeeds or
  fails, so a re-run cannot collide on the name - and so do the `DROP TABLE`s, on any replay
  that did not already reach them; `CREATE TABLE IF NOT EXISTS` is a no-op on a re-run. The
  two `INSERT ... SELECT` copies are the non-idempotent statements - a replay hits the
  duplicate primary key - which is why recovery is restore-and-rerun, or `DROP TABLE
  collectors` then re-run after either of the two aborts that reach a copy step (a
  provider-guard refusal, or an id shared between the two source tables). That drop works
  only because both aborts happen BEFORE the credential re-point; once `collector_credentials`
  references `collectors` the drop fails on the live foreign key and restore-and-rerun is the
  only recovery.
- **An id shared between the two source tables aborts `021`, and it is reachable from a
  VALID pre-merge config.** This is not the same shape as the four refusals and must not be
  read as one. Duplicate-id detection runs PER KIND - `ValidateEvidenceCollectors` and
  `ValidateAttestationTemplates` each open their own `seenIds` set and there is no set
  spanning both - and each legacy table has its own primary key, so
  `kind: EvidenceCollector, id: x` alongside `kind: AttestationTemplate, id: x` validates,
  syncs, and persists today. `021` then aborts on step 2, the `attestation_templates` copy,
  because both ids land in one `collectors` primary key. Three consequences follow that the
  four refusals do not have. It does NOT name itself: there is no named constraint, so the
  operator gets `ERROR 1062 Duplicate entry '<id>' for key 'collectors.PRIMARY'` - a key
  name, not a rule name. It leaves a partially created `collectors` table holding the step-1
  rows, so the provider guard is not the only way `021` can abort with `collectors`
  populated. And it is the one abort that refuses a database that was VALID rather than one
  already broken: clause 3 of the general rule would normally COPY a valid-source row the
  merged contract rejects, and it cannot here, because the merged contract's rejection IS the
  primary key -> Mitigation: the repair is the same shape as the others and is stated rather
  than left to be derived - rename one of the two ids in the AUTHORED config, run
  `freeboard gitops sync` against the pre-merge schema so the rename lands in the legacy
  tables, `DROP TABLE collectors`, then re-run `021`. That drop is valid only BEFORE the
  credential re-point (step 3): the collision fires on step 2, so at that moment no live
  foreign key points at `collectors` and the drop succeeds. It is not idempotent and it is
  not a general recovery - after the re-point, `collector_credentials` references
  `collectors`, and dropping it fails on the live constraint; recovery from that point on is
  the restore-and-rerun the header documents. Pre-production, so there is no id contract to
  honour, and folding a collector-plus-template PAIR into one document resolves the usual
  case during the hand-migration anyway (Migration Plan step 4).
- **Migrated attestations get a synthesised `annual` frequency, and it is NOT inert** - it
  is displayed by the API, the register page, and the CLI listing as a cadence no
  authored document backs (not by the SoA drill-down, which projects a null cadence on an
  attestation-tagged check per D7). It is not reachable through ingest staleness at migration time:
  only rows copied from `attestation_templates` get the placeholder, and those rows have no
  vendor and can hold no credential, so they cannot ingest (D4) -> Mitigation: `gitops sync`
  is a mandatory migration step, not a follow-up, and overwrites every placeholder from the
  authored config; `annual` is the longest window, so the pre-sync display is a permissive
  cadence rather than a false staleness alarm. Documented in the migration header and the
  Migration Plan.
- **Four classes of already-invalid source row now FAIL the migration** rather than
  migrating into a state the validator forbids or the typed read cannot bind (D4): a
  non-`integration` row carrying a `checks` list or a `connection_id`; a
  `type: integration` row whose `checks` is not a non-empty JSON array of objects with
  well-typed known members (SQL NULL -
  the pre-`018` shape - an empty array, a JSON null, a non-array value, an array carrying a
  non-object item, or an array carrying an object whose `SourceKey`/`Name`/`Severity` is
  present with the wrong JSON type or as a JSON null); an `attestation_templates` row whose
  `fields` or `quiz` is not a JSON array of objects with well-typed known members; and a
  `type: integration` row that carries its `checks` but has a NULL `connection_id`, so no
  `provider` can be derived. The cost is a migration an
  operator can be blocked on -> Mitigation: each guard is a named check constraint, so the
  failure names itself; the two step-0 guards cover the first three and run before anything is
  created, and the provider guard covers the fourth on the first copy step, so in every case
  both source tables are still
  intact and nothing is lost; the repair (`gitops sync` against the pre-merge schema, or
  deleting the stale row) needs only the pre-merge app; and the header states all four. The
  migration tests pin every refusal, so the behaviour is decided here rather than discovered
  in the field. No such row is reachable from a valid authored config - today's validator
  rejects every one of these shapes and `SerializeList` never writes an empty array - so the
  realistic blast radius is a hand-edited or pre-`018` database.
- **Accepted parity difference 1 of 3: registry parity is not bit-for-bit on an empty or
  blank authored value.** Today a
  `manual` template authoring `quiz: []` or a blank `pass_mark` validates, because the
  pre-merge rules test the VALUE (`Quiz.Count > 0`, `IsNullOrWhiteSpace(PassMark)`) and an
  empty one reads as absent. Under the registry those keys are unregistered for
  `(manual, -)`, so `config: { quiz: [] }` is rejected as an unknown key -> Mitigation: this
  is accepted, not worked around. It is the change's own rule - an unregistered key is
  rejected whatever its value - and rejecting a key that could never have had an effect is
  the better diagnostic. The required-key side keeps parity exactly, because requiredness is
  evaluated on the PARSED member rather than on key presence: an empty list and a blank
  scalar both count as ABSENT, so `training` still fails on `quiz: []`, on `pass_mark: ""`,
  and on a null-valued `pass_mark`, as it does today (D2). Scenarios pin both halves and the
  blank scalar.
- **Accepted parity difference 2 of 3: the control `evaluation` requirement now reaches a
  control whose only proving mechanism is an attestation.** Today
  `ValidateEvidenceCollectors` is the only place that populates the attached-control set, and
  `ValidateAttestationTemplates` never adds to it, so a `Control` carrying an
  `AttestationTemplate` and no `EvidenceCollector` validates with NO `evaluation` rule. That
  shape is reachable: the validator requires neither side of the collector/template pair, so
  a control may legally carry a template alone (D4a). After the merge a former template IS a
  collector, and the ADDED requirement makes `evaluation` required on a control with at least
  one attached collector of any type, so such a control now fails validation -> Mitigation:
  accepted, and recorded here rather than left emergent. The pre-merge asymmetry is itself
  the anomaly: a `manual-attestation` `EvidenceCollector` already forced its control to
  declare an `evaluation` while the `AttestationTemplate` carrying that same attestation's
  form did not, so one of two documents describing one proving mechanism triggered the rule
  and the other did not. The merge removes the inconsistency rather than creating one, and
  the rule it lands on is the meaningful half: a control with something proving it needs to
  say how those proofs combine. The cost is a hand-migration step - `gitops sync` is a
  REQUIRED migration step and will refuse such a config until an `evaluation` is added - so
  it is listed in Migration Plan step 4 and in the `docs/gitops.md` hand-migration guidance,
  and a Core test pins the new verdict. Scoping the rule to exclude `manual`/`training`
  collectors was the alternative and is rejected: it would preserve the anomaly under a new
  name, and it would mean a control proved only by attestations has no stated evaluation
  semantics at all.
- **Accepted parity difference 3 of 3: ids collapse into one space, so a collector and a
  template sharing an id stop validating.** Duplicate-id detection runs per kind today, and
  each legacy table has its own primary key, so an `EvidenceCollector` and an
  `AttestationTemplate` may both be authored as `id: x`. After the merge there is one kind
  and one id space, so that pair is a duplicate-id error -> Mitigation: accepted, and counted
  here rather than left to be inferred from the migration plan. It meets the same test as the
  other two - a document that validates today fails after the merge - so it is listed as a
  parity difference and not under some other name; the earlier draft recorded only its
  database-level consequence (the `021` abort on `collectors.PRIMARY`) and its hand-migration
  step, which described the same fact twice without ever counting it. The usual case is
  benign: a collector-and-template PAIR sharing an id folds into one document, which is the
  hand-migration the merge asks for anyway. Only a collector and a template that share an id
  WITHOUT being a pair need a rename, and that shape is an authoring accident rather than a
  pattern the pre-merge model encouraged. Preserving the per-kind id space was never an
  option: one kind with two id spaces is not a coherent model, and the `collectors` primary
  key would have to allow it.
- **A migrated attestation pair lands as two collectors on one control, and a migrated
  `training-attestation` row lands in a state the merged validator would reject** (no
  `pass_mark`, no `quiz`), because `021` copies the two source tables independently and
  nothing in the source schema links a template to its collector (D4a) -> Mitigation:
  accepted, not merged in SQL and not refused. Merging by shared `control_id` would invent a
  pairing the data never expressed and would pick an arbitrary surviving id; refusing would
  block a HEALTHY database, since both source rows are valid pre-merge and the missing form
  has no pre-merge home on an `EvidenceCollector`. The split row reads cleanly, is never
  scheduled, and is collapsed by the mandatory `gitops sync`, which prunes whichever id the
  hand-migrated document does not keep. The observable gap - a doubled entry on the register,
  the API, and the drill-down, and a training entry with no quiz - is documented in the
  migration header and the Migration Plan, and the migration test pins it.
- **`checks` becomes readable on the API, CLI, and register page** where it was previously
  persisted-and-unexposed -> Mitigation: it is non-secret, git-authored data and it is what
  the register exists to show; only the quiz `answer` is confidential and it stays redacted
  at the store boundary. The alternative would be code written specifically to hide it.
- **A wider schedule fingerprint means more edits revive a dead/error row** -> Mitigation:
  that is the intent (D9); ensure revives only dead/error rows, so a healthy collector's
  `next_due_at` is never reset by a config edit.
- **Diagnostic wording changes for `manual` declaring `pass_mark`/`quiz`** (now an
  unknown-config-key message rather than the bespoke "only valid for a training template")
  -> Mitigation: the acceptance criterion is that malformed forms are REJECTED as today,
  which holds. The Core tests assert rejection and the offending key name, not the exact
  sentence.
- **Empty V1 schemas for `script` and `agent` make those registry rows look like
  scaffolding** -> Mitigation: the registry is load-bearing for `manual`, `training`, and
  (after D1a) `integration/fleet`, and an empty row is what makes "no config key is
  accepted here" enforceable. The alternative - leaving those two free-form - is the hole
  the change exists to close.
- **Four breaking public surfaces land at once** (kind, table, endpoints, routes) ->
  Mitigation: pre-release software with a stated hard-cutover policy; every in-repo author
  of the old shapes is migrated in the same change; the task groups order Core -> schema ->
  persistence -> web -> CLI -> docs so the sequence is reviewable one layer at a time.
- **The model groups do not build in isolation.** Renaming `EvidenceCollectorFrequency` and
  replacing the `GitOpsConfig` collections breaks `Freeboard.Persistence`, `Freeboard`, and
  `Freeboard.CLI` at the moment the Core group lands, and those projects are repaired in the
  later groups -> Mitigation: groups 1-5 are a stacked sequence, not five independently
  green commits; `dotnet build` and `dotnet test` are required green at the tip of group 5,
  and the verification group enforces it. Each group stays a separate Conventional Commit
  so review and release notes still read one layer at a time. Splitting the rename across
  projects to make each commit compile would mean a temporary duplicate cadence helper -
  two systems doing one job - which costs more than it buys for a change that lands as one
  unit anyway.
- **`config` as one JSON column loses column-level queryability of `pass_mark`** ->
  Mitigation: nothing queries it; MySQL JSON paths cover a future need.
- **Renaming `EvidenceCollectorFrequency` touches the scheduler spec** -> Mitigation: a
  small, explicit `collector-scheduler` delta; the behaviour (tokens, windows, grace,
  interval) is byte-identical.

## Migration Plan

0. Before migrating, dump the two source tables (`mysqldump` of `evidence_collectors` and
   `attestation_templates`, or a `SELECT ... INTO OUTFILE`). Nothing is truly lost without
   it - the authored YAML is the source of truth and the migration never touches it - but
   `021` drops `evidence_collectors` along with its free-form `config` column, which is the
   one thing in the merged model that has no authored home, and an operator part-way
   through the hand-migration will want to read what was in it. The migration header says
   so.
1. `freeboard system migrate` applies `021_collector_merge.sql`, which creates `collectors`,
   copies both source tables, re-points the credential foreign key, and drops both legacy
   tables. Nothing else needs re-pointing: `collector_scheduler_state.collector_id` and
   `evidence_runs.collector_id` are scalar with no foreign key. The migration REFUSES four
   classes of already-invalid row (D4), all repairable with a `gitops sync` against the
   pre-merge schema or by deleting the row:
   - a non-`integration` row carrying a `checks` list or a `connection_id`, failing on the
     named `ck_evidence_collectors_premerge_payload` guard before anything is created, so
     nothing needs cleaning up before the re-run;
   - an `attestation_templates` row whose `fields` or `quiz` is not a JSON array of objects
     with well-typed known members - a JSON null, a non-array value, an array carrying a
     non-object item, or an object item carrying a wrong-typed known member - failing on the
     named `ck_attestation_templates_premerge_payload` guard, likewise before anything is
     created;
   - a `type: integration` row whose `checks` is not a non-empty JSON array of objects with
     well-typed known members - SQL
     NULL (the
     pre-`018` shape, in which `connection_id` is NULL too), an empty array, a JSON null, a
     non-array value, an array carrying a non-object item such as `[1]` or `[null]`, or an
     array carrying an object whose `SourceKey`, `Name`, or `Severity` is present with a JSON
     type other than string -
     failing on the same named guard, likewise before anything is
     created and with nothing to clean up;
   - a `type: integration` row that carries its `checks` but whose `connection_id` is NULL,
     failing on the named
     `ck_collectors_integration_provider` constraint before either source table is dropped.
     Of the four refusals this is the only one that has created anything: drop the partially
     created `collectors` table and re-run after the repair.

   One further abort is possible and is NOT one of the four refusals, because it is
   reachable from a config that is entirely valid pre-merge: an `EvidenceCollector` and an
   `AttestationTemplate` may share an id today, since duplicate-id detection runs per kind
   and each legacy table has its own primary key. `021` aborts on the
   `attestation_templates` copy with an UNNAMED `ERROR 1062 Duplicate entry '<id>' for key
   'collectors.PRIMARY'` - a key name rather than a rule name - with both legacy tables
   intact and a partially created `collectors` table holding the first copy's rows. Repair:
   rename one of the two ids in the authored config, `freeboard gitops sync` against the
   pre-merge schema, `DROP TABLE collectors`, re-run. The drop is valid only here, before the
   step-3 credential re-point; it is not idempotent, and after the re-point it would fail on
   the live foreign key.
2. `freeboard gitops sync` re-authors every collector row from the hand-migrated config.
   This step is REQUIRED, not optional. Until it runs the merged table holds three states
   that no authored document backs: the placeholder `frequency` on every migrated
   attestation, which the API, the register page, and the CLI listing display (the SoA
   drill-down does not - it projects a null cadence on an attestation-tagged check);
   nothing at all where an operator had authored free-form `config` keys, because the
   migration no longer carries that column across; and TWO collector rows for every
   attestation that was authored as a collector-plus-template pair, the form-carrying half
   of which has the placeholder cadence and the cadence-carrying half of which has no form -
   so a migrated `training` collector shows no quiz until the sync (D4a). The sync resolves
   all three from the authored config in one pass, pruning the surplus row of each pair.
3. Rollback: restore-and-rerun. The migration is forward-only; there is no down script,
   consistent with every migration in the repo.
4. Operators with a pre-existing config repository must hand-migrate their documents:
   `kind: EvidenceCollector` and `kind: AttestationTemplate` both become `kind: Collector`;
   `manual-attestation`/`training-attestation` become `manual`/`training`; a template's
   `body`/`fields`/`pass_mark`/`quiz` move under `config`; an integration collector's
   `checks` moves under `config`; a template gains a `frequency`; an integration collector
   gains a `provider` matching its connection's. An attestation authored as a PAIR - a
   `manual-attestation`/`training-attestation` `EvidenceCollector` and an
   `AttestationTemplate` on the same control - becomes ONE `Collector` document; keep the
   collector's id where a machine credential hangs off it, because the sync prunes the other
   row and `collector_credentials` cascades. Two further edits are needed and are easy to
   miss because neither is a field move:
   - **A `Control` whose only proving mechanism was an `AttestationTemplate` must gain an
     `evaluation` rule.** Such a control validates today with none, because only the
     evidence-collector phase populated the attached-control set; the former template is now
     a collector, so the required-evaluation rule reaches it. `gitops sync` is a REQUIRED
     migration step and refuses the config until the rule is added. Pick `all`, `any`, or
     `manual` on the same reading as any other control: how its proofs combine.
   - **Ids are now unique across the merged kind, not per legacy kind.** An
     `EvidenceCollector` and an `AttestationTemplate` may share an id today, because
     duplicate-id detection runs per kind. Folding a pair into one document resolves the
     usual case; a collector and a template that share an id but are NOT a pair need one of
     them renamed.

   Both legacy kinds become unknown-kind
   diagnostics, and every moved field left at top level becomes an unknown-field diagnostic
   (D12), so a missed or half-migrated document fails `gitops validate` loudly rather than
   being silently dropped or silently accepted in two shapes.
5. An operator who authored keys in the old free-form `config` map must hand-migrate those
   too, and there is no escape hatch: the merged `config` is closed by the registered
   `(type, provider)` schema, so a key with no registered home has to be dropped from the
   document or the schema has to gain it. The migration deliberately does not carry the old
   map into the database as a temporary parking space (D4). Since no runner consumes any
   V1 config key beyond `checks` and the attestation form, an old key almost certainly has
   no home yet, and `gitops validate` names it rather than leaving it to rot in a column.

## Verification Strategy

- **Core unit tests** (`tests/Freeboard.Core.Tests`): fold
  `EvidenceCollectorValidationTests` and `AttestationTemplateValidationTests` into one
  `CollectorValidationTests` covering every rule - required fields, the five type tokens,
  the frequency tokens, threshold range, control/vendor/connection resolution, the
  integration-only top-level `provider`/`connection` rules and the required `config`
  `checks`, the provider/connection
  mismatch, check shape and uniqueness, duplicate id, the control-evaluation rule, and the
  full `manual`/`training` value matrix carried over from the template tests. Add a
  `CollectorConfigSchemaTests` covering: an unknown config key per `(type, provider)`; a
  missing required key (including `checks` absent or empty on `integration/fleet`); a key
  valid for one type rejected on another (`pass_mark` on `manual`, `checks` on `manual`);
  any key rejected on `script` and `agent`; no cascading config diagnostics when the
  type or provider is itself unknown; and a completeness assertion that
  `CollectorConfigSchema.For` returns non-null for every pair the token sets can produce -
  `(integration, p)` for each `p` in `IntegrationProvider.Tokens` and `(t, absent)` for
  each non-integration type token - so a provider added to the shared token set without a
  registry row fails the build instead of silently reopening free-form `config` for that
  provider (D2). Cover the `manual`/`training` parity explicitly, since
  it is the acceptance criterion: a `manual` collector that omits `config` entirely
  validates, one that authors `body` without `fields` validates, and a `training` collector
  that authors `fields` alongside its `pass_mark` and `quiz` validates with the form-field
  rules applied. Pin both halves of the empty-value rule, which is where parity is
  deliberately not bit-for-bit: `quiz: []` on a `manual` collector is REJECTED as an
  unregistered key (the pre-merge value test ignored it), while `quiz: []` on a `training`
  collector still fails as a missing required key, because an empty list counts as absent.
  Pin the blank scalar with it: `pass_mark: ""` on a `training` collector fails as a missing
  required key, not as a range error and not silently, because requiredness is evaluated on
  the parsed member and the pre-merge blank test rejected it too.
  Pin the other accepted parity difference too, in `CollectorValidationTests`: a `Control`
  whose only attached collector is a `manual` or a `training` one and which declares no
  `evaluation` is REJECTED, naming the control and the missing rule. That shape validates
  pre-merge as a template-only control, so the test is what keeps the widening a decision
  rather than an accident.
  Add the D12 cases: a top-level `body`, `fields`,
  `pass_mark`, `quiz`, or `checks` on a `Collector` rejected as an unknown field, and
  `provider`/`connection` rejected on a non-integration collector. Update
  `ConfigLoaderTests` for the merged kind, the retired kinds' unknown-kind diagnostic, the
  valid-kinds enumeration no longer naming either retired kind, and
  the null-collection normalization of the `config` node itself and of the collections
  inside it (`fields`, `quiz`, `options`, and now `checks`). Add the wrong-shape cases,
  which the loader must turn into diagnostics
  rather than exceptions: `config` authored as a scalar and as a sequence (with no
  per-key cascade), and `config.fields`, `config.quiz`, and `config.checks` each authored
  as a scalar - the merged-kind successors of the existing non-list `fields`/`quiz` tests.
  Add the explicit-null `config:` case with them, which is the one that is NOT a
  diagnostic: it binds cleanly, so nothing the loader catches fires, and the collector must
  load with an empty config exactly as an omitted `config` does.
  Update `IntegrationConnectionValidationTests` onto the merged kind.
- **Persistence unit tests**: `ImportPlanTests` for the merged row plan - config
  serialization (null when every member is absent, absent members omitted, the stored keys
  being the C# member names, and `PassMark` serialized as a JSON number rather than the
  authored string, per D1c), threshold and pass-mark parsing,
  null-if-blank on `vendor`/`connection`/`provider`, and the single id list. Add a
  round-trip assertion that what `ImportPlan` writes is what `DeserializeConfig` reads back,
  since those two are the pair the storage contract binds together.
- **MySQL integration tests** (`FREEBOARD_TEST_DB`-gated, skipping cleanly when unset):
  the merged schema exists with its three foreign keys; round-trip of an integration, a
  script, a manual, and a training collector; the answer redaction on read (stored JSON
  has the answer, the read model does not); counts report one collector number; FK-safe
  prune order when a targeted control, a named vendor asset, or a referenced connection is
  removed; the credential cascade still fires from `collectors`; and a migration test that
  seeds pre-`021` rows in both legacy tables, applies `021`, and asserts the merged rows,
  the re-tokenized types, the derived `provider`, the composed `config` (the old `checks`
  column folded under a `Checks` key, the old template columns folded under their keys),
  the re-pointed credential foreign key surviving the legacy-table drop, and that both
  legacy tables are gone. The migration test must seed the awkward source rows, not only
  the happy ones: a row with a NULL `config` and a non-NULL `checks` (the common integration
  shape, which must land with its checks intact); a row with both NULL (which must land with
  `config = NULL`, not `{"Checks": null}`); a row whose old free-form `config` held keys,
  which must land with those keys ABSENT; a template row with a `pass_mark` (which must land
  as a JSON NUMBER the read store binds to `int?`); and a template row with every optional
  column NULL, which must land with `config = NULL` rather than `{}` or a JSON-null member.
  Further migration tests assert the REFUSALS, one per class. Seeding a `type: integration`
  row that carries a non-empty `checks` array but has a NULL `connection_id` - the `checks`
  must be seeded, or the row trips the step-0 guard first and the test asserts the wrong
  constraint - and applying `021` fails on `ck_collectors_integration_provider`,
  and both legacy tables still exist afterwards because the guard fires before the drops.
  Seeding a `type: manual-attestation` row with a non-NULL `checks` column, and a
  second with a non-NULL `connection_id`, each fails on
  `ck_evidence_collectors_premerge_payload` with `collectors` not created at all, because
  that guard runs before the `CREATE TABLE`. The integration branch of that guard gets its
  own cases, each stating its verdict: a `type: integration` row whose `checks` is SQL NULL
  (the pre-`018` shape, `connection_id` NULL too), one whose `checks` is `[]`, one whose
  `checks` is a JSON null, one whose `checks` is a JSON object rather than an array, one
  whose `checks` is `[1]` (a scalar item), one whose `checks` is `[null]` (a JSON null
  item), and one whose `checks` is
  `[{"SourceKey": 123, "Name": "MFA", "Severity": "Hard"}]` (an object item with a
  wrong-typed KNOWN member) all
  FAIL on `ck_evidence_collectors_premerge_payload` before `collectors` is created; while a
  `type: integration` row whose `checks` is `[{"nope": 1}]` - a non-empty array of OBJECT
  items whose known members are simply ABSENT - COPIES,
  because the guard types the three known members and does not require them. The `[1]`,
  `[null]`, and `[{"SourceKey": 123, ...}]` cases are
  the ones that make the read-boundary premise true: `[1]` does not bind to a `Check` at all,
  `[null]` binds to a NULL list element, and a present member of an incompatible type is a
  `JsonException`, whereas `[{"nope": 1}]` binds to a `Check` with
  empty members because an UNMATCHED property is ignored. Pin the happy case with them - a
  `manual-attestation` row with NULL `checks` and NULL `connection_id`, and an
  `integration` row with a non-empty `checks` array and a `connection_id`, both pass the
  guard - so the guard is shown to refuse
  only what it is meant to. Pin the replay property too: after a refused `ADD`,
  `evidence_collectors` carries no leftover constraint, so the file re-runs after the repair.
  One more pins the accepted SPLIT (D4a): seed a `training-attestation`
  `evidence_collectors` row and an `attestation_templates` row on the SAME control, apply
  `021`, and assert the migration SUCCEEDS and leaves two `type: training` rows on that
  control - one carrying the authored cadence with `config` SQL NULL, one carrying the form
  with the placeholder cadence - so the deliberate transient is decided here rather than
  read as a defect. Assert too that the form-less row reads back through the store without
  error, which is the condition that makes copying it acceptable at all.
  One more pins the ID COLLISION, which is an abort rather than a refusal and needs its own
  test because it is reachable from a valid config: seed an `evidence_collectors` row and an
  `attestation_templates` row that SHARE an id, apply `021`, and assert it fails, that the
  migration version is not recorded, and that both legacy tables and their rows are intact.
  `IntegrationConnectionIntegrationTests`, `CollectorCredentialIntegrationTests`, and
  `AssetUnificationIntegrationTests` move onto the merged table and kind.
- **Web tests**: one `CollectorsPageTests` replacing the two page test files (render,
  empty state, anonymous redirect, read-only mode, store-unreachable notice, zero-grant
  visibility, HTML-encoded body, no answer in the markup, an integration collector's checks
  rendered); `ComplianceEndpointTests` for
  `GET /collectors` (including the omit-absent `config` projection: a `script` collector
  serializes `"config": {}`, a `training` collector carries no `checks` key, and an
  `integration` collector carries no attestation keys) and the changed counts shape;
  `CollectorCredentialEndpointTests` on the
  renamed route; `CollectorSchedulerServiceTests` with the new type tokens AND the widened
  fingerprint (a `config`, `provider`, or `connection` edit revives a dead/error row; a
  `threshold` edit does not; a healthy row's `next_due_at` is untouched either way), plus
  the case that motivates the whole widening: changing only a check's `SourceKey` inside
  `config` changes the fingerprint, which is the operator repair D9 exists to make
  effective;
  `EvidenceIngestEndpointTests` unchanged in behaviour; `StatementOfApplicabilityTests` for
  the derived check tag AND the null cadence on an attestation-tagged check (a `manual` or
  `training` collector with a `frequency` projects no cadence, while an `integration`
  collector projects its own); `IntegrationConnectionsTests` onto the merged kind;
  `ShellNavCatalogTests`,
  `ShellRouteReachabilityTests`, and `RouteAuthzMetadataTests` for the single nav entry and
  route.
- **CLI tests**: one `CollectorCommandTests` covering the merged listing (including a
  training collector's quiz prompts, the has-body / no-body indicator carried over from the
  retired command, and the absence of any answer), the removed
  `attestation-template` group, and the exit-code matrix; `GitOpsCommandTests` and
  `SyncMySqlIntegrationTests` on the merged kind, including a representative
  provider-mismatch and unknown-config-key rejection at the command surface.
- **E2E** (`FREEBOARD_TEST_E2E`-gated): `AccessibilityAuditE2ETests`, `DrawerE2ETests`, and
  `StatementOfApplicabilityE2ETests` updated for the single route; the preserved markers
  (`soa-nodes`, `data-node-id`, `badge`, ...) stay intact.
- **Build gate**: `dotnet build` then `dotnet test` at the repo root, plus
  `npx markdownlint-cli2 "**/*.md"` for the `docs/gitops.md` rewrite.

## Open Questions

Each carries the position this plan takes, so a reviewer can challenge the position rather
than re-derive the question.

1. **Should `(integration, fleet)` register a `team` key now?** Fleet scopes policies by
   team, so #52 may well want one. **Position: no, wait for #52 to ask.** After D1a the
   pair already has a required key (`checks`), so the schema is not empty and the provider
   axis is exercised without it; a second key with no consumer would be scaffolding, and
   adding one later is a member plus a registry row. The reviewer may prefer to land it now
   so #52 needs no format change - the counter-argument is that a format change is cheap
   pre-production and guessing the key's shape is not.
2. **May a `manual`/`training` collector hold a credential and ingest evidence?** Today it
   can, as `manual-attestation`, and this change preserves that unchanged. **Position:
   preserve it; narrowing is out of scope.** Note this question is COUPLED to question 3:
   the reason `frequency` must stay required on attestation types (D11) is precisely that
   they can ingest, and their cadence is what makes the resulting evidence stale. If a
   reviewer decides attestations should instead arrive through a dedicated response path,
   both this and the `frequency` requirement should be revisited in that change, together.
3. **Should `frequency` be required on `manual`/`training`, which nothing schedules?** This
   was a real divergence between the two plans (D11). **Position: required on every type.**
   Not on schema-flatness grounds - on the concrete ground that ingest stamps the
   collector's cadence onto every evidence run and staleness reads that stamp, so an
   attestation collector with no cadence produces evidence that can never go stale. The
   reviewer's lever here is question 2, not this question directly.
4. **Should the merged register page show a training quiz's prompts and options to any
   authenticated user?** It does today on the attestation-template register, and this
   change carries that forward onto the shared page. **Position: keep it; only the `answer`
   is redacted.** Worth confirming, because the merge widens the audience by putting quiz
   content on the page every reader already visits for collectors: a reader who could
   previously ignore the attestation register now sees quiz prompts alongside collectors.
   The answer stays redacted at the store boundary either way, so the exposure is the
   question wording, not the gradeable content. If prompts should be gated, that is a
   permission decision for the register as a whole, not a redaction decision.
5. **Does exposing `checks` on the read surfaces need a second look?** New with D1a and D5:
   `checks` was previously persisted and never read back. **Position: expose it** - it is
   non-secret git-authored data and the register exists to show what proves a control.
   Flagged because it is the one place this change widens a read surface rather than
   merging two.
