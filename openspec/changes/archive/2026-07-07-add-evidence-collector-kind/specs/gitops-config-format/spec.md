## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the requirements published
by each standard, the organisations being assessed, the scopes that map an
organisation to a standard, the requirement-scopes that map an organisation to a
requirement, the vendors (software and platforms) in use, the vendor-scopes
that record whether a requirement or control applies to a vendor, and the
evidence-collectors that attach a data source to a control. The format SHALL
be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Organisation`, `Scope`,
`RequirementScope`, `Vendor`, `VendorScope`, and `EvidenceCollector`. Documents of
different kinds MAY appear in any file. Every resource SHALL have an immutable `id`
that is its identity and a mutable `title` for display. A `Standard` has an `id`, a
`title`, required `version` and `authority`, and optional `publisher` and
`source_url` metadata. A `Control` has an `id`, a `title`, a `maps_to` field that is
a non-empty list of `Requirement` ids (the requirements the control satisfies), and
an optional `evaluation` rule (`all`, `any`, or `manual`) that says how the control's
attached evidence-collectors roll up into a status. A `Requirement` has an `id`, a
`title`, a `standard` (a single `Standard` id it belongs to), a `theme` (a free-form
label), a `statement` (the normative text), an optional `guidance`, a
`citation_label`, and a `citation_url` (an absolute `http`/`https` link to the
published source). An `Organisation` has an `id`, a `title`, a `type` (`Company` or
`Department`), and an optional `parent` that is an `Organisation` id. The
Organisation's Company/Department value is authored under the YAML key `type` so it
does not collide with the document discriminator `kind`; it persists and reads back
as the organisation's `kind`. A `Scope` has an `id`, a `title`, an `organisation` (an
`Organisation` id), a `standard` (a `Standard` id), and a `disposition` (`In` or
`Out`). A `RequirementScope` has an `id`, a `title`, an `organisation` (an
`Organisation` id), a `requirement` (a `Requirement` id), and a `disposition` (`In`
or `Out`); it has no `standard` field. A `Vendor` has an `id` and a `title`. A
`VendorScope` has an `id`, a `title`, a `vendor` (a `Vendor` id), exactly one of
`requirement` (a `Requirement` id) or `control` (a `Control` id), a `disposition`
(`In` or `Out`), and a `justification` (required when the disposition is `Out`). An
`EvidenceCollector` has an `id`, a `title`, a `control` (a `Control` id it attaches
to), an optional `vendor` (a `Vendor` id), a `type` (exactly one of `integration`,
`script`, `manual-attestation`, `training-attestation`, or `agent`), a `frequency`
(a collection cadence: `continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or
`annual`), an optional `threshold` (an integer percent from 0 to 100), and an
optional `config` (a free-form map of type-specific settings). Property binding is
snake_case for domain/property fields, so `maps_to` and `source_url` bind to their
model properties. The document discriminator key `kind` and the version key
`apiVersion` are camelCase to match the Kubernetes-style convention; they are not
snake_case-bound, so `apiVersion` is the valid key (not `api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Organisation`, `Scope`, `RequirementScope`, `Vendor`,
  `VendorScope`, and `EvidenceCollector`
- **THEN** the loader returns a typed config model containing all standards
  (with any metadata), controls (with any `evaluation` rule), requirements,
  organisations, scopes, requirement-scopes, vendors, vendor-scopes, and
  evidence-collectors with their `id`, `title`, and reference fields populated and
  no errors

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
  `VendorScope`, or `EvidenceCollector`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds a secret (token, key, password,
or equivalent). Credentials needed by future integrations SHALL be referenced by
a named credential resolved out-of-band, never inlined in git-tracked config. The
`EvidenceCollector.config` map is git-tracked type-specific settings and SHALL NOT
inline a secret; a collector that needs a credential names it for out-of-band
resolution.

#### Scenario: No secret fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, `RequirementScope`, `Vendor`, `VendorScope`, and `EvidenceCollector` is
  inspected
- **THEN** it contains no field intended to hold secret material

## ADDED Requirements

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
