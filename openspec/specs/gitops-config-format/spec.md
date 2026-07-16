# gitops-config-format Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
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

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds credential material (a token,
key, password, or equivalent). Credentials needed by integrations SHALL be
referenced by identity and resolved out-of-band, never inlined in git-tracked
config. The `EvidenceCollector.config` map is git-tracked type-specific settings and
SHALL NOT inline credential material; a collector that needs a credential names it
for out-of-band resolution. An `IntegrationConnection` names its provider, base URL,
and discovery cadence in git, but SHALL NOT carry its API token: the token is
resolved out-of-band at runtime, keyed by the connection id, and is never authored in
config. The `AttestationTemplate` `body`, `fields`, and `quiz` are git-tracked form
and quiz content and SHALL NOT inline credential material. A training quiz's correct
`answer` is not credential material: it is confidential authoring data that MAY be
stored in git-tracked config (the later grading runtime needs it) but MUST be redacted
from every broad read surface (the read API, CLI, and web register).

#### Scenario: No credential fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, `RequirementScope`, `Vendor`, `VendorScope`, `IntegrationConnection`,
  `EvidenceCollector`, and `AttestationTemplate` is inspected
- **THEN** it contains no field intended to hold credential material, and an
  `IntegrationConnection` in particular declares no token field

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

An `EvidenceCollector` of `type: integration` SHALL additionally name a `connection`
(an `IntegrationConnection` id, the connection that backs the collector) and a
`checks` list. Each item in `checks` has a `source_key` (the provider-native id, for
example a Fleet policy id, the key that joins a provider result to a Freeboard
check), a `name` (the Freeboard check name), and a `severity` that is exactly one of
`Hard` or `Soft` (matching an evidence check's severity: a failing `Hard` check fails
the requirement, a failing `Soft` check warns). The `connection` SHALL be empty and
the `checks` list SHALL be omitted on any collector whose `type` is not `integration`.

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
collectors; it is REQUIRED on a control that has at least one attached collector.

#### Scenario: EvidenceCollector loads attached to a control

- **WHEN** an `EvidenceCollector` document names an `id`, a `title`, a `control`, a
  `type`, and a `frequency`
- **THEN** it loads as an `EvidenceCollector` bound to that control with that type
  and frequency, and its optional `vendor`, `threshold`, and `config` populated when
  present

#### Scenario: Integration collector loads with a connection and checks

- **WHEN** an `EvidenceCollector` document declares `type: integration`, a
  `connection` naming a defined `IntegrationConnection` id, and a non-empty `checks`
  list whose items each name a `source_key`, a `name`, and a `severity` of `Hard` or
  `Soft`
- **THEN** it loads as an integration `EvidenceCollector` bound to that connection
  with its checks populated in author order

#### Scenario: Provider id absent from the authored checks is not tracked

- **WHEN** an integration collector's `checks` list authors a fixed set of `source_key`
  values and a provider reports a result for a `source_key` not in that list
- **THEN** the unlisted `source_key` is not a tracked check of the collector and does
  not contribute to its results; the tracked set is exactly the authored `checks`

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
`Vendor` id that no document defines; an `EvidenceCollector` of `type: integration` is
missing its `connection` or names a `connection` id that no `IntegrationConnection`
document defines; an `EvidenceCollector` of `type: integration` has an empty `checks`
list; an `EvidenceCollector` whose `type` is not `integration` names a non-empty
`connection` or a non-empty `checks` list; a `checks` item is missing or blank on
`source_key`, `name`, or `severity`; a `checks` item's `severity` is not `Hard` or
`Soft`; two `checks` items in one collector share a `name` or share a `source_key`; a
`Control.evaluation` is present but is not one of `all`, `any`, or `manual`; or a
`Control` has at least one attached `EvidenceCollector` but has no `evaluation` rule.
A collector's `vendor`, `threshold`, and `config` that are omitted are treated as
absent and do NOT fail validation.

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

#### Scenario: Integration collector missing or dangling connection rejected

- **WHEN** an `EvidenceCollector` of `type: integration` omits its `connection` or
  names a `connection` id that no `IntegrationConnection` document defines
- **THEN** validation fails and the error list names the collector and the missing or
  unknown connection

#### Scenario: Integration collector missing checks rejected

- **WHEN** an `EvidenceCollector` of `type: integration` declares no `checks` or an
  empty `checks` list
- **THEN** validation fails and the error list names the collector and the missing
  checks

#### Scenario: Connection or checks on a non-integration collector rejected

- **WHEN** an `EvidenceCollector` whose `type` is not `integration` names a
  `connection` or a non-empty `checks` list
- **THEN** validation fails and the error list names the collector and the field that
  is only valid for an integration collector

#### Scenario: Check with an unknown severity rejected

- **WHEN** a `checks` item declares a `severity` other than `Hard` or `Soft`
- **THEN** validation fails and the error list names the collector, the check, and the
  bad severity

#### Scenario: Duplicate check name or source key rejected

- **WHEN** two `checks` items in one collector share a `name`, or share a `source_key`
- **THEN** validation fails and the error list names the collector and the duplicated
  value

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

### Requirement: Config-format documentation covers every supported kind

The GitOps config-format documentation (`docs/gitops.md`) SHALL document every
kind the loader and validator support. For each kind it SHALL state the schema
fields, at least one example document, and the validation rules, including the
referential-integrity rules (which fields reference which other kind by id and
that a reference to an absent id is rejected). This SHALL include
IntegrationConnection (an optional `vendor` reference by id, its out-of-band token
resolved by id, never in config), EvidenceCollector (references a control by id
and, optionally, a vendor by id, and, when `type: integration`, a connection by id
plus its `checks` list) and AttestationTemplate (references a control by id), so the
documented surface matches the shipped `GitOpsSchema` kind set.

#### Scenario: IntegrationConnection documented with its schema and token rule

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the IntegrationConnection kind, its `provider`, `base_url`,
  and `discovery_cadence` fields and optional `vendor` reference, and states that the
  API token is never authored in config but resolved out-of-band by connection id

#### Scenario: EvidenceCollector documented with its connection and checks

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the EvidenceCollector kind, its `control` (required) and
  `vendor` (optional) references, its `connection` (required for `type: integration`)
  reference, and its `checks` list, and states that a `control`, `vendor`, or
  `connection` naming an id that no document defines is rejected as a validation error

#### Scenario: Supported-kind list and noun table are complete

- **WHEN** a reader consults the supported-kinds list and the noun mapping table
  in `docs/gitops.md`
- **THEN** both include IntegrationConnection alongside the existing kinds, so no
  shipped kind is omitted from the catalogue

### Requirement: IntegrationConnection authorship

The system SHALL support an `IntegrationConnection` kind that defines an
integration's connection: one base URL and one discovery cadence, backing an
integration's discovery and its per-control collectors. An `IntegrationConnection`
has an `id` (its permanent identity), a `title` (display only), a required
`provider`, a required `base_url`, a required `discovery_cadence`, and an optional
`vendor` (a `Vendor` id linking the connection to a vendor record).

`provider` SHALL be drawn from a closed, case-sensitive token set whose only value
in this increment is `fleet`; it selects the integration runner. `provider` is
distinct from `vendor` and is NOT unique - one provider MAY back many connections;
identity is the `id`. A connection's `provider` is the same token a machine reports as
its `asset_source.source`, so the alignment is at the provider level: a machine seen
through this provider aligns with the `provider` token, not with a specific connection
instance. Because a machine's source attachment is keyed by `(organisation_id, source,
external_id)` and carries no connection id, a machine does not resolve to one connection
when several connections share a provider; connection-level disambiguation is future
work. `base_url` SHALL be an absolute `http`/`https` URL (the same
URL rule as `Requirement.citation_url` and `Standard.source_url`).
`discovery_cadence` SHALL be one of the collection-cadence tokens `continuous`,
`daily`, `weekly`, `monthly`, `quarterly`, or `annual` (the same set an
`EvidenceCollector.frequency` uses). The connection SHALL NOT carry an API token or
any other credential field; its token is resolved out-of-band at runtime, keyed by
the connection id.

#### Scenario: IntegrationConnection loads with provider, base URL, and cadence

- **WHEN** an `IntegrationConnection` document names an `id`, a `title`, a `provider`
  of `fleet`, an absolute `http`/`https` `base_url`, and a `discovery_cadence`
- **THEN** it loads as an `IntegrationConnection` with those fields populated and its
  optional `vendor` populated when present, and it declares no token field

#### Scenario: IntegrationConnection links to a vendor

- **WHEN** an `IntegrationConnection` names a `vendor` that a `Vendor` document defines
- **THEN** it loads with that vendor link, distinct from its `provider`

### Requirement: IntegrationConnection validation

The system SHALL validate integration-connections and report every error as a
structured diagnostic, consistent with the rest of config validation. Validation
SHALL fail when any of the following hold: an `IntegrationConnection` is missing or
blank on `id`, `title`, `provider`, `base_url`, or `discovery_cadence`; an
`IntegrationConnection` id is duplicated within its kind; an unknown field is present
on an `IntegrationConnection`; an `IntegrationConnection.provider` is not the token
`fleet`; an `IntegrationConnection.base_url` is not a well-formed absolute
`http`/`https` URL; an `IntegrationConnection.discovery_cadence` is not one of
`continuous`, `daily`, `weekly`, `monthly`, `quarterly`, or `annual`; an
`IntegrationConnection.id` contains a `:` character or a `__` sequence, or two
`IntegrationConnection` ids collide case-insensitively; or an
`IntegrationConnection.vendor` is present but references a `Vendor` id that no document
defines. An omitted `vendor` is treated as absent and does NOT fail validation.

The `id` rules exist because the connection id is interpolated into the out-of-band
token configuration key `Freeboard:Integrations:<id>:ApiToken`, and .NET configuration
keys are case-insensitive and `:`-delimited (the environment-variable provider maps `__`
to `:`). An id containing `:` or `__`, or two ids that differ only in case, would resolve
an ambiguous or wrong token. These id constraints apply only to the
`IntegrationConnection` kind, because only its id resolves a secret.

#### Scenario: IntegrationConnection missing a required field

- **WHEN** an `IntegrationConnection` document omits its `provider`, `base_url`, or
  `discovery_cadence`
- **THEN** validation fails and the error list names the connection and the missing
  field

#### Scenario: IntegrationConnection unknown provider rejected

- **WHEN** an `IntegrationConnection` declares a `provider` other than `fleet`
- **THEN** validation fails and the error list names the connection and the bad
  provider

#### Scenario: IntegrationConnection malformed base URL rejected

- **WHEN** an `IntegrationConnection.base_url` is not a well-formed absolute
  `http`/`https` URL
- **THEN** validation fails and the error list names the connection and the malformed
  base URL

#### Scenario: IntegrationConnection unknown cadence rejected

- **WHEN** an `IntegrationConnection.discovery_cadence` is outside the cadence set
- **THEN** validation fails and the error list names the connection and the bad cadence

#### Scenario: IntegrationConnection references an unknown vendor

- **WHEN** an `IntegrationConnection` names a `vendor` id that no `Vendor` document
  defines
- **THEN** validation fails and the error list names the connection and the unknown
  vendor reference

#### Scenario: Duplicate connection id rejected

- **WHEN** two `IntegrationConnection` documents share the same `id`
- **THEN** validation fails and the error list names the duplicated id

#### Scenario: Connection id that is an unsafe configuration-key segment rejected

- **WHEN** an `IntegrationConnection.id` contains a `:` character or a `__` sequence, or
  two `IntegrationConnection` ids differ only in letter case
- **THEN** validation fails and the error list names the connection and the unsafe or
  colliding id, because the id would resolve an ambiguous or wrong out-of-band token

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

