## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the requirements published
by each standard, the organisations being assessed, the scopes that map an
organisation to a standard, and the requirement-scopes that map an organisation to a
requirement. The format SHALL be loadable into a typed config model in
`Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.io/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Organisation`, `Scope`, and
`RequirementScope`. Documents of different kinds MAY appear in any file. Every
resource SHALL have an immutable `id` that is its identity and a mutable `title` for
display. A `Standard` has an `id`, a `title`, required `version` and `authority`, and
optional `publisher` and `source_url` metadata. A `Control` has an `id`, a `title`,
and a `maps_to` field that is a non-empty list of `Requirement` ids (the requirements
the control satisfies). A `Requirement` has an `id`, a `title`, a `standard` (a
single `Standard` id it belongs to), a `theme` (a free-form label), a `statement`
(the normative text), an optional `guidance`, a `citation_label`, and a
`citation_url` (an absolute `http`/`https` link to the published source). An
`Organisation` has an `id`, a `title`, a `type` (`Company` or `Department`), and an
optional `parent` that is an `Organisation` id. The Organisation's Company/Department
value is authored under the YAML key `type` so it does not collide with the document
discriminator `kind`; it persists and reads back as the organisation's `kind`. A
`Scope` has an `id`, a `title`, an `organisation` (an `Organisation` id), a
`standard` (a `Standard` id), and a `disposition` (`In` or `Out`). A
`RequirementScope` has an `id`, a `title`, an `organisation` (an `Organisation` id), a
`requirement` (a `Requirement` id), and a `disposition` (`In` or `Out`); it has no
`standard` field. Property binding is snake_case for domain/property fields, so
`maps_to` and `source_url` bind to their model properties. The document discriminator
key `kind` and the version key `apiVersion` are camelCase to match the
Kubernetes-style convention; they are not snake_case-bound, so `apiVersion` is the
valid key (not `api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Organisation`, `Scope`, and `RequirementScope`
- **THEN** the loader returns a typed config model containing all standards
  (with any metadata), controls, requirements, organisations, scopes, and
  requirement-scopes with their `id`, `title`, and reference fields populated and no
  errors

#### Scenario: Multiple documents in one file

- **WHEN** a single YAML file contains multiple documents separated by `---`
- **THEN** every document is parsed and included in the config model

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
`freeboard.io/v1alpha1`. Optional fields (`Requirement.guidance`,
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

- **WHEN** a document declares an `apiVersion` other than `freeboard.io/v1alpha1`
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
  `Control`, `Requirement`, `Organisation`, `Scope`, or `RequirementScope`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds a secret (token, key, password,
or equivalent). Credentials needed by future integrations SHALL be referenced by
a named credential resolved out-of-band, never inlined in git-tracked config.

#### Scenario: No secret fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, and `RequirementScope` is inspected
- **THEN** it contains no field intended to hold secret material
