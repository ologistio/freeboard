# gitops-config-format Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
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

### Requirement: Stable id is identity, title is display only

The system SHALL treat each resource's `id` as its permanent identity. The
`title` is human-facing and MAY change without changing identity. All
cross-references and duplicate detection SHALL key off `id` and SHALL NOT match
on `title`.

#### Scenario: Title change does not change identity

- **WHEN** a resource's `title` is edited but its `id` is unchanged
- **THEN** the resource is treated as the same resource, and any references to
  its `id` still resolve

#### Scenario: References resolve by id

- **WHEN** a `Control.maps_to`, an `Organisation.parent`, or a `Scope.organisation`
  or `Scope.standard` entry names an id
- **THEN** resolution matches on that `id` only, never on any resource `title`

### Requirement: Config validation

The system SHALL validate a loaded config and report all errors as a structured
list, not just the first error. Validation SHALL fail when any of the following
hold: a required field is missing or empty; an unknown field is present on a
document; an `id` is duplicated within its kind; a `Control.maps_to` is empty; a
`Control.maps_to` entry references a `Requirement` id that does not exist; a
`Control.maps_to` lists the same `Requirement` id more than once; a
`Requirement.standard` references a `Standard` id that does not exist; a
`Requirement` is missing its `standard`, `theme`, `statement`, `citation_label`,
or `citation_url`; a `Requirement.citation_url` is not a well-formed absolute
`http`/`https` URL; an `Organisation.parent` references an `Organisation` id that
does not exist; the organisations form a cycle through `parent`; a
`Scope.organisation` references an `Organisation` id that does not exist; a
`Scope.standard` references a `Standard` id that does not exist; a
`RequirementScope.organisation` references an `Organisation` id that does not exist; a
`RequirementScope.requirement` references a `Requirement` id that does not exist; a
`RequirementScope` is missing its `organisation`, `requirement`, or `disposition`; an
`Organisation.type` (the authored Company/Department value) is not `Company` or
`Department`; a `Scope.disposition` is not `In` or `Out`; a
`RequirementScope.disposition` is not `In` or `Out`; a `Standard` is missing
or blank on its required `version` or `authority`; a `Standard.source_url` is
present and non-empty but is not a well-formed absolute `http`/`https` URL; two
Scopes share the same `(organisation, standard)` pair; two RequirementScopes share
the same `(organisation, requirement)` pair; the `apiVersion` is not exactly
`freeboard.dev/v1alpha1`. Optional fields (`Requirement.guidance`,
`Standard.publisher`, `Standard.source_url`) that are omitted or whitespace-only
are treated as absent and do NOT fail validation; their non-empty and URI-format
checks run only when the field is present and non-empty. Unknown or missing `kind`
is reported by the loader (see the loader requirement below), not re-checked here.

#### Scenario: Missing required field

- **WHEN** a `Control` document omits its `maps_to` field
- **THEN** validation fails and the error list includes an entry naming the
  document and the missing field

#### Scenario: RequirementScope missing required field

- **WHEN** a `RequirementScope` document omits its `organisation`, `requirement`, or
  `disposition`
- **THEN** validation fails and the error list includes an entry naming the
  requirement-scope and the missing field

#### Scenario: RequirementScope references an unknown organisation or requirement

- **WHEN** a `RequirementScope` names an `organisation` or `requirement` id that no
  document defines
- **THEN** validation fails and the error list names the requirement-scope and the
  unknown reference

#### Scenario: RequirementScope unknown disposition rejected

- **WHEN** a `RequirementScope` declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error list names the requirement-scope and the bad
  disposition

#### Scenario: Duplicate requirement-scope mapping

- **WHEN** two `RequirementScope` documents name the same `(organisation,
  requirement)` pair
- **THEN** validation fails and the error list names the duplicated pair

#### Scenario: Unknown apiVersion rejected

- **WHEN** a document declares an `apiVersion` other than `freeboard.dev/v1alpha1`
- **THEN** validation fails and the error list names the document and the
  unknown `apiVersion`

#### Scenario: Unknown field rejected

- **WHEN** a document contains a field not defined for its kind
- **THEN** validation fails and the error list names the document and the
  unknown field

#### Scenario: Dangling scope reference

- **WHEN** a `Scope` names an `organisation` or `standard` id that no document
  defines
- **THEN** validation fails and the error list names the scope and the unknown
  reference

#### Scenario: Organisation parent cycle

- **WHEN** organisation documents form a cycle through `parent`
- **THEN** validation fails and the error list identifies the cycle

#### Scenario: Duplicate scope mapping

- **WHEN** two `Scope` documents name the same `(organisation, standard)` pair
- **THEN** validation fails and the error list names the duplicated pair

#### Scenario: Duplicate id

- **WHEN** two `Standard` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: All errors reported

- **WHEN** a config has more than one validation error
- **THEN** the error list contains an entry for every error, not only the first

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

### Requirement: Deterministic loading

The system SHALL load files in a deterministic order: files sorted by their
normalized relative path using ordinal comparison, then documents in their
in-file order. This makes validation output and reporting stable across runs and
across platforms on the same input.

#### Scenario: Order matches normalized path then in-file order

- **WHEN** a known multi-file fixture is loaded
- **THEN** the resulting config model and any error list are ordered by each
  file's normalized relative path (ordinal comparison) and then by document
  order within the file, matching the expected order for that fixture

### Requirement: Requirement authorship and standard metadata

The system SHALL support a `Requirement` kind that is DISTINCT from `Control` and
records a standard's published normative content. A `Requirement` SHALL belong to
exactly one `Standard`, named by a singular `standard` field that is a `Standard`
id. A `Requirement` SHALL carry a `theme` (a free-form label, NOT a fixed enum),
a `statement` (the normative requirement text), an optional `guidance`, and an
external citation split into a required `citation_label` (a human label for the
published source) and a required `citation_url` (an absolute `http`/`https` link
to it). `Control.maps_to` SHALL name `Requirement` ids: a control maps to the
specific requirements it satisfies, not to a whole standard. `maps_to` SHALL be a
non-empty list, each entry SHALL resolve to a defined `Requirement` id, and a
control SHALL NOT list the same `Requirement` id more than once.

The `Standard` kind SHALL support metadata: required `version` and `authority`
(the body that owns the scheme), and optional `publisher` (the delivery or
certification body) and `source_url` (the official source). `version` and
`authority` SHALL be required so a `Standard` is a described object; `publisher`
and `source_url` SHALL be optional. `theme` SHALL be a free-form string so the
model stays standard-agnostic; the five Cyber Essentials Plus themes are values a
fixture supplies, not values the schema enumerates.

Optional string fields (`Requirement.guidance`, `Standard.publisher`,
`Standard.source_url`) SHALL normalize omitted-or-whitespace-only to absent: an
absent value is stored and read as NULL, and the non-empty and URI-format checks
SHALL run only when such a field is present and non-empty (the same treatment
`Organisation.parent` gives an empty value). Required fields (`Standard.version`,
`Standard.authority`) keep the non-empty rule and SHALL fail validation when empty
or whitespace-only.

#### Scenario: Requirement is distinct from Control and owned by one standard

- **WHEN** a `Requirement` document names a `standard`, a `theme`, a `statement`,
  a `citation_label`, and a `citation_url`
- **THEN** it loads as a `Requirement` (not a `Control`), owned by the single named
  `Standard` id, and no `Control` semantics (such as `maps_to`) apply to it

#### Scenario: Control maps to requirements

- **WHEN** a `Control` document's `maps_to` names defined `Requirement` ids
- **THEN** it loads and validates, and the control is mapped to those requirements
  (not to a standard directly)

#### Scenario: Control mapping to an unknown requirement is rejected

- **WHEN** a `Control.maps_to` entry names a `Requirement` id that no `Requirement`
  document defines
- **THEN** validation fails and the error list names the control and the unknown
  requirement reference

#### Scenario: Duplicate requirement id within one control is rejected

- **WHEN** a `Control.maps_to` lists the same `Requirement` id more than once
- **THEN** validation fails and the error list names the control and the duplicated
  `Requirement` id

#### Scenario: Optional guidance omitted or blank is absent

- **WHEN** a `Requirement` omits `guidance` (or sets it to a whitespace-only value)
  but provides `standard`, `theme`, `statement`, `citation_label`, and
  `citation_url`
- **THEN** it loads and validates, and `guidance` is absent (read back as null)

#### Scenario: Standard requires version and authority

- **WHEN** a `Standard` document provides `id`, `title`, `version`, and
  `authority`, and omits `publisher` and `source_url`
- **THEN** it loads and validates, with `publisher` and `source_url` absent (read
  back as null)

#### Scenario: Blank optional standard metadata is absent, not an error

- **WHEN** a `Standard` provides `version` and `authority` but sets `publisher` or
  `source_url` to an omitted or whitespace-only value
- **THEN** it loads and validates, treating the blank optional field as absent
  rather than reporting an empty-value or malformed-URL error

#### Scenario: Standard missing version or authority is rejected

- **WHEN** a `Standard` document omits `version` or `authority`
- **THEN** validation fails and the error list names the standard and the missing
  field

#### Scenario: Theme is a free-form label

- **WHEN** two `Requirement` documents under different standards use different
  `theme` values
- **THEN** both load without the schema constraining `theme` to any fixed set

### Requirement: RequirementScope authorship

The system SHALL support a `RequirementScope` kind that binds one `Organisation` to
one `Requirement` with a `disposition`. A `RequirementScope` has an `id`, a `title`,
an `organisation` (an `Organisation` id), a `requirement` (a `Requirement` id), and a
`disposition` (`In` or `Out`). It SHALL NOT carry a `standard` field: the owning
standard is derived from the requirement. `disposition` reuses the `Scope`
disposition enum (`In` or `Out`). A `RequirementScope` records requirement-level
scoping layered under the standard-level `Scope`; how it resolves down the
organisation tree is defined by the statement-of-applicability capability.

#### Scenario: RequirementScope loads with organisation, requirement, and disposition

- **WHEN** a `RequirementScope` document names an `organisation`, a `requirement`,
  and a `disposition` of `In` or `Out`
- **THEN** it loads as a `RequirementScope` bound to that organisation and
  requirement with that disposition, and no `standard` field is expected on it

#### Scenario: RequirementScope naming a standard field is rejected

- **WHEN** a `RequirementScope` document includes a `standard` field
- **THEN** validation fails and the error names the requirement-scope and the unknown
  field, because `RequirementScope` derives its standard from the requirement

### Requirement: Vendor and VendorScope authorship

The system SHALL support a `Vendor` kind that names a piece of software or a
platform in use (for example Crowdstrike, FleetDM, Google Workspace, an outsourced
accountant). A `Vendor` has an `id` (its permanent identity) and a `title` (display
only) and carries no other required fields in this increment.

The system SHALL support a `VendorScope` kind that records whether one
`Requirement` or one `Control` applies to one `Vendor`, with an exception
rationale. A `VendorScope` has an `id`, a `title`, a `vendor` (a `Vendor` id),
exactly one of `requirement` (a `Requirement` id) or `control` (a `Control` id), a
`disposition` (`In` or `Out`), and a `justification`. The `disposition` reuses the
`Scope` disposition (`In` or `Out`): `In` means the requirement or control applies
to the vendor; `Out` means it is excepted for that vendor. A `VendorScope` is a flat
per-`(vendor, target)` statement and SHALL NOT carry an `organisation` field; it
does not participate in organisation-tree inheritance. At most one `VendorScope`
SHALL exist per `(vendor, requirement)` pair and at most one per `(vendor, control)`
pair.

#### Scenario: Vendor loads with id and title

- **WHEN** a `Vendor` document names an `id` and a `title`
- **THEN** it loads as a `Vendor` with that identity and display title and no other
  required field

#### Scenario: VendorScope loads targeting a requirement

- **WHEN** a `VendorScope` document names a `vendor`, a `requirement`, a
  `disposition`, and (for `Out`) a `justification`
- **THEN** it loads as a `VendorScope` bound to that vendor and requirement with
  that disposition and justification, and no `control` or `organisation` field is
  expected on it

#### Scenario: VendorScope loads targeting a control

- **WHEN** a `VendorScope` document names a `vendor`, a `control`, a `disposition`,
  and (for `Out`) a `justification`
- **THEN** it loads as a `VendorScope` bound to that vendor and control, and no
  `requirement` field is expected on it

### Requirement: Vendor and VendorScope validation

The system SHALL validate vendors and vendor-scopes and report every error as a
structured diagnostic, consistent with the rest of config validation. Validation
SHALL fail when any of the following hold: a `Vendor` or `VendorScope` is missing or
blank on `id` or `title`; a `Vendor` id or `VendorScope` id is duplicated within its
kind; an unknown field is present on a `Vendor` or `VendorScope`; a `VendorScope`
is missing its `vendor` or `disposition`; a `VendorScope` names neither
`requirement` nor `control`, or names both; a `VendorScope.vendor` references a
`Vendor` id that no document defines; a `VendorScope.requirement` references a
`Requirement` id that no document defines; a `VendorScope.control` references a
`Control` id that no document defines; a `VendorScope.disposition` is not `In` or
`Out`; two vendor-scopes share the same `(vendor, requirement)` pair or the same
`(vendor, control)` pair; or a `VendorScope` whose disposition is `Out` has a
missing or whitespace-only `justification`. A `VendorScope` whose disposition is
`In` SHALL NOT require a `justification`. An omitted or whitespace-only
`justification` on an `In` vendor-scope is treated as absent and does not fail
validation.

#### Scenario: VendorScope missing required field

- **WHEN** a `VendorScope` document omits its `vendor` or `disposition`
- **THEN** validation fails and the error list names the vendor-scope and the
  missing field

#### Scenario: VendorScope must name exactly one target

- **WHEN** a `VendorScope` names both `requirement` and `control`, or names neither
- **THEN** validation fails and the error list names the vendor-scope and the target
  problem

#### Scenario: VendorScope references an unknown vendor or target

- **WHEN** a `VendorScope` names a `vendor`, `requirement`, or `control` id that no
  document defines
- **THEN** validation fails and the error list names the vendor-scope and the
  unknown reference

#### Scenario: Out disposition requires a justification

- **WHEN** a `VendorScope` declares `disposition: Out` with no `justification` (or a
  whitespace-only one)
- **THEN** validation fails and the error list names the vendor-scope and the
  missing justification

#### Scenario: In disposition does not require a justification

- **WHEN** a `VendorScope` declares `disposition: In` with no `justification`
- **THEN** it loads and validates, with `justification` absent

#### Scenario: Duplicate vendor-scope mapping

- **WHEN** two `VendorScope` documents name the same `(vendor, requirement)` pair or
  the same `(vendor, control)` pair
- **THEN** validation fails and the error list names the duplicated pair

#### Scenario: VendorScope unknown disposition rejected

- **WHEN** a `VendorScope` declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error list names the vendor-scope and the bad
  disposition

### Requirement: EvidenceCollector authorship and Control evaluation rule

The system SHALL support an `EvidenceCollector` kind that attaches a data source to
one `Control`. An `EvidenceCollector` has an `id` (its permanent identity), a `title`
(display only), a `control` (the `Control` id it attaches to), an optional `vendor`
(a `Vendor` id), a `type` that is exactly one of `integration`, `script`,
`manual-attestation`, `training-attestation`, or `agent`, a `frequency` that is a
collection cadence (`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or
`annual`), an optional `threshold` (an integer percent from 0 to 100 giving the share
of the collector's checks that must pass), and an optional `config` (a free-form map
of type-specific settings). A control MAY have several collectors; identity is keyed on
`id` only. The collector-to-control-to-requirement path holds transitively: a valid
`Control` always carries a non-empty `maps_to` of existing requirement ids, so a
collector that resolves to a control resolves to at least one requirement.

The system SHALL extend the `Control` kind with an optional `evaluation` rule that is
exactly one of `all`, `any`, or `manual`: `all` means the control is satisfied only
if every attached collector is satisfied; `any` means it is satisfied if at least one
attached collector is satisfied; `manual` means a human sets the control status and
collectors are advisory. `evaluation` is optional on a control that has no attached
collectors; it is REQUIRED on a control that has at least one attached collector.

#### Scenario: EvidenceCollector loads attached to a control

- **WHEN** an `EvidenceCollector` document names an `id`, a `title`, a `control`, a
  `type`, and a `frequency`
- **THEN** it loads as an `EvidenceCollector` bound to that control with that type
  and frequency, and its optional `vendor`, `threshold`, and `config` populated when
  present

#### Scenario: Control loads with an evaluation rule

- **WHEN** a `Control` document names an `evaluation` of `all`, `any`, or `manual`
- **THEN** it loads with that evaluation rule alongside its `maps_to`

### Requirement: EvidenceCollector and Control evaluation validation

The system SHALL validate evidence-collectors and the control evaluation rule and
report every error as a structured diagnostic, consistent with the rest of config
validation. Validation SHALL fail when any of the following hold: an
`EvidenceCollector` is missing or blank on `id`, `title`, `control`, `type`, or
`frequency`; an `EvidenceCollector` id is duplicated within its kind; an unknown
field is present on an `EvidenceCollector`; an `EvidenceCollector.type` is not one of
`integration`, `script`, `manual-attestation`, `training-attestation`, or `agent`; an
`EvidenceCollector.frequency` is not one of `continuous`, `daily`, `weekly`,
`monthly`, `quarterly`, or `annual`; an `EvidenceCollector.threshold` is present but
is not an integer from 0 to 100; an `EvidenceCollector.control` references a `Control`
id that no document defines; an `EvidenceCollector.vendor` is present but references a
`Vendor` id that no document defines; a `Control.evaluation` is present but is not one
of `all`, `any`, or `manual`; or a `Control` has at least one attached
`EvidenceCollector` but has no `evaluation` rule. A collector's `vendor`, `threshold`,
and `config` that are omitted are treated as absent and do NOT fail validation.

#### Scenario: EvidenceCollector missing a required field

- **WHEN** an `EvidenceCollector` document omits its `control`, `type`, or `frequency`
- **THEN** validation fails and the error list names the collector and the missing
  field

#### Scenario: EvidenceCollector unknown type rejected

- **WHEN** an `EvidenceCollector` declares a `type` other than `integration`,
  `script`, `manual-attestation`, `training-attestation`, or `agent`
- **THEN** validation fails and the error list names the collector and the bad `type`

#### Scenario: EvidenceCollector unknown frequency rejected

- **WHEN** an `EvidenceCollector` declares a `frequency` outside the cadence set
- **THEN** validation fails and the error list names the collector and the bad
  `frequency`

#### Scenario: EvidenceCollector threshold out of range rejected

- **WHEN** an `EvidenceCollector` declares a `threshold` that is not an integer from
  0 to 100
- **THEN** validation fails and the error list names the collector and the bad
  `threshold`

#### Scenario: EvidenceCollector references an unknown control or vendor

- **WHEN** an `EvidenceCollector` names a `control` id, or a `vendor` id, that no
  document defines
- **THEN** validation fails and the error list names the collector and the unknown
  reference

#### Scenario: Duplicate collector id rejected

- **WHEN** two `EvidenceCollector` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: Control evaluation unknown value rejected

- **WHEN** a `Control` declares an `evaluation` other than `all`, `any`, or `manual`
- **THEN** validation fails and the error list names the control and the bad
  `evaluation`

#### Scenario: Control with collectors requires an evaluation rule

- **WHEN** a `Control` has at least one attached `EvidenceCollector` but declares no
  `evaluation`
- **THEN** validation fails and the error list names the control and the missing
  `evaluation` rule

#### Scenario: Control without collectors needs no evaluation rule

- **WHEN** a `Control` has no attached `EvidenceCollector` and declares no `evaluation`
- **THEN** it validates without an evaluation error

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

