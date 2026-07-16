## RENAMED Requirements

- FROM: `### Requirement: IntegrationConnection authorship`
- TO: `### Requirement: Integration authorship`

- FROM: `### Requirement: IntegrationConnection validation`
- TO: `### Requirement: Integration validation`

## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as a
set of standards, the controls under each standard, the requirements published by
each standard, the assets being assessed (the organisation tree, the vendors in
use, and the discovered machines), the scopes that map an organisation asset to a
standard, the requirement-scopes that map an organisation asset to a requirement,
the vendor-scopes that record whether a requirement or control applies to a vendor
asset, the integrations that define a provider connection's base URL and
discovery cadence, the evidence-collectors that attach a data source to a control,
and the attestation-templates that describe an attestation form for a control. The
format SHALL be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`,
`VendorScope`, `Integration`, `EvidenceCollector`, and
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
`Integration` has an `id`, a `title`, a required `provider` (a closed
token set whose only value in this increment is `fleet`), a required `base_url` (an
absolute `http`/`https` URL), a required `discovery_cadence` (`continuous`,
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
  `Control`, `Requirement`, `Asset`, `Scope`, `RequirementScope`, `VendorScope`,
  `Integration`, `EvidenceCollector`, and `AttestationTemplate`
- **THEN** the loader returns a typed config model containing all standards,
  controls, requirements, assets (with `type`, `source`, and any `parent`/`owner`),
  scopes, requirement-scopes, vendor-scopes, integrations,
  evidence-collectors, and attestation-templates with their `id`, `title`, and
  reference fields populated and no errors

#### Scenario: Multiple documents in one file

- **WHEN** a single YAML file contains multiple documents separated by `---`
- **THEN** every document is parsed and included in the config model

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
  `Integration`, `EvidenceCollector`, or `AttestationTemplate`
- **THEN** the loader returns a diagnostic naming the document and the bad `kind`,
  does not throw, and does not deserialize that document further

#### Scenario: Retired IntegrationConnection kind is now unknown

- **WHEN** a document authors `kind: IntegrationConnection`, the pre-ratification wire
  token
- **THEN** the loader loads no connection from it and returns an unknown-kind
  diagnostic naming the document and the bad `kind`; the diagnostic's valid-kinds
  enumeration lists `Integration` and does NOT contain the substring
  `IntegrationConnection`

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds credential material (a token,
key, password, or equivalent). Credentials needed by integrations SHALL be
referenced by identity and resolved out-of-band, never inlined in git-tracked
config. The `EvidenceCollector.config` map is git-tracked type-specific settings and
SHALL NOT inline credential material; a collector that needs a credential names it
for out-of-band resolution. An `Integration` names its provider, base URL,
and discovery cadence in git, but SHALL NOT carry its API token: the token is
resolved out-of-band at runtime, keyed by the connection id, and is never authored in
config. The `AttestationTemplate` `body`, `fields`, and `quiz` are git-tracked form
and quiz content and SHALL NOT inline credential material. A training quiz's correct
`answer` is not credential material: it is confidential authoring data that MAY be
stored in git-tracked config (the later grading runtime needs it) but MUST be redacted
from every broad read surface (the read API, CLI, and web register).

#### Scenario: No credential fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, `RequirementScope`, `Vendor`, `VendorScope`, `Integration`,
  `EvidenceCollector`, and `AttestationTemplate` is inspected
- **THEN** it contains no field intended to hold credential material, and an
  `Integration` in particular declares no token field

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
(an `Integration` id, the connection that backs the collector) and a
`checks` list. Each item in `checks` has a `source_key` (the provider-native id, for
example a Fleet policy id, the key that joins a provider result to a Freeboard
check), a `name` (the Freeboard check name), and a `severity` that is exactly one of
`Hard` or `Soft` (matching an evidence check's severity: a failing `Hard` check fails
the requirement, a failing `Soft` check warns). The `connection` SHALL be empty and
the `checks` list SHALL be omitted on any collector whose `type` is not `integration`.
The provider that runs an integration collector is the `provider` of the `Integration`
it names; that provider is drawn from the single, shared provider token set (only
`fleet` in this increment), not a separate collector vocabulary.

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
  `connection` naming a defined `Integration` id, and a non-empty `checks`
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
missing its `connection` or names a `connection` id that no `Integration`
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
  names a `connection` id that no `Integration` document defines
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

### Requirement: Config-format documentation covers every supported kind

The GitOps config-format documentation (`docs/gitops.md`) SHALL document every
kind the loader and validator support. For each kind it SHALL state the schema
fields, at least one example document, and the validation rules, including the
referential-integrity rules (which fields reference which other kind by id and
that a reference to an absent id is rejected). This SHALL include
Integration (an optional `vendor` reference by id, its out-of-band token
resolved by id, never in config), EvidenceCollector (references a control by id
and, optionally, a vendor by id, and, when `type: integration`, a connection by id
plus its `checks` list) and AttestationTemplate (references a control by id), so the
documented surface matches the shipped `GitOpsSchema` kind set.

#### Scenario: Integration documented with its schema and token rule

- **WHEN** a reader consults `docs/gitops.md`
- **THEN** it describes the Integration kind, its `provider`, `base_url`,
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
- **THEN** the supported-kinds list includes `Integration` alongside the existing
  kinds, and the noun-mapping row stays `integration-connections` (it is NOT renamed to
  `integration`), so no shipped kind is omitted from the catalogue and the
  persisted-entity noun is unchanged

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
whose only value in this increment is `fleet`. That one closed set governs exactly two
things: it validates `Integration.provider`, and it selects the runner for an
integration `EvidenceCollector` that names this connection. There is no separate
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
`daily`, `weekly`, `monthly`, `quarterly`, or `annual` (the same set an
`EvidenceCollector.frequency` uses). The connection SHALL NOT carry an API token or
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
