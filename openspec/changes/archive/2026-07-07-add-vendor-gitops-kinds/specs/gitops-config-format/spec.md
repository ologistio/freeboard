## MODIFIED Requirements

### Requirement: Declarative compliance config schema

The system SHALL define a YAML config format that describes compliance state as
a set of standards, the controls under each standard, the requirements published
by each standard, the organisations being assessed, the scopes that map an
organisation to a standard, the requirement-scopes that map an organisation to a
requirement, the vendors (software and platforms) in use, and the vendor-scopes
that record whether a requirement or control applies to a vendor. The format SHALL
be loadable into a typed config model in `Freeboard.Core`.

A config directory contains one or more `.yaml` files. Each document has a
top-level `apiVersion` and `kind`. The only valid `apiVersion` value for this
increment is `freeboard.dev/v1alpha1`. For this increment the valid `kind` values
are `Standard`, `Control`, `Requirement`, `Organisation`, `Scope`,
`RequirementScope`, `Vendor`, and `VendorScope`. Documents of different kinds MAY
appear in any file. Every resource SHALL have an immutable `id` that is its
identity and a mutable `title` for display. A `Standard` has an `id`, a `title`,
required `version` and `authority`, and optional `publisher` and `source_url`
metadata. A `Control` has an `id`, a `title`, and a `maps_to` field that is a
non-empty list of `Requirement` ids (the requirements the control satisfies). A
`Requirement` has an `id`, a `title`, a `standard` (a single `Standard` id it
belongs to), a `theme` (a free-form label), a `statement` (the normative text), an
optional `guidance`, a `citation_label`, and a `citation_url` (an absolute
`http`/`https` link to the published source). An `Organisation` has an `id`, a
`title`, a `type` (`Company` or `Department`), and an optional `parent` that is an
`Organisation` id. The Organisation's Company/Department value is authored under the
YAML key `type` so it does not collide with the document discriminator `kind`; it
persists and reads back as the organisation's `kind`. A `Scope` has an `id`, a
`title`, an `organisation` (an `Organisation` id), a `standard` (a `Standard` id),
and a `disposition` (`In` or `Out`). A `RequirementScope` has an `id`, a `title`, an
`organisation` (an `Organisation` id), a `requirement` (a `Requirement` id), and a
`disposition` (`In` or `Out`); it has no `standard` field. A `Vendor` has an `id`
and a `title`. A `VendorScope` has an `id`, a `title`, a `vendor` (a `Vendor` id),
exactly one of `requirement` (a `Requirement` id) or `control` (a `Control` id), a
`disposition` (`In` or `Out`), and a `justification` (required when the disposition
is `Out`). Property binding is snake_case for domain/property fields, so `maps_to`
and `source_url` bind to their model properties. The document discriminator key
`kind` and the version key `apiVersion` are camelCase to match the Kubernetes-style
convention; they are not snake_case-bound, so `apiVersion` is the valid key (not
`api_version`).

#### Scenario: Valid config loads into the typed model

- **WHEN** a directory contains well-formed YAML documents of kinds `Standard`,
  `Control`, `Requirement`, `Organisation`, `Scope`, `RequirementScope`, `Vendor`,
  and `VendorScope`
- **THEN** the loader returns a typed config model containing all standards
  (with any metadata), controls, requirements, organisations, scopes,
  requirement-scopes, vendors, and vendor-scopes with their `id`, `title`, and
  reference fields populated and no errors

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
  or `VendorScope`
- **THEN** the loader returns a diagnostic naming the document and the bad
  `kind`, does not throw, and does not deserialize that document further

### Requirement: Config carries no secret material

The schema SHALL NOT define any field that holds a secret (token, key, password,
or equivalent). Credentials needed by future integrations SHALL be referenced by
a named credential resolved out-of-band, never inlined in git-tracked config.

#### Scenario: No secret fields exist

- **WHEN** the schema for `Standard`, `Control`, `Requirement`, `Organisation`,
  `Scope`, `RequirementScope`, `Vendor`, and `VendorScope` is inspected
- **THEN** it contains no field intended to hold secret material

## ADDED Requirements

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
