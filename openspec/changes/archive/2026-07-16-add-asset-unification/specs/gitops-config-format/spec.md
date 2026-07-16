## ADDED Requirements

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
The `Scope.organisation`, `RequirementScope.organisation`, `VendorScope.vendor`,
and `EvidenceCollector.vendor` references SHALL name the matching typed asset (a
`Company`/`Department` asset for an organisation reference, a `Vendor` asset for a
vendor reference).

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

## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as a
set of standards, the controls under each standard, the requirements published by
each standard, the assets being assessed (the organisation tree, the vendors in
use, and the discovered machines), the scopes that map an organisation asset to a
standard, the requirement-scopes that map an organisation asset to a requirement,
the vendor-scopes that record whether a requirement or control applies to a vendor
asset, the integration-connections that define an integration's base URL and
discovery cadence, the evidence-collectors that attach a data source to a control,
and the attestation-templates that describe an attestation form for a control. The
format SHALL be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`,
`VendorScope`, `IntegrationConnection`, `EvidenceCollector`, and
`AttestationTemplate`. Documents of different kinds MAY appear in any file. Every
resource SHALL have an immutable `id` that is its identity and a mutable `title`
for display. A `Standard` has an `id`, a `title`, required `version` and
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
has an `id`, a `title`, an `organisation` (a `Company`/`Department` asset id), a
`standard` (a `Standard` id), and a `disposition` (`In` or `Out`). A
`RequirementScope` has an `id`, a `title`, an `organisation` (a `Company`/
`Department` asset id), a `requirement` (a `Requirement` id), and a `disposition`
(`In` or `Out`); it has no `standard` field. A `VendorScope` has an `id`, a
`title`, a `vendor` (a `Vendor` asset id), exactly one of `requirement` (a
`Requirement` id) or `control` (a `Control` id), a `disposition` (`In` or `Out`),
and a `justification` (required when the disposition is `Out`). An
`IntegrationConnection` has an `id`, a `title`, a required `provider` (a closed
token set whose only value in this increment is `fleet`), a required `base_url` (an
absolute `http`/`https` URL), a required `discovery_cadence` (`continuous`,
`daily`, `weekly`, `monthly`, `quarterly`, or `annual`), and an optional `vendor`
(a `Vendor` asset id); its API token is never authored in config. An
`EvidenceCollector` has an `id`, a `title`, a `control` (a `Control` id), an
optional `vendor` (a `Vendor` asset id), a `type` (exactly one of `integration`,
`script`, `manual-attestation`, `training-attestation`, or `agent`), a `frequency`
(`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or `annual`), an optional
`threshold` (an integer percent from 0 to 100), an optional `config`, a
`connection` (an `IntegrationConnection` id, required when `type` is `integration`
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
  `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`, `VendorScope`,
  `IntegrationConnection`, `EvidenceCollector`, and `AttestationTemplate`
- **THEN** the loader returns a typed config model containing all standards,
  controls, requirements, assets (with `type`, `source`, and any `parent`/`owner`),
  scopes, requirement-scopes, vendor-scopes, integration-connections,
  evidence-collectors, and attestation-templates with their `id`, `title`, and
  reference fields populated and no errors

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
or Machine; an `owner` is carried by an asset that is not a Vendor; a
`Scope.organisation` references an id that is not a `Company`/`Department` asset; a
`Scope.standard` references a `Standard` id that does not exist; a
`RequirementScope.organisation` references an id that is not a `Company`/`Department`
asset; a `RequirementScope.requirement` references a `Requirement` id that does not
exist; a `RequirementScope` is missing its `organisation`, `requirement`, or
`disposition`; a `Scope.disposition` or `RequirementScope.disposition` is not `In`
or `Out`; a `Standard` is missing or blank on `version` or `authority`; a
`Standard.source_url` is present and non-empty but not a well-formed absolute
`http`/`https` URL; two Scopes share the same `(organisation, standard)` pair; two
RequirementScopes share the same `(organisation, requirement)` pair; the
`apiVersion` is not exactly `freeboard.dev/v1alpha1`. Optional fields that are
omitted or whitespace-only are treated as absent. A dangling `Asset.parent` or
`Asset.owner`, a `parent` cycle among assets, and a missing required edge (a
declared `Vendor` with no `owner` or a `Machine` with no `parent`) SHALL be reported
as NON-BLOCKING `Warning` diagnostics that do not fail validation, not as errors.
Unknown or missing `kind` is reported by the loader, not re-checked here.

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

#### Scenario: Dangling scope reference

- **WHEN** a `Scope` names an `organisation` or `standard` id that no document
  defines
- **THEN** validation fails and the error list names the scope and the unknown
  reference

#### Scenario: Dangling asset parent is a warning, not an error

- **WHEN** an `Asset.parent` names an id that no asset defines
- **THEN** the error list contains no error for it; a non-blocking warning names the
  dangling reference and validation still passes

#### Scenario: Duplicate scope mapping

- **WHEN** two `Scope` documents name the same `(organisation, standard)` pair
- **THEN** validation fails and the error list names the duplicated pair

#### Scenario: All errors reported

- **WHEN** a config has more than one validation error
- **THEN** the error list contains an entry for every error, not only the first

### Requirement: Loader and validator never throw or print

The loader and validator in `Freeboard.Core` SHALL return diagnostics as data and
SHALL NOT throw exceptions for malformed or invalid input, and SHALL NOT write to
any output stream. Callers decide how to present results and set exit codes.
Diagnostics carry a severity (`Error` or `Warning`); a config is valid when it has
no `Error` diagnostics, so a `Warning` (for example a dangling `parent`/`owner`)
does not fail loading or validation.

#### Scenario: Malformed input returns diagnostics

- **WHEN** a config file contains malformed YAML that the parser cannot read
- **THEN** the loader catches the parse error, returns a result with a diagnostic
  naming the file (and line/column where available) rather than throwing, and
  writes nothing to output

#### Scenario: Unknown or missing kind reported by the loader

- **WHEN** a document has a `kind` that is missing or not one of `Standard`,
  `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`, `VendorScope`,
  `IntegrationConnection`, `EvidenceCollector`, or `AttestationTemplate`
- **THEN** the loader returns a diagnostic naming the document and the bad `kind`,
  does not throw, and does not deserialize that document further

### Requirement: Vendor and VendorScope authorship

The system SHALL model a vendor as an `Asset` of `type: Vendor` (see the Asset
authoring requirement); there is no standalone `Vendor` document kind. A vendor
asset names a piece of software or a platform in use (for example Crowdstrike,
FleetDM, Google Workspace, an outsourced accountant) and MAY carry an `owner` (a
`Company`/`Department` asset) that drives its read-access.

The system SHALL support a `VendorScope` kind that records whether one `Requirement`
or one `Control` applies to one vendor asset, with an exception rationale. A
`VendorScope` has an `id`, a `title`, a `vendor` (a `Vendor` asset id), exactly one
of `requirement` (a `Requirement` id) or `control` (a `Control` id), a
`disposition` (`In` or `Out`), and a `justification`. The `disposition` reuses the
`Scope` disposition: `In` means the requirement or control applies to the vendor;
`Out` means it is excepted. A `VendorScope` is a flat per-`(vendor, target)`
statement and SHALL NOT carry an `organisation` field. At most one `VendorScope`
SHALL exist per `(vendor, requirement)` pair and at most one per `(vendor, control)`
pair.

#### Scenario: Vendor is authored as an Asset

- **WHEN** a `kind: Asset` document of `type: Vendor` names an `id` and a `title`
- **THEN** it loads as a vendor asset with that identity and display title

#### Scenario: VendorScope loads targeting a requirement

- **WHEN** a `VendorScope` document names a `vendor`, a `requirement`, a
  `disposition`, and (for `Out`) a `justification`
- **THEN** it loads as a `VendorScope` bound to that vendor asset and requirement
  with that disposition and justification

#### Scenario: VendorScope loads targeting a control

- **WHEN** a `VendorScope` document names a `vendor`, a `control`, a `disposition`,
  and (for `Out`) a `justification`
- **THEN** it loads as a `VendorScope` bound to that vendor asset and control, and no
  `requirement` field is expected on it

### Requirement: Vendor and VendorScope validation

The system SHALL validate vendor-scopes and report every error as a structured
diagnostic. Validation SHALL fail when any of the following hold: a `VendorScope`
is missing or blank on `id` or `title`; a `VendorScope` id is duplicated; an unknown
field is present on a `VendorScope`; a `VendorScope` is missing its `vendor` or
`disposition`; a `VendorScope` names neither `requirement` nor `control`, or names
both; a `VendorScope.vendor` references an id that is not a `Vendor` asset; a
`VendorScope.requirement` references a `Requirement` id that no document defines; a
`VendorScope.control` references a `Control` id that no document defines; a
`VendorScope.disposition` is not `In` or `Out`; two vendor-scopes share the same
`(vendor, requirement)` pair or the same `(vendor, control)` pair; or a `VendorScope`
whose disposition is `Out` has a missing or whitespace-only `justification`. A
`VendorScope` whose disposition is `In` SHALL NOT require a `justification`.

#### Scenario: VendorScope missing required field

- **WHEN** a `VendorScope` document omits its `vendor` or `disposition`
- **THEN** validation fails and the error list names the vendor-scope and the missing
  field

#### Scenario: VendorScope must name exactly one target

- **WHEN** a `VendorScope` names both `requirement` and `control`, or names neither
- **THEN** validation fails and the error list names the vendor-scope and the target
  problem

#### Scenario: VendorScope references an unknown vendor asset or target

- **WHEN** a `VendorScope` names a `vendor` that is not a `Vendor` asset, or a
  `requirement`/`control` id that no document defines
- **THEN** validation fails and the error list names the vendor-scope and the unknown
  reference

#### Scenario: Out disposition requires a justification

- **WHEN** a `VendorScope` declares `disposition: Out` with no `justification` (or a
  whitespace-only one)
- **THEN** validation fails and the error list names the vendor-scope and the missing
  justification

#### Scenario: In disposition does not require a justification

- **WHEN** a `VendorScope` declares `disposition: In` with no `justification`
- **THEN** it loads and validates, with `justification` absent
