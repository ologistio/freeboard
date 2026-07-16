## ADDED Requirements

### Requirement: Unified Scope authorship

The system SHALL support one `Scope` kind that records whether a target applies to a
subject, replacing the previous `Scope`, `RequirementScope`, and `VendorScope` kinds. A
`Scope` has an immutable `id`, a mutable `title`, a `subject` (an asset id), exactly one
of a `standard` (a `Standard` id), a `requirement` (a `Requirement` id), or a `control`
(a `Control` id), a `disposition` (`In` or `Out`), and an optional `justification`. The
`subject` names the asset the scope is about; the one target names what it scopes the
subject in or out of. `disposition` `In` means the target applies to the subject; `Out`
means the subject is excepted from it. A `justification` is REQUIRED on every `Out` scope
(it records the exception rationale) and optional on `In`.

The `subject` is a scalar asset reference with NO foreign key, validated at write and
dangling-tolerated (see Unified Scope validation): it MAY name a Company, Department, Machine,
or Vendor asset (a group later). A `Scope` whose `subject` resolves to a `Vendor` asset MAY
target only a `requirement` or a `control`, never a `standard`, because a vendor has no
standard-level disposition; an organisation (Company/Department) subject MAY target a
standard, a requirement, or a control. At most one `Scope` SHALL exist per `(subject,
standard)`, per `(subject, requirement)`, and per `(subject, control)` pair.

#### Scenario: Scope targeting a standard loads

- **WHEN** a `kind: Scope` document names a `subject`, a `standard`, and a `disposition`
- **THEN** it loads as a `Scope` bound to that subject and standard with that disposition,
  and no `requirement` or `control` field is expected on it

#### Scenario: Scope targeting a requirement loads

- **WHEN** a `kind: Scope` document names a `subject`, a `requirement`, and a `disposition`
- **THEN** it loads as a `Scope` bound to that subject and requirement, and no `standard`
  or `control` field is expected on it

#### Scenario: Scope targeting a control loads

- **WHEN** a `kind: Scope` document names a `subject`, a `control`, and a `disposition`
- **THEN** it loads as a `Scope` bound to that subject and control, and no `standard` or
  `requirement` field is expected on it

#### Scenario: Out scope carries a justification

- **WHEN** a `kind: Scope` document declares `disposition: Out` with a non-empty
  `justification`
- **THEN** it loads with that justification, which the read surfaces always show so an
  exception is never silent

#### Scenario: Unknown field on a Scope is rejected

- **WHEN** a `kind: Scope` document carries a field not defined for the kind (for example
  the removed `organisation` or `vendor` field)
- **THEN** the loader reports the document and the unknown field

### Requirement: Unified Scope validation

The system SHALL validate scopes and report every error as a structured diagnostic.
Validation SHALL fail (an `Error` diagnostic) when any of the following hold: a `Scope` is
missing or blank on `id`, `title`, `subject`, or `disposition`; a `Scope` id is
duplicated; an unknown field is present on a `Scope`; a `Scope` names neither `standard`
nor `requirement` nor `control`, or names more than one of them (exactly one target is
required); a `Scope.standard` references a `Standard` id that no document defines; a
`Scope.requirement` references a `Requirement` id that no document defines; a
`Scope.control` references a `Control` id that no document defines; a `Scope.disposition`
is not `In` or `Out`; a `Scope` whose disposition is `Out` has a missing or
whitespace-only `justification`; a `Scope` whose `subject` resolves to a `Vendor` asset
targets a `standard`; or two scopes share the same `(subject, standard)`, `(subject,
requirement)`, or `(subject, control)` pair. A `Scope` whose disposition is `In` SHALL NOT
require a `justification`.

A `Scope.subject` that names an id absent from the resolved asset set (a dangling subject)
SHALL NOT be an error: it SHALL be reported as a NON-BLOCKING `Warning` diagnostic that
does not fail `validate`, `apply`, or `sync`, because the subject is a scalar asset
reference and an asset may be retired or not-yet-discovered. The three target references
(`standard`, `requirement`, `control`) keep referential integrity and remain hard errors
when they do not resolve. The Vendor-subject-targets-a-standard cross-field check is
evaluated only when the subject resolves to an asset; a dangling subject warns and is not
additionally checked against the cross-field rule.

#### Scenario: Scope must name exactly one target

- **WHEN** a `Scope` names none of `standard`/`requirement`/`control`, or names more than
  one
- **THEN** validation fails and the error list names the scope and the target problem

#### Scenario: Out disposition requires a justification

- **WHEN** a `Scope` declares `disposition: Out` with no `justification` (or a
  whitespace-only one)
- **THEN** validation fails and the error list names the scope and the missing
  justification, for every target kind (standard, requirement, or control)

#### Scenario: In disposition does not require a justification

- **WHEN** a `Scope` declares `disposition: In` with no `justification`
- **THEN** it loads and validates, with `justification` absent

#### Scenario: Vendor subject may not target a standard

- **WHEN** a `Scope` whose `subject` resolves to a `Vendor` asset names a `standard`
- **THEN** validation fails and the error list names the scope, because a vendor has no
  standard-level disposition

#### Scenario: Dangling subject is a non-blocking warning

- **WHEN** a `Scope.subject` names an id that no asset defines
- **THEN** the error list contains no error for it; a non-blocking `Warning` names the
  dangling subject and validation still passes

#### Scenario: Dangling target is an error

- **WHEN** a `Scope.standard`, `Scope.requirement`, or `Scope.control` names an id that no
  document defines
- **THEN** validation fails and the error list names the scope and the unknown target
  reference

#### Scenario: Duplicate subject-target pair rejected

- **WHEN** two `Scope` documents name the same `(subject, standard)`, `(subject,
  requirement)`, or `(subject, control)` pair
- **THEN** validation fails and the error list names the duplicated pair

## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as a
set of standards, the controls under each standard, the requirements published by
each standard, the assets being assessed (the organisation tree, the vendors in
use, and the discovered machines), the scopes that map a subject asset to a standard, a
requirement, or a control with a disposition, the integrations that define a provider
connection's base URL and discovery cadence, the evidence-collectors that attach a data
source to a control, and the attestation-templates that describe an attestation form for a
control. The format SHALL be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Asset`, `Scope`, `Integration`,
`EvidenceCollector`, and `AttestationTemplate`. Documents of different kinds MAY appear in
any file. Every resource SHALL have an immutable `id` that is its identity and a mutable
`title` for display. A `Standard` has an `id`, a `title`, required `version` and
`authority`, and optional `publisher` and `source_url` metadata. A `Control` has an
`id`, a `title`, a `maps_to` field that is a non-empty list of `Requirement` ids,
and an optional `evaluation` rule (`all`, `any`, or `manual`). A `Requirement` has
an `id`, a `title`, a `standard` (a single `Standard` id it belongs to), a `theme`,
a `statement`, an optional `guidance`, a `citation_label`, and a `citation_url` (an
absolute `http`/`https` link). An `Asset` has an `id`, a `title`, a `type`
(`Company`, `Department`, `Machine`, or `Vendor`), a `source` (`declared` or
`discovered`, of which only `declared` is authorable), and at most one of a
`parent` (a `Company`/`Department` asset id) or an `owner` (a `Company`/`Department`
asset id); its Company/Department/Machine/Vendor value is authored under the YAML
key `type` so it does not collide with the document discriminator `kind`. A `Scope`
has an `id`, a `title`, a `subject` (an asset id), exactly one of a `standard` (a
`Standard` id), a `requirement` (a `Requirement` id), or a `control` (a `Control` id), a
`disposition` (`In` or `Out`), and an optional `justification` (required when the
disposition is `Out`); a `Scope` whose subject is a `Vendor` asset may target only a
`requirement` or a `control`. An `Integration` has an `id`, a `title`, a required
`provider` (a closed token set whose only value in this increment is `fleet`), a required
`base_url` (an absolute `http`/`https` URL), a required `discovery_cadence` (`continuous`,
`daily`, `weekly`, `monthly`, `quarterly`, or `annual`), and an optional `vendor`
(a `Vendor` asset id); its API token is never authored in config. An
`EvidenceCollector` has an `id`, a `title`, a `control` (a `Control` id), an
optional `vendor` (a `Vendor` asset id), a `type` (exactly one of `integration`,
`script`, `manual-attestation`, `training-attestation`, or `agent`), a `frequency`
(`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or `annual`), an optional
`threshold` (an integer percent from 0 to 100), an optional `config`, a
`connection` (an `Integration` id, required when `type` is `integration`
and empty otherwise), and a `checks` list (each item a `source_key`, a `name`, and
a `severity` of `Hard` or `Soft`, required and non-empty when `type` is
`integration`). An `AttestationTemplate` has an `id`, a `title`, a `control`, a
`type` (`manual` or `training`), an optional markdown `body`, an optional list of
`fields`, an optional `quiz`, and an optional `pass_mark`; a `training` template
requires a `pass_mark` and at least one `quiz` item, a `manual` declares neither,
and the quiz `answer` is git-tracked authoring data redacted from every read
surface. Property binding is snake_case for domain fields; `apiVersion` and `kind`
are camelCase.

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Asset`, `Scope`, `Integration`, `EvidenceCollector`, and
  `AttestationTemplate`
- **THEN** the loader returns a typed config model containing all standards,
  controls, requirements, assets (with `type`, `source`, and any `parent`/`owner`),
  scopes (each with its `subject`, one target of `standard`/`requirement`/`control`,
  `disposition`, and any `justification`), integrations, evidence-collectors, and
  attestation-templates with their `id`, `title`, and reference fields populated and no
  errors

#### Scenario: Multiple documents in one file

- **WHEN** a single YAML file contains multiple documents separated by `---`
- **THEN** every document is parsed and included in the config model

### Requirement: Config validation

The system SHALL validate a loaded config and report all errors as a structured
list, not just the first error. Validation SHALL fail (an `Error` diagnostic) when
any of the following hold: a required field is missing or empty; an unknown field
is present on a document; an `id` is duplicated within its kind; a `Control.maps_to`
is empty; a `Control.maps_to` entry references a `Requirement` id that does not
exist; a `Control.maps_to` lists the same `Requirement` id more than once; a
`Requirement.standard` references a `Standard` id that does not exist; a
`Requirement` is missing its `standard`, `theme`, `statement`, `citation_label`, or
`citation_url`; a `Requirement.citation_url` is not a well-formed absolute
`http`/`https` URL; an `Asset.type` is not `Company`, `Department`, `Machine`, or
`Vendor`; an `Asset.source` is not `declared` or `discovered`; a declared config
authors `source: discovered`; a declared config authors any discovered-only field
(`identity_kind`, `identity_value`, `state`, `first_seen`, `last_seen`); an `Asset`
carries both `parent` and `owner`; an
`Asset.parent` or `Asset.owner` names an asset that is not a `Company` or
`Department`; a `parent` is carried by an asset that is not a Company, Department,
or Machine; an `owner` is carried by an asset that is not a Vendor; a `Scope` is missing
its `subject` or `disposition`; a `Scope` names none of `standard`/`requirement`/`control`
or more than one of them; a `Scope.standard`, `Scope.requirement`, or `Scope.control`
references an id that no document defines; a `Scope.disposition` is not `In` or `Out`; a
`Scope` whose disposition is `Out` has a missing or whitespace-only `justification`; a
`Scope` whose `subject` resolves to a `Vendor` asset targets a `standard`; a `Standard` is
missing or blank on `version` or `authority`; a `Standard.source_url` is present and
non-empty but not a well-formed absolute `http`/`https` URL; two Scopes share the same
`(subject, standard)`, `(subject, requirement)`, or `(subject, control)` pair; the
`apiVersion` is not exactly `freeboard.dev/v1alpha1`. Optional fields that are omitted or
whitespace-only are treated as absent. A dangling `Asset.parent` or `Asset.owner`, a
`parent` cycle among assets, a missing required edge (a declared `Vendor` with no `owner`
or a `Machine` with no `parent`), and a dangling `Scope.subject` SHALL be reported as
NON-BLOCKING `Warning` diagnostics that do not fail validation, not as errors. Unknown or
missing `kind` is reported by the loader, not re-checked here.

#### Scenario: Missing required field

- **WHEN** a `Control` document omits its `maps_to` field
- **THEN** validation fails and the error list includes an entry naming the
  document and the missing field

#### Scenario: Unknown apiVersion rejected

- **WHEN** a document declares an `apiVersion` other than `freeboard.dev/v1alpha1`
- **THEN** validation fails and the error list names the document and the unknown
  `apiVersion`

#### Scenario: Unknown field rejected

- **WHEN** a document contains a field not defined for its kind
- **THEN** validation fails and the error list names the document and the unknown
  field

#### Scenario: Dangling scope target reference

- **WHEN** a `Scope` names a `standard`, `requirement`, or `control` id that no document
  defines
- **THEN** validation fails and the error list names the scope and the unknown target

#### Scenario: Dangling scope subject is a warning, not an error

- **WHEN** a `Scope.subject` names an id that no asset defines
- **THEN** the error list contains no error for it; a non-blocking warning names the
  dangling subject and validation still passes

#### Scenario: Dangling asset parent is a warning, not an error

- **WHEN** an `Asset.parent` names an id that no asset defines
- **THEN** the error list contains no error for it; a non-blocking warning names the
  dangling reference and validation still passes

#### Scenario: Duplicate scope mapping

- **WHEN** two `Scope` documents name the same `(subject, standard)`, `(subject,
  requirement)`, or `(subject, control)` pair
- **THEN** validation fails and the error list names the duplicated pair

#### Scenario: All errors reported

- **WHEN** a config has more than one validation error
- **THEN** the error list contains an entry for every error, not only the first

### Requirement: Loader and validator never throw or print

The loader and validator in `Freeboard.Core` SHALL return diagnostics as data and
SHALL NOT throw exceptions for malformed or invalid input, and SHALL NOT write to
any output stream. Callers decide how to present results and set exit codes.
Diagnostics carry a severity (`Error` or `Warning`); a config is valid when it has
no `Error` diagnostics, so a `Warning` (for example a dangling `parent`/`owner` or a
dangling `Scope.subject`) does not fail loading or validation.

#### Scenario: Malformed input returns diagnostics

- **WHEN** a config file contains malformed YAML that the parser cannot read
- **THEN** the loader catches the parse error, returns a result with a diagnostic
  naming the file (and line/column where available) rather than throwing, and
  writes nothing to output

#### Scenario: Unknown or missing kind reported by the loader

- **WHEN** a document has a `kind` that is missing or not one of `Standard`,
  `Control`, `Requirement`, `Asset`, `Scope`, `Integration`, `EvidenceCollector`, or
  `AttestationTemplate`
- **THEN** the loader returns a diagnostic naming the document and the bad `kind`,
  does not throw, and does not deserialize that document further

#### Scenario: Retired RequirementScope and VendorScope kinds are now unknown

- **WHEN** a document authors `kind: RequirementScope` or `kind: VendorScope`, the
  pre-generalization wire tokens
- **THEN** the loader loads no scope from it and returns an unknown-kind diagnostic naming
  the document and the bad `kind`; the diagnostic's valid-kinds enumeration lists `Scope`
  and does NOT contain `RequirementScope` or `VendorScope`

### Requirement: Asset authoring, type, source, and edges

The system SHALL support an `Asset` kind that unifies the previous `Organisation`
and `Vendor` kinds and the discovered machine model into one resource. An `Asset`
has an immutable `id`, a mutable `title`, a required `type` (exactly one of
`Company`, `Department`, `Machine`, or `Vendor`), a required `source` (exactly one
of `declared` or `discovered`), and at most one of two mutually exclusive scalar
edges: `parent` (a `Company` or `Department` asset id) or `owner` (a `Company` or
`Department` asset id). A `Company`, `Department`, or `Machine` asset MAY carry a
`parent`; a `Vendor` asset MAY carry an `owner`. A declared asset MAY carry the
authored fields only; the discovered-only fields (`identity_kind`,
`identity_value`, `state`, `first_seen`, `last_seen`) are written by ingest, never
authored in config.

A declared config MAY author `source: declared` only. `source: discovered` is
reserved for ingest and SHALL be rejected when authored in config. A declared
asset uses an authored slug id; a discovered asset uses a ULID id; both share one
id space. `parent` and `owner` are scalar references validated at write with no
foreign key: a reference that does not resolve is tolerated (see Asset validation).
The `Scope.subject` and `EvidenceCollector.vendor` references SHALL name the matching
asset: `Scope.subject` names any asset (a Company/Department, a Machine, or a Vendor; a Vendor
subject may not target a standard), and `EvidenceCollector.vendor` names a `Vendor` asset. The
`Scope.subject` reference is scalar with no foreign key and is dangling-tolerated, while
the `Scope` target references (`standard`/`requirement`/`control`) keep referential
integrity.

#### Scenario: Company asset with a department child loads

- **WHEN** a `kind: Asset` document of `type: Company` with `source: declared` and a
  `kind: Asset` of `type: Department` with `source: declared` and a `parent` naming
  the Company both appear
- **THEN** both load into the typed model, the Company is a root asset, and the
  Department is its child

#### Scenario: Vendor asset with an owner loads

- **WHEN** a `kind: Asset` of `type: Vendor` with `source: declared` and an `owner`
  naming a `Company` asset appears
- **THEN** it loads as a declared vendor asset owned by that Company

#### Scenario: Declared source is the only authorable source

- **WHEN** a `kind: Asset` document authors `source: declared`
- **THEN** it loads, whereas a document authoring `source: discovered` is rejected
  (see Asset validation)

#### Scenario: Unknown field on an Asset is rejected

- **WHEN** a `kind: Asset` document carries a field not defined for the kind
- **THEN** the loader reports the document and the unknown field

## REMOVED Requirements

### Requirement: RequirementScope authorship

**Reason**: Folded into the unified `Scope` kind. A requirement-level disposition is now a
`Scope` whose `subject` is an organisation asset and whose target is a `requirement`.

**Migration**: Rewrite each `kind: RequirementScope` document as `kind: Scope` with
`subject` (the former `organisation`) and `requirement` (unchanged); add a `justification`
when the disposition is `Out`. See the Unified Scope authorship and Unified Scope
validation requirements.

### Requirement: Vendor and VendorScope authorship

**Reason**: The `VendorScope` kind is folded into the unified `Scope` kind (a vendor-scope
is now a `Scope` whose `subject` is a `Vendor` asset and whose target is a `requirement`
or a `control`). The "vendor is an `Asset` of `type: Vendor`" fact is already stated by
the Asset authoring requirement.

**Migration**: Rewrite each `kind: VendorScope` document as `kind: Scope` with `subject`
(the former `vendor`) and its `requirement` or `control` target unchanged; the
`justification` requirement on `Out` carries over unchanged. See the Unified Scope
authorship requirement.

### Requirement: Vendor and VendorScope validation

**Reason**: VendorScope validation is folded into Unified Scope validation, which applies
the same exactly-one-target and `Out`-requires-justification rules to every scope and adds
the Vendor-subject-may-not-target-a-standard cross-field rule.

**Migration**: The vendor-scope rules (exactly one of `requirement`/`control`, `Out`
requires `justification`, unique per `(vendor, requirement)` and `(vendor, control)`) are
preserved by the Unified Scope validation requirement, now keyed on `subject` and covering
the `standard` target as well.
