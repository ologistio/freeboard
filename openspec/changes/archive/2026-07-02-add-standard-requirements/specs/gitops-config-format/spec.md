## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the requirements published
by each standard, the organisations being assessed, and the scopes that map an
organisation to a standard. The format SHALL be loadable into a typed config model
in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.io/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Organisation`, and `Scope`. Documents
of different kinds MAY appear in any file. Every resource SHALL have an immutable
`id` that is its identity and a mutable `title` for display. A `Standard` has an
`id`, a `title`, required `version` and `authority`, and optional `publisher` and
`source_url` metadata. A `Control` has an `id`, a `title`, and a `maps_to` field
that is a non-empty list of `Requirement` ids (the requirements the control
satisfies). A `Requirement` has an `id`, a `title`, a
`standard` (a single `Standard` id it belongs to), a `theme` (a free-form label),
a `statement` (the normative text), an optional `guidance`, a `citation_label`,
and a `citation_url` (an absolute `http`/`https` link to the published source). An
`Organisation` has an `id`, a `title`, a `type` (`Company` or `Department`), and
an optional `parent` that is an `Organisation` id. The Organisation's
Company/Department value is authored under the YAML key `type` so it does not
collide with the document discriminator `kind`; it persists and reads back as the
organisation's `kind`. A `Scope` has an `id`, a `title`, an `organisation` (an
`Organisation` id), a `standard` (a `Standard` id), and a `disposition` (`In` or
`Out`). Property binding is snake_case for domain/property fields, so `maps_to`
and `source_url` bind to their model properties. The document discriminator key
`kind` and the version key `apiVersion` are camelCase to match the
Kubernetes-style convention; they are not snake_case-bound, so `apiVersion` is the
valid key (not `api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Organisation`, and `Scope`
- **THEN** the loader returns a typed config model containing all standards
  (with any metadata), controls, requirements, organisations, and scopes with
  their `id`, `title`, and reference fields populated and no errors

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
`Scope.standard` references a `Standard` id that does not exist; an
`Organisation.type` (the authored Company/Department value) is not `Company` or
`Department`; a `Scope.disposition` is not `In` or `Out`; a `Standard` is missing
or blank on its required `version` or `authority`; a `Standard.source_url` is
present and non-empty but is not a well-formed absolute `http`/`https` URL; two
Scopes share the same `(organisation, standard)` pair; the `apiVersion` is not
exactly `freeboard.io/v1alpha1`. Optional fields (`Requirement.guidance`,
`Standard.publisher`, `Standard.source_url`) that are omitted or whitespace-only
are treated as absent and do NOT fail validation; their non-empty and URI-format
checks run only when the field is present and non-empty. Unknown or missing `kind`
is reported by the loader (see the loader requirement below), not re-checked here.

#### Scenario: Missing required field

- **WHEN** a `Control` document omits its `maps_to` field
- **THEN** validation fails and the error list includes an entry naming the
  document and the missing field

#### Scenario: Requirement missing required field

- **WHEN** a `Requirement` document omits its `standard`, `theme`, `statement`,
  `citation_label`, or `citation_url`
- **THEN** validation fails and the error list includes an entry naming the
  requirement and the missing field

#### Scenario: Malformed requirement citation URL rejected

- **WHEN** a `Requirement` provides a `citation_url` that is not a well-formed
  absolute `http`/`https` URL
- **THEN** validation fails and the error list names the requirement and the bad
  `citation_url`

#### Scenario: Requirement references an unknown standard

- **WHEN** a `Requirement` names a `standard` id that no `Standard` document
  defines
- **THEN** validation fails and the error list names the requirement and the
  unknown standard reference

#### Scenario: Malformed standard source URL rejected

- **WHEN** a `Standard` provides a `source_url` that is not a well-formed absolute
  `http`/`https` URL
- **THEN** validation fails and the error list names the standard and the bad
  `source_url`

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
  `Control`, `Requirement`, `Organisation`, or `Scope`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds a secret (token, key, password,
or equivalent). Credentials needed by future integrations SHALL be referenced by
a named credential resolved out-of-band, never inlined in git-tracked config.

#### Scenario: No secret fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  and `Scope` is inspected
- **THEN** it contains no field intended to hold secret material
