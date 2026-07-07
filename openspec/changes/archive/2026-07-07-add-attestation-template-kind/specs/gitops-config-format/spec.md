## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the requirements published
by each standard, the organisations being assessed, the scopes that map an
organisation to a standard, the requirement-scopes that map an organisation to a
requirement, the vendors (software and platforms) in use, the vendor-scopes
that record whether a requirement or control applies to a vendor, the
evidence-collectors that attach a data source to a control, and the
attestation-templates that describe an attestation form for a control. The format
SHALL be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Organisation`, `Scope`,
`RequirementScope`, `Vendor`, `VendorScope`, `EvidenceCollector`, and
`AttestationTemplate`. Documents of different kinds MAY appear in any file. Every
resource SHALL have an immutable `id` that is its identity and a mutable `title`
for display. A `Standard` has an `id`, a `title`, required `version` and
`authority`, and optional `publisher` and `source_url` metadata. A `Control` has an
`id`, a `title`, a `maps_to` field that is a non-empty list of `Requirement` ids
(the requirements the control satisfies), and an optional `evaluation` rule (`all`,
`any`, or `manual`) that says how the control's attached evidence-collectors roll up
into a status. A `Requirement` has an `id`, a `title`, a `standard` (a single
`Standard` id it belongs to), a `theme` (a free-form label), a `statement` (the
normative text), an optional `guidance`, a `citation_label`, and a `citation_url`
(an absolute `http`/`https` link to the published source). An `Organisation` has an
`id`, a `title`, a `type` (`Company` or `Department`), and an optional `parent` that
is an `Organisation` id. The Organisation's Company/Department value is authored
under the YAML key `type` so it does not collide with the document discriminator
`kind`; it persists and reads back as the organisation's `kind`. A `Scope` has an
`id`, a `title`, an `organisation` (an `Organisation` id), a `standard` (a `Standard`
id), and a `disposition` (`In` or `Out`). A `RequirementScope` has an `id`, a
`title`, an `organisation` (an `Organisation` id), a `requirement` (a `Requirement`
id), and a `disposition` (`In` or `Out`); it has no `standard` field. A `Vendor` has
an `id` and a `title`. A `VendorScope` has an `id`, a `title`, a `vendor` (a `Vendor`
id), exactly one of `requirement` (a `Requirement` id) or `control` (a `Control`
id), a `disposition` (`In` or `Out`), and a `justification` (required when the
disposition is `Out`). An `EvidenceCollector` has an `id`, a `title`, a `control` (a
`Control` id it attaches to), an optional `vendor` (a `Vendor` id), a `type` (exactly
one of `integration`, `script`, `manual-attestation`, `training-attestation`, or
`agent`), a `frequency` (a collection cadence: `continuous`, `daily`, `weekly`,
`monthly`, `quarterly`, or `annual`), an optional `threshold` (an integer percent
from 0 to 100), and an optional `config` (a free-form map of type-specific
settings). An `AttestationTemplate` has an `id`, a `title`, a `control` (the
`Control` id the attestation form attaches to), a `type` (exactly one of `manual` or
`training`), an optional markdown `body`, an optional list of `fields` (each a form
field with an `id`, a `label`, and a `type` that is exactly one of `boolean`,
`single-choice`, or `short-text`, plus `options` - a list of at least two choice
labels, unique within the field - for a `single-choice` field), an optional `quiz` (a
list of items, each with an `id`, a `prompt`, `options` - a list of at least two answer
labels, unique within the item - and an `answer` that is exactly one of those options),
and an optional `pass_mark` (an integer percent from 0 to 100); a `training` template
requires a `pass_mark` and at least one `quiz` item, and a `manual` template declares
neither. The quiz `answer` is the correct choice; it is git-tracked authoring data but
is redacted from every read surface (it is never returned by the read API, CLI, or web
register), so a training quiz's answer key is not exposed to readers. Property binding is snake_case for
domain/property fields, so `maps_to`, `source_url`, and `pass_mark` bind to their
model properties. The document discriminator key `kind` and the version key
`apiVersion` are camelCase to match the Kubernetes-style convention; they are not
snake_case-bound, so `apiVersion` is the valid key (not `api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Organisation`, `Scope`, `RequirementScope`, `Vendor`,
  `VendorScope`, `EvidenceCollector`, and `AttestationTemplate`
- **THEN** the loader returns a typed config model containing all standards
  (with any metadata), controls (with any `evaluation` rule), requirements,
  organisations, scopes, requirement-scopes, vendors, vendor-scopes,
  evidence-collectors, and attestation-templates with their `id`, `title`, and
  reference fields populated and no errors

#### Scenario: Multiple documents in one file

- **WHEN** a single YAML file contains multiple documents separated by `---`
- **THEN** every document is parsed and included in the config model

### Requirement: Loader and validator never throw or print

The loader and validator in `Freeboard.Core` SHALL return diagnostics as data
and SHALL NOT throw exceptions for malformed or invalid input, and SHALL NOT
write to any output stream. Callers decide how to present results and set exit
codes.

#### Scenario: Malformed input returns diagnostics

- **WHEN** a config file contains malformed YAML that the parser cannot read
- **THEN** the loader catches the parse error, returns a result with a diagnostic
  naming the file (and line/column where available) rather than throwing, and
  writes nothing to output

#### Scenario: Unknown or missing kind reported by the loader

- **WHEN** a document has a `kind` that is missing or not one of `Standard`,
  `Control`, `Requirement`, `Organisation`, `Scope`, `RequirementScope`, `Vendor`,
  `VendorScope`, `EvidenceCollector`, or `AttestationTemplate`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds credential material (a token,
key, password, or equivalent). Credentials needed by future integrations SHALL be
referenced by a named credential resolved out-of-band, never inlined in git-tracked
config. The `EvidenceCollector.config` map is git-tracked type-specific settings and
SHALL NOT inline credential material; a collector that needs a credential names it
for out-of-band resolution. The `AttestationTemplate` `body`, `fields`, and `quiz`
are git-tracked form and quiz content and SHALL NOT inline credential material. A
training quiz's correct `answer` is not credential material: it is confidential
authoring data that MAY be stored in git-tracked config (the later grading runtime
needs it) but MUST be redacted from every broad read surface (the read API, CLI, and
web register).

#### Scenario: No credential fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, `RequirementScope`, `Vendor`, `VendorScope`, `EvidenceCollector`, and
  `AttestationTemplate` is inspected
- **THEN** it contains no field intended to hold credential material

## ADDED Requirements

### Requirement: AttestationTemplate authorship

The system SHALL support an `AttestationTemplate` kind that describes the
attestation form for one `Control`. An `AttestationTemplate` has an `id` (its
permanent identity), a `title` (display only), a `control` (the `Control` id it
attaches to), a `type` that is exactly one of `manual` or `training`, an optional
markdown `body` (introductory or instructional copy), an optional list of `fields`,
an optional `quiz` (a list of items), and an optional `pass_mark` (an integer
percent from 0 to 100).

Each entry in `fields` is a form field with an `id` (unique within the template), a
`label` (display text), and a `type` that is exactly one of `boolean`,
`single-choice`, or `short-text`. A `single-choice` field SHALL carry `options`, a
list of at least two choice labels that are unique within the field; a `boolean` or
`short-text` field SHALL NOT declare a non-empty `options` list.

Each entry in `quiz` is an item with an `id` (unique within the template), a
`prompt` (the question text), `options` (a list of at least two answer labels that are
unique within the item), and an `answer` that is exactly one of the item's `options`
(the correct choice). The `answer` is persisted but is redacted from every read
surface, so the correct answer is not exposed to readers.

A control MAY have several attestation templates; identity is keyed on `id` only.
The template-to-control-to-requirement path holds transitively: a valid `Control`
always carries a non-empty `maps_to` of existing requirement ids, so a template that
resolves to a control resolves to at least one requirement.

#### Scenario: Manual template loads attached to a control

- **WHEN** an `AttestationTemplate` document names an `id`, a `title`, a `control`, a
  `type` of `manual`, and any `body` and `fields`
- **THEN** it loads as an `AttestationTemplate` bound to that control with type
  `manual`, its `body` and `fields` populated when present, and no `quiz` or
  `pass_mark`

#### Scenario: Training template loads with a quiz and pass mark

- **WHEN** an `AttestationTemplate` document names a `type` of `training`, a
  `pass_mark`, and a non-empty `quiz` whose items each name a `prompt`, `options`,
  and a matching `answer`
- **THEN** it loads as a training `AttestationTemplate` with its quiz items and pass
  mark populated

#### Scenario: Single-choice field loads with its options

- **WHEN** an `AttestationTemplate` field declares `type: single-choice` with an
  `options` list of two or more unique labels
- **THEN** the field loads with its options as the offered choices

### Requirement: AttestationTemplate validation

The system SHALL validate attestation-templates and report every error as a
structured diagnostic, consistent with the rest of config validation. Validation
SHALL fail when any of the following hold: an `AttestationTemplate` is missing or
blank on `id`, `title`, `control`, or `type`; an `AttestationTemplate` id is
duplicated within its kind; an unknown top-level field is present on an
`AttestationTemplate` document; an `AttestationTemplate.type` is not `manual` or `training`;
an `AttestationTemplate.control` references a `Control` id that no document defines; a
`field` is missing or blank on `id`, `label`, or `type`; two fields in one template
share an `id`; a field `type` is not one of `boolean`, `single-choice`, or
`short-text`; a `single-choice` field has fewer than two `options`; a `single-choice`
field has two `options` that share a label; a
`boolean` or `short-text` field declares a non-empty `options` list; a quiz item is missing or blank
on `id`, `prompt`, or `answer`; two quiz items in one template share an `id`; a quiz
item has fewer than two `options`; a quiz item has two `options` that share a label; a
quiz item's `answer` is not one of its
`options`; an `AttestationTemplate.pass_mark` is present but is not an integer from 0
to 100; a `training` template has no `pass_mark` or an empty `quiz`; or a `manual`
template declares a `pass_mark` or a non-empty `quiz`. An `AttestationTemplate`'s
omitted `body`, `fields`, `quiz`, and `pass_mark` are treated as absent and do NOT
fail validation, except where the `type`-conditional rules above require them.

Unknown-field rejection applies to top-level `AttestationTemplate` document keys
only (matching the loader's per-document-kind top-level key check and the
`EvidenceCollector.config` free-map precedent); it does not inspect keys inside
nested `fields`/`quiz` items. A field's or quiz item's REQUIRED nested keys (a
field's `id`/`label`/`type`, a quiz item's `id`/`prompt`/`answer`) are still rejected
when missing or blank by the required-field rules above, so the only unchecked case
is an extra unknown key inside a nested `fields`/`quiz` item, which is ignored.

#### Scenario: AttestationTemplate missing a required field

- **WHEN** an `AttestationTemplate` document omits its `control` or `type`
- **THEN** validation fails and the error list names the template and the missing
  field

#### Scenario: AttestationTemplate unknown type rejected

- **WHEN** an `AttestationTemplate` declares a `type` other than `manual` or
  `training`
- **THEN** validation fails and the error list names the template and the bad `type`

#### Scenario: Field type outside the allowed set rejected

- **WHEN** an `AttestationTemplate` field declares a `type` other than `boolean`,
  `single-choice`, or `short-text`
- **THEN** validation fails and the error list names the template, the field, and the
  bad `type`

#### Scenario: Single-choice field with fewer than two options rejected

- **WHEN** an `AttestationTemplate` field declares `type: single-choice` with no
  `options`, an empty list, or a single option
- **THEN** validation fails and the error list names the template and the field

#### Scenario: Duplicate option labels rejected

- **WHEN** a `single-choice` field or a quiz item declares two `options` that share the
  same label
- **THEN** validation fails and the error list names the template and the field or quiz
  item, so the value-based `answer` reference stays unambiguous

#### Scenario: Non-choice field with options rejected

- **WHEN** an `AttestationTemplate` field declares `type: boolean` or
  `type: short-text` but also declares a non-empty `options` list
- **THEN** validation fails and the error list names the template and the field

#### Scenario: Quiz item answer not among its options rejected

- **WHEN** a quiz item's `answer` is not equal to any of the item's `options`
- **THEN** validation fails and the error list names the template and the quiz item

#### Scenario: Training template requires a pass mark and a quiz

- **WHEN** an `AttestationTemplate` declares `type: training` but omits its
  `pass_mark` or declares an empty `quiz`
- **THEN** validation fails and the error list names the template and the missing
  pass mark or quiz

#### Scenario: Manual template with a quiz or pass mark rejected

- **WHEN** an `AttestationTemplate` declares `type: manual` but also declares a
  `pass_mark` or a non-empty `quiz`
- **THEN** validation fails and the error list names the template and the disallowed
  quiz or pass mark

#### Scenario: AttestationTemplate references an unknown control

- **WHEN** an `AttestationTemplate` names a `control` id that no document defines
- **THEN** validation fails and the error list names the template and the unknown
  reference

#### Scenario: Duplicate template id rejected

- **WHEN** two `AttestationTemplate` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: pass_mark out of range rejected

- **WHEN** an `AttestationTemplate` declares a `pass_mark` that is not an integer from
  0 to 100
- **THEN** validation fails and the error list names the template and the bad
  `pass_mark`
