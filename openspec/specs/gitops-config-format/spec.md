# gitops-config-format Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the organisations being
assessed, and the scopes that map an organisation to a standard. The format SHALL
be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.io/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Organisation`, and `Scope`. Documents of different
kinds MAY appear in any file. Every resource SHALL have an immutable `id` that is
its identity and a mutable `title` for display. A `Standard` has an `id` and a
`title`. A `Control` has an `id`, a `title`, and a `maps_to` field that is a list
of `Standard` ids. An `Organisation` has an `id`, a `title`, a `type` (`Company`
or `Department`), and an optional `parent` that is an `Organisation` id. The
Organisation's Company/Department value is authored under the YAML key `type` so
it does not collide with the document discriminator `kind`; it persists and reads
back as the organisation's `kind`. A `Scope` has an `id`, a `title`, an
`organisation` (an `Organisation` id), a `standard` (a `Standard` id), and a
`disposition` (`In` or `Out`). Property binding is snake_case for
domain/property fields, so `maps_to` binds to its model property. The document
discriminator key `kind` and the version key `apiVersion` are camelCase to match
the Kubernetes-style convention; they are not snake_case-bound, so `apiVersion` is
the valid key (not `api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Organisation`, and `Scope`
- **THEN** the loader returns a typed config model containing all standards,
  controls, organisations, and scopes with their `id`, `title`, and reference
  fields populated and no errors

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
document; an `id` is duplicated within its kind; a `Control.maps_to` entry
references a `Standard` id that does not exist; an `Organisation.parent` references
an `Organisation` id that does not exist; the organisations form a cycle through
`parent`; a `Scope.organisation` references an `Organisation` id that does not
exist; a `Scope.standard` references a `Standard` id that does not exist; an
`Organisation.type` (the authored Company/Department value) is not `Company` or
`Department`; a `Scope.disposition` is not
`In` or `Out`; two Scopes share the same `(organisation, standard)` pair; the
`apiVersion` is not exactly `freeboard.io/v1alpha1`. Unknown or missing `kind` is
reported by the loader (see the loader requirement below), not re-checked here.

#### Scenario: Missing required field

- **WHEN** a `Control` document omits its `maps_to` field
- **THEN** validation fails and the error list includes an entry naming the
  document and the missing field

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
  `Control`, `Organisation`, or `Scope`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds a secret (token, key, password,
or equivalent). Credentials needed by future integrations SHALL be referenced by
a named credential resolved out-of-band, never inlined in git-tracked config.

#### Scenario: No secret fields exist

- **WHEN** the schema for `Standard`, `Control`, `Organisation`, and `Scope` is
  inspected
- **THEN** it contains no field intended to hold secret material

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

