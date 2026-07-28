# gitops-config-format Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as a
set of standards, the controls under each standard, the requirements published by
each standard, the assets being assessed (the organisation tree, the vendors in
use, and the discovered machines), the scopes that map a subject asset to a standard, a
requirement, or a control with a disposition, the integrations that define a provider
connection's base URL and discovery cadence, and the collectors that attach a proving
mechanism - a data source or an attestation form - to a control. The format SHALL be
loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Asset`, `Scope`, `Integration`, and
`Collector`. Documents of different kinds MAY appear in
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
(a `Vendor` asset id); its API token is never authored in config. A
`Collector` has an `id`, a `title`, a `control` (a `Control` id), an
optional `vendor` (a `Vendor` asset id), a `type` (exactly one of `integration`,
`script`, `agent`, `manual`, or `training`), a `frequency`
(`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or `annual`), an optional
`threshold` (an integer percent from 0 to 100), an optional `config` whose keys are
closed by the schema registered for the collector's `(type, provider)` pair, and - only
when `type` is `integration` - a required `provider` (the same closed provider token set
an `Integration` uses) and a required `connection` (an `Integration` id). A collector's
top-level fields SHALL be limited to its identity, its attach point, its cross-document
references, and its cadence; every type-specific payload SHALL be authored inside `config`.
So an integration collector authors its tracked checks inside `config` (each item a
`source_key`, a `name`, and a `severity` of `Hard` or `Soft`), and a `manual` or
`training` collector carries its form or quiz inside `config`. Which of those keys each pair
must and may author is decided by the registered `(type, provider)` schema alone: in this
increment `(integration, fleet)` requires a non-empty `checks` and `(training, -)` requires
a `pass_mark` and a non-empty `quiz`. The quiz
`answer` is git-tracked authoring data redacted from every read
surface. `frequency` is required on every `type`, including `manual` and `training`: a
collector of any type that is registered with a vendor may post evidence, evidence ingest
records the collector's cadence on each run it appends, and staleness is judged from that
recorded cadence, so a collector with no cadence would produce evidence that can never be
evaluated as stale. Property binding is snake_case for domain fields; `apiVersion` and
`kind` are camelCase.

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Asset`, `Scope`, `Integration`, and `Collector`
- **THEN** the loader returns a typed config model containing all standards,
  controls, requirements, assets (with `type`, `source`, and any `parent`/`owner`),
  scopes (each with its `subject`, one target of `standard`/`requirement`/`control`,
  `disposition`, and any `justification`), integrations, and collectors with their `id`,
  `title`, reference fields, and typed `config` populated and no errors

#### Scenario: Multiple documents in one file

- **WHEN** a single YAML file contains multiple documents separated by `---`
- **THEN** every document is parsed and included in the config model

#### Scenario: Pre-merge top-level payload fields are unknown on a Collector

- **WHEN** a `Collector` document authors `body`, `fields`, `pass_mark`, `quiz`, or
  `checks` as a top-level field, the shape a half-migrated `AttestationTemplate` or
  `EvidenceCollector` document produces
- **THEN** the loader returns an unknown-field diagnostic naming the document and that
  field, so the cutover cannot silently accept two authoring shapes alongside the `config`
  form

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

- **WHEN** a `Control.maps_to`, an `Asset.parent`, an `Asset.owner`, a `Scope.subject`,
  or a `Scope.standard`, `Scope.requirement`, or `Scope.control` entry names an id
- **THEN** resolution matches on that `id` only, never on any resource `title`

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

A `Collector`'s `config` is a mapping whose keys the registered `(type, provider)` schema
closes, and each of `fields`, `quiz`, and `checks` inside it is a list. A document that
authors any of those with the wrong YAML shape SHALL produce a diagnostic naming the
document, SHALL load no collector from that document, and SHALL NOT prevent the remaining
documents in the directory from loading. When `config` is not a mapping the key-set diff
SHALL be skipped rather than attempted, so one wrong-shape mistake yields one diagnostic
rather than a cascade of unknown-key diagnostics.

A `config` key authored with NO VALUE is NOT a wrong shape and SHALL NOT produce a
diagnostic. An explicit null binds cleanly to an absent config, so the loader SHALL
normalise it to an empty config and load the collector exactly as if `config` had been
omitted. The same rule applies one level in, to an explicit-null `fields`, `quiz`, or
`checks` key. This is stated because it is the case the never-throw contract turns on: a
scalar or a sequence fails the typed bind, which the loader already reports as a
diagnostic, whereas an explicit null fails nothing at load time and would surface only as a
null dereference later.

#### Scenario: Malformed input returns diagnostics

- **WHEN** a config file contains malformed YAML that the parser cannot read
- **THEN** the loader catches the parse error, returns a result with a diagnostic
  naming the file (and line/column where available) rather than throwing, and
  writes nothing to output

#### Scenario: Unknown or missing kind reported by the loader

- **WHEN** a document has a `kind` that is missing or not one of `Standard`,
  `Control`, `Requirement`, `Asset`, `Scope`, `Integration`, or `Collector`
- **THEN** the loader returns a diagnostic naming the document and the bad `kind`,
  does not throw, and does not deserialize that document further

#### Scenario: Retired RequirementScope and VendorScope kinds are now unknown

- **WHEN** a document authors `kind: RequirementScope` or `kind: VendorScope`, the
  pre-generalization wire tokens
- **THEN** the loader loads no scope from it and returns an unknown-kind diagnostic naming
  the document and the bad `kind`; the diagnostic's valid-kinds enumeration lists `Scope`
  and does NOT contain `RequirementScope` or `VendorScope`

#### Scenario: Retired EvidenceCollector and AttestationTemplate kinds are now unknown

- **WHEN** a document authors `kind: EvidenceCollector` or `kind: AttestationTemplate`,
  the pre-merge wire tokens
- **THEN** the loader loads no collector from it and returns an unknown-kind diagnostic
  naming the document and the bad `kind`; the diagnostic's valid-kinds enumeration lists
  `Collector` and does NOT contain `EvidenceCollector` or `AttestationTemplate`

#### Scenario: Collector config that is not a mapping returns a diagnostic

- **WHEN** a `Collector` document authors `config` as a scalar or as a sequence rather
  than as a mapping
- **THEN** the loader returns a diagnostic naming the document, loads no collector from
  it, emits no per-key unknown-config-key diagnostic, does not throw, and still loads the
  other documents in the directory

#### Scenario: A config list authored as a scalar returns a diagnostic

- **WHEN** a `Collector` document authors `config.fields`, `config.quiz`, or
  `config.checks` as a scalar or a mapping rather than as a list
- **THEN** the loader returns a diagnostic naming the document, loads no collector from
  it, does not throw, and still loads the other documents in the directory

#### Scenario: An explicit-null config loads as an empty config

- **WHEN** a `Collector` document authors `config` with no value, or authors a `config`
  `fields`, `quiz`, or `checks` key with no value
- **THEN** the loader loads the collector with an empty config, or with that list empty,
  returns no diagnostic for it, and does not throw

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds credential material (a token,
key, password, or equivalent). Credentials needed by integrations SHALL be
referenced by identity and resolved out-of-band, never inlined in git-tracked
config. The `Collector.config` map is git-tracked type-specific settings whose keys are
closed by the schema registered for the collector's `(type, provider)` pair; no registered
key SHALL hold credential material, and because an unregistered key is rejected, a
credential cannot be smuggled into `config` under an ad-hoc name. A collector that needs a
credential names it for out-of-band resolution. An `Integration` names its provider, base
URL, and discovery cadence in git, but SHALL NOT carry its API token: the token is
resolved out-of-band at runtime, keyed by the connection id, and is never authored in
config. A `manual` or `training` collector's `body`, `fields`, and `quiz` config values are
git-tracked form and quiz content and SHALL NOT inline credential material. A training
quiz's correct `answer` is not credential material: it is confidential authoring data that
MAY be stored in git-tracked config (the later grading runtime needs it) but MUST be
redacted from every broad read surface (the read API, CLI, and web register).

#### Scenario: No credential fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Asset`, `Scope`,
  `Integration`, and `Collector` is inspected, including every registered
  `(type, provider)` config key
- **THEN** it contains no field or config key intended to hold credential material, and an
  `Integration` in particular declares no token field

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
`Asset.parent` gives an empty value). Required fields (`Standard.version`,
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

### Requirement: Config-format documentation covers every supported kind

The GitOps config-format documentation (`docs/gitops.md`) SHALL document every
kind the loader and validator support. For each kind it SHALL state the schema
fields, at least one example document, and the validation rules, including the
referential-integrity rules (which fields reference which other kind by id and
that a reference to an absent id is rejected). This SHALL include
Integration (an optional `vendor` reference by id, its out-of-band token
resolved by id, never in config) and Collector (references a control by id
and, optionally, a vendor by id, and, when `type: integration`, a `provider` token plus a
connection by id and the `config` `checks` list its registered schema requires), so the
documented surface matches the shipped
`GitOpsSchema` kind set. The documentation SHALL also state, for each `(type, provider)`
pair, which `config` keys are accepted and which are required, and that any other key is
rejected. The supported-kinds list, the noun-mapping table, and every example SHALL carry
`Collector` and SHALL NOT mention `EvidenceCollector` or `AttestationTemplate`.

#### Scenario: Integration documented with its schema and token rule

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the Integration kind, its `provider`, `base_url`,
  and `discovery_cadence` fields and optional `vendor` reference, and states that the
  API token is never authored in config but resolved out-of-band by connection id

#### Scenario: Collector documented with its provider, connection, and checks

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the Collector kind, its `control` (required) and
  `vendor` (optional) references, its top-level `provider` and `connection` (both required
  for `type: integration`) and the `config` `checks` its registered `(integration, fleet)`
  schema requires, and states that a `control`,
  `vendor`, or `connection` naming an id that no document defines is rejected as a
  validation error

#### Scenario: Config keys documented per type and provider

- **WHEN** a reader consults the Collector section of `docs/gitops.md`
- **THEN** it lists, for each `(type, provider)` pair the system registers, the accepted
  `config` keys and which of them are required, states that any other key is rejected,
  states that no type-specific payload is authored at the top level, and shows an
  `integration` example carrying its `checks` under `config` alongside a `manual` and a
  `training` example carrying the form under `config`

#### Scenario: Supported-kind list and noun table are complete

- **WHEN** a reader consults the supported-kinds list and the noun mapping table
  in `docs/gitops.md`
- **THEN** the supported-kinds list includes `Collector` and `Integration` alongside the
  existing kinds and omits the retired `EvidenceCollector` and `AttestationTemplate`, and
  the noun-mapping table carries one `collectors` row in place of the separate
  `evidence-collectors` and `attestation-templates` rows while the
  `integration-connections` row is unchanged

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
The `Scope.subject` and `Collector.vendor` references SHALL name the matching
asset: `Scope.subject` names any asset (a Company/Department, a Machine, or a Vendor; a Vendor
subject may not target a standard), and `Collector.vendor` names a `Vendor` asset. The
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

### Requirement: Asset validation

The system SHALL validate assets and report every error as a structured
diagnostic. Validation SHALL fail (an `Error` diagnostic) when any of the
following hold: an `Asset` is missing or blank on `id` or `title`; an `Asset.type`
is not one of `Company`, `Department`, `Machine`, or `Vendor`; an `Asset.source`
is not `declared` or `discovered`; a declared config authors `source: discovered`; a
declared config authors any discovered-only field (`identity_kind`, `identity_value`,
`state`, `first_seen`, `last_seen`), which is ingest-written and never authored;
an `Asset` carries both `parent` and `owner`; an `Asset.parent` names an asset that
is not a `Company` or `Department`; an `Asset.owner` names an asset that is not a
`Company` or `Department`; a `parent` is carried by an asset that is not a
`Company`, `Department`, or `Machine`; an `owner` is carried by an asset that is not
a `Vendor`; an unknown field is present; or an `Asset` id is duplicated. Authoring a
discovered-only field is a distinct error from authoring `source: discovered`: the
first names the offending field, the second names the source.

A `parent` or `owner` that names an id absent from the resolved asset set (a
dangling reference) SHALL NOT be an error: it SHALL be reported as a NON-BLOCKING
`Warning` diagnostic that does not fail `validate`, `apply`, or `sync`. A `parent`
cycle among declared assets SHALL likewise be tolerated (a warning, not an error),
because resolution walks are cycle-guarded. A missing required edge - a declared
`Vendor` with no `owner`, or a `Machine` with no `parent` - SHALL also be a
NON-BLOCKING `Warning`, not an error, because such an asset is invisible under the
fail-closed read model; a `Company` or `Department` with no `parent` is a legitimate
root and SHALL NOT warn.

#### Scenario: Unknown type rejected

- **WHEN** an `Asset` declares a `type` other than `Company`, `Department`,
  `Machine`, or `Vendor`
- **THEN** validation fails, naming the asset and the bad type

#### Scenario: Authoring a discovered asset in config is rejected

- **WHEN** a config document declares `kind: Asset` with `source: discovered`
- **THEN** validation fails, naming the asset, because ingest is the only writer of
  discovered assets

#### Scenario: Authoring a discovered-only field in config is rejected

- **WHEN** a `kind: Asset` document authors a discovered-only field (`identity_kind`,
  `identity_value`, `state`, `first_seen`, or `last_seen`)
- **THEN** validation fails with an `Error` naming the asset and the discovered-only
  field, distinct from the `source: discovered` rejection, because those fields are
  written only by ingest

#### Scenario: Parent and owner are mutually exclusive

- **WHEN** an `Asset` declares both `parent` and `owner`
- **THEN** validation fails, naming the asset

#### Scenario: Parent target must be Company or Department

- **WHEN** an `Asset.parent` names an asset that is not a `Company` or `Department`
- **THEN** validation fails, naming the asset and the invalid parent target

#### Scenario: Vendor owner target must be Company or Department

- **WHEN** a `Vendor` asset's `owner` names an asset that is not a `Company` or
  `Department`
- **THEN** validation fails, naming the vendor and the invalid owner target

#### Scenario: Dangling parent or owner is a non-blocking warning

- **WHEN** an `Asset.parent` or `Asset.owner` names an id that no asset defines
- **THEN** a non-blocking `Warning` diagnostic names the dangling reference and
  validation does not fail on it

#### Scenario: Missing required edge is a non-blocking warning

- **WHEN** a declared `Vendor` carries no `owner`, or a `Machine` carries no
  `parent`
- **THEN** a non-blocking `Warning` diagnostic names the asset as unreachable and
  validation does not fail on it, while a parent-less `Company` or `Department`
  produces no diagnostic

### Requirement: Integration authorship

The system SHALL support an `Integration` kind that defines an
integration's connection: one base URL and one discovery cadence, backing an
integration's discovery and its per-control collectors. `Integration` is the seventh
declared kind of the object model, alongside `Standard`, `Requirement`, `Control`,
`Asset`, `Scope`, and `Collector`. An `Integration`
has an `id` (its permanent identity), a `title` (display only), a required
`provider`, a required `base_url`, a required `discovery_cadence`, and an optional
`vendor` (a `Vendor` id linking the connection to a vendor record).

`provider` SHALL be drawn from a single, closed, case-sensitive provider token set
whose only value in this increment is `fleet`. That one closed set governs exactly three
things: it validates `Integration.provider`, it validates the `provider` a `Collector` of
`type: integration` authors, and it selects the runner for an
integration `Collector` that names this connection. There is no separate
provider token set for collectors. `provider` is distinct from `vendor` and is NOT
unique - one provider MAY back many connections; identity is the `id`.

The closed set does NOT govern a machine's `asset_source.source` broadly.
`asset_source.source` accepts any nonblank token (validated only as nonblank, up to 64
characters) and is NOT checked against `IntegrationProvider.Tokens`; a machine reported
by some other source carries whatever source token that source emits. The tie to
`provider` is narrower and forward-looking: when the future integration runner writes a
machine observation discovered through this connection, it SHALL write the exact
`Integration.provider` token as that observation's `asset_source.source`. That equality
is a contract for the integration runner, not a runtime validation of every
`asset_source.source`. Because a machine's source attachment is keyed by
`(organisation_id, source, external_id)` and carries no connection id, a machine does
not resolve to one connection when several connections share a provider;
connection-level disambiguation is future work. `base_url` SHALL be an absolute
`http`/`https` URL (the same
URL rule as `Requirement.citation_url` and `Standard.source_url`).
`discovery_cadence` SHALL be one of the collection-cadence tokens `continuous`,
`daily`, `weekly`, `monthly`, `quarterly`, or `annual` (the same set a
`Collector.frequency` uses). The connection SHALL NOT carry an API token or
any other credential field; its token is resolved out-of-band at runtime, keyed by
the connection id.

#### Scenario: Integration loads with provider, base URL, and cadence

- **WHEN** an `Integration` document names an `id`, a `title`, a `provider`
  of `fleet`, an absolute `http`/`https` `base_url`, and a `discovery_cadence`
- **THEN** it loads as an `Integration` with those fields populated and its
  optional `vendor` populated when present, and it declares no token field

#### Scenario: Integration links to a vendor

- **WHEN** an `Integration` names a `vendor` that a `Vendor` document defines
- **THEN** it loads with that vendor link, distinct from its `provider`

### Requirement: Integration validation

The system SHALL validate integrations and report every error as a
structured diagnostic, consistent with the rest of config validation. Validation
SHALL fail when any of the following hold: an `Integration` is missing or
blank on `id`, `title`, `provider`, `base_url`, or `discovery_cadence`; an
`Integration` id is duplicated within its kind; an unknown field is present
on an `Integration`; an `Integration.provider` is not the token
`fleet`; an `Integration.base_url` is not a well-formed absolute
`http`/`https` URL; an `Integration.discovery_cadence` is not one of
`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or `annual`; an
`Integration.id` contains a `:` character or a `__` sequence, or two
`Integration` ids collide case-insensitively; or an
`Integration.vendor` is present but references a `Vendor` id that no document
defines. An omitted `vendor` is treated as absent and does NOT fail validation.

The `id` rules exist because the connection id is interpolated into the out-of-band
token configuration key `Freeboard:Integrations:<id>:ApiToken`, and .NET configuration
keys are case-insensitive and `:`-delimited (the environment-variable provider maps `__`
to `:`). An id containing `:` or `__`, or two ids that differ only in case, would resolve
an ambiguous or wrong token. These id constraints apply only to the
`Integration` kind, because only its id resolves a secret.

#### Scenario: Integration missing a required field

- **WHEN** an `Integration` document omits its `provider`, `base_url`, or
  `discovery_cadence`
- **THEN** validation fails and the error list names the connection and the missing
  field

#### Scenario: Integration unknown provider rejected

- **WHEN** an `Integration` declares a `provider` other than `fleet`
- **THEN** validation fails and the error list names the connection and the bad
  provider

#### Scenario: Integration malformed base URL rejected

- **WHEN** an `Integration.base_url` is not a well-formed absolute
  `http`/`https` URL
- **THEN** validation fails and the error list names the connection and the malformed
  base URL

#### Scenario: Integration unknown cadence rejected

- **WHEN** an `Integration.discovery_cadence` is outside the cadence set
- **THEN** validation fails and the error list names the connection and the bad cadence

#### Scenario: Integration references an unknown vendor

- **WHEN** an `Integration` names a `vendor` id that no `Vendor` document
  defines
- **THEN** validation fails and the error list names the connection and the unknown
  vendor reference

#### Scenario: Duplicate connection id rejected

- **WHEN** two `Integration` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: Connection id that is an unsafe configuration-key segment rejected

- **WHEN** an `Integration.id` contains a `:` character or a `__` sequence, or
  two `Integration` ids differ only in letter case
- **THEN** validation fails and the error list names the connection and the unsafe or
  colliding id, because the id would resolve an ambiguous or wrong out-of-band token

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

### Requirement: Collector authorship and Control evaluation rule

The system SHALL support a `Collector` kind that attaches a proving mechanism to one
`Control`. A `Collector` has an `id` (its permanent identity), a `title`
(display only), a `control` (the `Control` id it attaches to), an optional `vendor`
(a `Vendor`-type `Asset` id), a `type` that is exactly one of `integration`, `script`,
`agent`, `manual`, or `training`, a `frequency` that is a
collection cadence (`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or
`annual`), an optional `threshold` (an integer percent from 0 to 100 giving the share
of the collector's checks that must pass), and an optional `config` (a map of
type-specific settings whose keys are closed by the registered `(type, provider)` schema).
A control MAY have several collectors; identity is keyed on
`id` only. The collector-to-control-to-requirement path holds transitively: a valid
`Control` always carries a non-empty `maps_to` of existing requirement ids, so a
collector that resolves to a control resolves to at least one requirement.

This kind replaces the retired `EvidenceCollector` and `AttestationTemplate` kinds. An
attestation is a `Collector` of `type: manual` or `type: training` that carries its form or
quiz in `config`: there is no separate template kind and no separate attach point. The
retired `manual-attestation` and `training-attestation` type tokens are replaced by
`manual` and `training`.

A `Collector` of `type: integration` SHALL additionally name a top-level `provider` and a
top-level `connection` (an `Integration` id, the connection that backs the collector), and
SHALL author its tracked checks as a `checks` list inside its `config`, which the schema
registered for its `(type, provider)` pair is what requires - for the one pair this
increment registers, `(integration, fleet)`, `checks` is required and must be non-empty.
The `provider` is drawn from
the single, shared provider token set (only
`fleet` in this increment), the same set an `Integration` authors, and SHALL equal the
`provider` of the `Integration` the collector's `connection` names. That cross-check SHALL
NOT run when the named `Integration` declares no `provider` at all: the connection is already
reported as missing a required field, and a disagreement naming its empty value would report
one authoring mistake twice. It DOES run when the connection's `provider` is present but
outside the token set, because the two documents then genuinely name different providers and
repairing only the collector would leave the connection's unknown token in place. Each item in `checks`
has a `source_key` (the provider-native id, for example a Fleet policy id, the key that
joins a provider result to a Freeboard check), a `name` (the Freeboard check name), and a
`severity` that is exactly one of `Hard` or `Soft` (matching an evidence check's severity:
a failing `Hard` check fails the requirement, a failing `Soft` check warns). The
`provider` and the `connection` SHALL each be absent on any collector whose `type` is not
`integration`, and `checks` SHALL NOT be a registered `config` key for any pair whose
`type` is not `integration`.

`checks` is authored inside `config` rather than at the top level because a check's
`source_key` is a provider-native id by definition, which makes the check list the
type-and-provider-specific payload the `(type, provider)` schema exists to close. This is
the same rule that puts a `manual` or `training` collector's form inside `config`; the
`connection` stays top-level because it is a cross-document reference, not payload.

The authored `checks` list SHALL be the exhaustive set of checks tracked for an
integration collector: a provider-native id (`source_key`) that is not present in the
authored list SHALL NOT be a tracked check and SHALL NOT contribute to the collector's
results. A provider result whose `source_key` is not authored is ignored, not
discovered as a new check.

The system SHALL extend the `Control` kind with an optional `evaluation` rule that is
exactly one of `all`, `any`, or `manual`: `all` means the control is satisfied only
if every attached collector is satisfied; `any` means it is satisfied if at least one
attached collector is satisfied; `manual` means a human sets the control status and
collectors are advisory. `evaluation` is optional on a control that has no attached
collectors; it is REQUIRED on a control that has at least one attached collector, of any
type.

#### Scenario: Collector loads attached to a control

- **WHEN** a `Collector` document names an `id`, a `title`, a `control`, a
  `type`, and a `frequency`
- **THEN** it loads as a `Collector` bound to that control with that type
  and frequency, and its optional `vendor`, `threshold`, and `config` populated when
  present

#### Scenario: Integration collector loads with a provider, connection, and checks

- **WHEN** a `Collector` document declares `type: integration`, a `provider` matching the
  provider of the `Integration` its `connection` names, and a `config` carrying a non-empty
  `checks` list whose items each name a `source_key`, a `name`, and a `severity` of `Hard`
  or `Soft`
- **THEN** it loads as an integration `Collector` bound to that connection
  with its provider populated and its checks populated under `config` in author order

#### Scenario: Manual collector loads with its form in config

- **WHEN** a `Collector` document declares `type: manual` and a `config` carrying an
  optional `body` and a `fields` list whose items each name an `id`, a `label`, and a
  `type`
- **THEN** it loads as a manual `Collector` with its body and ordered fields populated
  under `config`, and with no `provider`, no `connection`, and no `config` `checks`

#### Scenario: Training collector loads with its quiz and pass mark in config

- **WHEN** a `Collector` document declares `type: training` and a `config` carrying a
  `pass_mark` and a non-empty `quiz` whose items each name a `prompt`, `options`, and a
  matching `answer`
- **THEN** it loads as a training `Collector` with its quiz items and pass mark populated
  under `config`

#### Scenario: Single-choice field loads with its options

- **WHEN** a `manual` or `training` collector's `config` `fields` item declares
  `type: single-choice` with an `options` list of two or more unique labels
- **THEN** the field loads with its options as the offered choices

#### Scenario: Provider id absent from the authored checks is not tracked

- **WHEN** an integration collector's `config` `checks` list authors a fixed set of
  `source_key` values and a provider reports a result for a `source_key` not in that list
- **THEN** the unlisted `source_key` is not a tracked check of the collector and does
  not contribute to its results; the tracked set is exactly the authored `checks`

#### Scenario: Control loads with an evaluation rule

- **WHEN** a `Control` document names an `evaluation` of `all`, `any`, or `manual`
- **THEN** it loads with that evaluation rule alongside its `maps_to`

### Requirement: Typed Collector config schema per type and provider

The system SHALL register, for each supported `(type, provider)` pair, a schema naming the
`config` keys that pair accepts and which of them are required. There SHALL be no
uniformly free-form `config`: a `config` key that the pair's registered schema does not
name SHALL fail validation with a diagnostic naming the collector and the unknown key, and
a required key the schema names but the document omits SHALL fail validation with a
diagnostic naming the collector and the missing key. For a collector whose `type` is not
`integration` the schema key's provider component is the absent provider.

The registered schemas for this increment SHALL be:

- `(manual, -)`: `body` optional, `fields` optional.
- `(training, -)`: `body` optional, `fields` optional, `pass_mark` required, `quiz`
  required.
- `(integration, fleet)`: `checks` required and non-empty.
- `(script, -)` and `(agent, -)`: the empty schema - no `config` key is accepted, so any
  `config` key on such a collector is rejected.

`body` and `fields` are registered for BOTH attestation types and are optional on both,
and `pass_mark` and `quiz` are registered for `training` alone. This is the pre-merge
`AttestationTemplate` rule expressed as a key set: a template's `body`, `fields`, `quiz`,
and `pass_mark` were all optional, the form-field rules were enforced on whichever type
declared `fields`, and only `pass_mark` and `quiz` were type-conditional (required on
`training`, forbidden on `manual`). Registering `fields` as required on `manual`, or
omitting it from `training`, would change what validates and break that parity.

Requiredness SHALL be evaluated against the VALUE the key carries, not against the key's
presence in the document. A required key SHALL be satisfied only by a value that is
non-empty: a non-empty list where the key's value is a list, and a non-blank scalar where it
is a scalar. An authored empty list, an authored blank scalar, and an authored null all
count as ABSENT. That keeps parity with the pre-merge value rules, under which a `training`
template declaring `quiz: []` fails for a missing quiz and one declaring `pass_mark: ""` or
a null-valued `pass_mark` fails for a missing pass mark. Testing presence alone would change
both of those verdicts, and would additionally skip the `pass_mark` range check, which is
guarded by the same blank test.

An UNREGISTERED key SHALL be rejected whatever its value, including an empty
list or a blank scalar - this side is evaluated on the authored document, because a key with
an empty value is indistinguishable from an absent one once parsed. That is one deliberate
difference from the pre-merge rules, which
tested the value rather than the key and so accepted a `manual` template declaring
`quiz: []` or a blank `pass_mark`; those authored keys had no effect then and are an
authoring mistake now, so naming them is the better diagnostic. Nothing that carried meaning
changes verdict.

When no schema resolves for a collector's `(type, provider)` pair, the system SHALL report
only the token at fault, NOT an unknown-key diagnostic for each authored `config` key and NOT
a missing-required-key diagnostic, so one authoring mistake yields one diagnostic rather than
a cascade. No schema resolves in three cases: the `type` is outside its token set, the
`provider` is outside its token set, and a `type: integration` collector omits `provider`
altogether. The third is included deliberately: which keys a pair accepts and requires is a
property of the PAIR, so with no provider there is no requiredness to report that would not be
one provider's key set applied unconditionally to `type: integration`. Such a collector is
reported as missing its required `provider` and, once that is supplied, is checked against the
resolved pair's required keys. It is still REJECTED in both passes, so no document changes
verdict; only the diagnostic set differs.

A `provider` authored on a type that cannot carry one is NOT such a case. The schema SHALL
resolve on the `type` alone, so the collector's required-key and unknown-key diagnostics are
reported alongside the stray-`provider` one. Off the integration path a `provider` is forbidden
outright and so selects no key set; the type determines the key set by itself, and deleting the
stray token - the only repair available - would not change which schema applies.

Registering `checks` under `(integration, fleet)` is what makes the schema key's provider
component load-bearing in this increment: a check's `source_key` is a provider-native id,
so a second provider would register a differently shaped check list or a different key set
entirely. No further `(integration, fleet)` key is registered in this increment.

Unknown-key rejection applies to the top-level keys of the `config` map itself. It SHALL
NOT inspect keys inside a nested `fields`, `quiz`, or `checks` ITEM, matching the pre-merge
`AttestationTemplate` carve-out; a nested item's REQUIRED keys are still enforced by the
collector validation rules, so the only unchecked case is an extra unknown key inside a
nested item, which is ignored.

#### Scenario: Unknown config key rejected

- **WHEN** a `Collector` authors a `config` key that its `(type, provider)` schema does not
  name
- **THEN** validation fails and the error list names the collector and the unknown config
  key

#### Scenario: Missing required config key rejected

- **WHEN** a `Collector` of `type: training` omits `pass_mark` or `quiz` from its `config`
- **THEN** validation fails and the error list names the collector and the missing config
  key

#### Scenario: Integration collector missing its required config checks rejected

- **WHEN** a `Collector` of `type: integration` with `provider: fleet` omits `config`
  entirely, omits the `config` `checks` key, or declares it as an empty list
- **THEN** validation fails and the error list names the collector and the missing or empty
  `checks`

#### Scenario: An empty authored list is absent for a required key and still present for an unregistered one

- **WHEN** a `Collector` of `type: training` authors `quiz: []` in its `config`, and a
  `Collector` of `type: manual` also authors `quiz: []`
- **THEN** the training collector fails for a missing required `quiz`, because an empty
  list counts as absent, and the manual collector fails for an unknown `quiz` key, because
  an unregistered key is rejected whatever its value

#### Scenario: A blank required scalar is a missing required key

- **WHEN** a `Collector` of `type: training` authors `pass_mark` in its `config` with a blank
  value or with no value at all, alongside a valid `quiz`
- **THEN** validation fails for a missing required `pass_mark`, not for an out-of-range one
  and not silently, because requiredness is evaluated on the value rather than on the key

#### Scenario: A key valid for one type is rejected on another

- **WHEN** a `Collector` of `type: manual` authors `pass_mark` or `quiz` in its `config`
- **THEN** validation fails and the error list names the collector and the key, because the
  `manual` schema does not register it

#### Scenario: config checks on a non-integration collector rejected

- **WHEN** a `Collector` of `type: manual`, `training`, `script`, or `agent` authors a
  `checks` key in its `config`
- **THEN** validation fails and the error list names the collector and the `checks` key,
  because no non-integration pair registers it

#### Scenario: Empty-schema types accept no config key

- **WHEN** a `Collector` of `type: script` or `type: agent` authors any `config` key
- **THEN** validation fails and the error list names the collector and the rejected key

#### Scenario: Omitted config is valid where the schema requires nothing

- **WHEN** a `Collector` of `type: script`, `type: agent`, or `type: manual` omits `config`
  entirely, or a `manual` collector authors a `config` carrying neither `body` nor `fields`
- **THEN** validation reports no config diagnostic for it, because the `(manual, -)` schema
  names no required key

#### Scenario: Fields are accepted on a training collector

- **WHEN** a `Collector` of `type: training` authors `fields` in its `config` alongside its
  `pass_mark` and `quiz`
- **THEN** validation accepts the `fields` key and applies the same form-field rules a
  `manual` collector's `fields` receives, because `fields` is registered for both
  attestation types

#### Scenario: An unknown type does not cascade config diagnostics

- **WHEN** a `Collector` declares a `type` outside the token set and also authors several
  `config` keys
- **THEN** validation fails naming the bad `type` and reports no per-key unknown-config-key
  diagnostic, because no schema resolves for that pair

#### Scenario: An absent provider does not cascade config diagnostics

- **WHEN** a `Collector` of `type: integration` omits `provider` and authors an unregistered
  `config` key, or omits the `checks` key its provider's schema would require
- **THEN** validation fails naming the missing required `provider` and reports neither an
  unknown-config-key nor a missing-required-config-key diagnostic, because no schema resolves
  without the provider component of the pair

#### Scenario: A stray provider does not suppress config diagnostics

- **WHEN** a `Collector` of `type: training` declares a `provider`, omits its required
  `pass_mark`, and authors an unregistered `config` key
- **THEN** validation fails naming the stray `provider`, the missing required `pass_mark`, and
  the unknown config key, because a provider the type cannot carry selects no key set and the
  schema resolves on the `type` alone

#### Scenario: Unknown keys inside a nested field, quiz, or check item are ignored

- **WHEN** a `manual` collector's `config` `fields` item, a `training` collector's
  `config` `quiz` item, or an `integration` collector's `config` `checks` item carries an
  extra key beyond its defined ones
- **THEN** validation does not fail for that nested key, while the item's required keys are
  still enforced

### Requirement: Collector and Control evaluation validation

The system SHALL validate collectors and the control evaluation rule and
report every error as a structured diagnostic, consistent with the rest of config
validation. Validation SHALL fail when any of the following hold: a
`Collector` is missing or blank on `id`, `title`, `control`, `type`, or
`frequency`; a `Collector` id is duplicated within its kind; an unknown
top-level field is present on a `Collector`; a `Collector.type` is not one of
`integration`, `script`, `agent`, `manual`, or `training`; a
`Collector.frequency` is not one of `continuous`, `daily`, `weekly`,
`monthly`, `quarterly`, or `annual`; a `Collector.threshold` is present but
is not an integer from 0 to 100; a `Collector.control` references a `Control`
id that no document defines; a `Collector.vendor` is present but references a
`Vendor` asset id that no document defines; a `Collector` of `type: integration` is
missing its `provider`, names a `provider` outside the shared provider token set, or names
a `provider` that differs from the `provider` of the `Integration` its `connection` names;
a `Collector` of `type: integration` is missing its `connection` or names a `connection`
id that no `Integration` document defines; a `Collector` whose `type` is not `integration`
names a non-empty `provider` or a non-empty `connection`; a `config` `checks` item is
missing or blank on `source_key`, `name`, or `severity`; a `checks` item's `severity` is
not `Hard` or `Soft`; two `checks` items in one collector share a `name` or share a
`source_key`; a `Control.evaluation` is present but is not one of `all`, `any`, or
`manual`; or a `Control` has at least one attached `Collector` but has no `evaluation`
rule. A collector's `vendor`, `threshold`, and `config` that are omitted are treated as
absent and do NOT fail validation, subject to the registered config schema's required keys.

Which `config` keys a collector must, may, and may not carry is NOT restated here: it is
owned in full by the registered `(type, provider)` schema (see the typed config-schema
requirement). So a `type: integration` collector with a missing or empty `checks` list fails
because `(integration, fleet)` registers `checks` as required and requiredness is evaluated
on the value, and a `checks` key on any other type fails because no other pair registers it.
Restating either rule here would hard-code, against a `type` alone, a decision the
`(type, provider)` key exists to let a second provider make differently.

For a `manual` or `training` collector the system SHALL additionally validate the form
carried in `config`, with the same rules the retired `AttestationTemplate` kind enforced.
Validation SHALL fail when any of the following hold: a `fields` item is missing or blank
on `id`, `label`, or `type`; two `fields` items in one collector share an `id`; a `fields`
item's `type` is not one of `boolean`, `single-choice`, or `short-text`; a `single-choice`
field has fewer than two `options`; a `single-choice` field has two `options` that share a
label; a `boolean` or `short-text` field declares a non-empty `options` list; a `quiz` item
is missing or blank on `id`, `prompt`, or `answer`; two `quiz` items in one collector share
an `id`; a `quiz` item has fewer than two `options`; a `quiz` item has two `options` that
share a label; a `quiz` item's `answer` is not one of its `options`; or a `pass_mark` is
present but is not an integer from 0 to 100. A `body` is optional and free text.

#### Scenario: Collector missing a required field

- **WHEN** a `Collector` document omits its `control`, `type`, or `frequency`
- **THEN** validation fails and the error list names the collector and the missing
  field

#### Scenario: Collector unknown type rejected

- **WHEN** a `Collector` declares a `type` other than `integration`,
  `script`, `agent`, `manual`, or `training`
- **THEN** validation fails and the error list names the collector and the bad `type`

#### Scenario: Retired attestation type tokens rejected

- **WHEN** a `Collector` declares `type: manual-attestation` or
  `type: training-attestation`, the pre-merge tokens
- **THEN** validation fails and the error list names the collector and the bad `type`

#### Scenario: Collector unknown frequency rejected

- **WHEN** a `Collector` declares a `frequency` outside the cadence set
- **THEN** validation fails and the error list names the collector and the bad
  `frequency`

#### Scenario: Collector threshold out of range rejected

- **WHEN** a `Collector` declares a `threshold` that is not an integer from
  0 to 100
- **THEN** validation fails and the error list names the collector and the bad
  `threshold`

#### Scenario: Collector references an unknown control or vendor

- **WHEN** a `Collector` names a `control` id, or a `vendor` id, that no
  document defines
- **THEN** validation fails and the error list names the collector and the unknown
  reference

#### Scenario: Integration collector missing or dangling connection rejected

- **WHEN** a `Collector` of `type: integration` omits its `connection` or
  names a `connection` id that no `Integration` document defines
- **THEN** validation fails and the error list names the collector and the missing or
  unknown connection

#### Scenario: Integration collector missing provider rejected

- **WHEN** a `Collector` of `type: integration` omits its `provider` or names a `provider`
  outside the shared provider token set
- **THEN** validation fails and the error list names the collector and the missing or
  unknown provider

#### Scenario: Provider that disagrees with the connection rejected

- **WHEN** a `Collector` of `type: integration` names a `provider` that differs from the
  `provider` of the `Integration` its `connection` names
- **THEN** validation fails and the error list names the collector, its provider, and the
  connection's provider

#### Scenario: A connection with no provider is reported once, not also as a disagreement

- **WHEN** a `Collector` of `type: integration` names a `provider` and a `connection` whose
  `Integration` declares no `provider` at all
- **THEN** validation fails naming the connection's missing required `provider` and reports
  no provider-disagreement diagnostic for the collector

#### Scenario: Provider or connection on a non-integration collector rejected

- **WHEN** a `Collector` whose `type` is not `integration` names a `provider` or a
  `connection`
- **THEN** validation fails and the error list names the collector and the field that
  is only valid for an integration collector

#### Scenario: Check with an unknown severity rejected

- **WHEN** a `config` `checks` item declares a `severity` other than `Hard` or `Soft`
- **THEN** validation fails and the error list names the collector, the check, and the
  bad severity

#### Scenario: Duplicate check name or source key rejected

- **WHEN** two `config` `checks` items in one collector share a `name`, or share a
  `source_key`
- **THEN** validation fails and the error list names the collector and the duplicated
  value

#### Scenario: Duplicate collector id rejected

- **WHEN** two `Collector` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: Form field type outside the allowed set rejected

- **WHEN** a `manual` collector's `config` `fields` item declares a `type` other than
  `boolean`, `single-choice`, or `short-text`
- **THEN** validation fails and the error list names the collector, the field, and the
  bad `type`

#### Scenario: Single-choice field with fewer than two options rejected

- **WHEN** a `manual` collector's `config` `fields` item declares `type: single-choice`
  with no `options`, an empty list, or a single option
- **THEN** validation fails and the error list names the collector and the field

#### Scenario: Duplicate option labels rejected

- **WHEN** a `single-choice` field or a quiz item declares two `options` that share the
  same label
- **THEN** validation fails and the error list names the collector and the field or quiz
  item, so the value-based `answer` reference stays unambiguous

#### Scenario: Non-choice field with options rejected

- **WHEN** a `manual` collector's `config` `fields` item declares `type: boolean` or
  `type: short-text` but also declares a non-empty `options` list
- **THEN** validation fails and the error list names the collector and the field

#### Scenario: Quiz item answer not among its options rejected

- **WHEN** a `training` collector's `config` `quiz` item has an `answer` that is not equal
  to any of the item's `options`
- **THEN** validation fails and the error list names the collector and the quiz item

#### Scenario: pass_mark out of range rejected

- **WHEN** a `training` collector's `config` declares a `pass_mark` that is not an integer
  from 0 to 100
- **THEN** validation fails and the error list names the collector and the bad `pass_mark`

#### Scenario: Duplicate field or quiz id rejected

- **WHEN** two `fields` items, or two `quiz` items, in one collector's `config` share an
  `id`
- **THEN** validation fails and the error list names the collector and the duplicated id

#### Scenario: Control evaluation unknown value rejected

- **WHEN** a `Control` declares an `evaluation` other than `all`, `any`, or `manual`
- **THEN** validation fails and the error list names the control and the bad
  `evaluation`

#### Scenario: Control with collectors requires an evaluation rule

- **WHEN** a `Control` has at least one attached `Collector` but declares no
  `evaluation`
- **THEN** validation fails and the error list names the control and the missing
  `evaluation` rule

#### Scenario: Control proved only by an attestation requires an evaluation rule

- **WHEN** a `Control` has exactly one attached `Collector`, of `type: manual` or
  `type: training`, and declares no `evaluation`
- **THEN** validation fails and the error list names the control and the missing
  `evaluation` rule, because the rule applies to an attached collector of any type

#### Scenario: Control without collectors needs no evaluation rule

- **WHEN** a `Control` has no attached `Collector` and declares no `evaluation`
- **THEN** it validates without an evaluation error

