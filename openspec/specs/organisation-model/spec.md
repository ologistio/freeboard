# organisation-model Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Scope binds one organisation to one standard with a disposition

The system SHALL model a `Scope` as a mapping from one `subject` asset to one target -
exactly one of a `Standard`, a `Requirement`, or a `Control` - carrying a `disposition`.
When the `subject` is an organisation asset (an `Asset` of `type: Company` or
`type: Department`), the scope MAY target a standard, a requirement, or a control; the
standard-target case is the organisation-to-standard binding this capability governs, and
the requirement-target case is the requirement-level binding (folded from the former
`RequirementScope`). `disposition` SHALL be an enumeration whose values are `In` and
`Out`. A Scope keeps its own immutable `id`. A Scope SHALL name a `subject` and exactly
one target; the target reference SHALL resolve to an existing standard, requirement, or
control (validation SHALL fail on a target that does not resolve), while a `subject` that
resolves to no asset SHALL be a non-blocking warning rather than an error (the subject is
a scalar asset reference, dangling-tolerated). At most one Scope SHALL exist per
`(subject, standard)`, per `(subject, requirement)`, and per `(subject, control)` pair. An
`Out` scope SHALL carry a `justification`. The full `Scope` schema and validation are
defined by the gitops-config-format capability.

#### Scenario: Organisation scope targeting a standard resolves

- **WHEN** a Scope names a `subject` id that resolves to a `Company` or `Department`
  asset and a `standard` id that exists, with disposition `In`
- **THEN** it loads as a valid standard-level mapping for that organisation asset and
  standard

#### Scenario: Dangling scope target rejected

- **WHEN** a Scope names a `standard`, `requirement`, or `control` id that no resource
  defines
- **THEN** validation fails and the error names the scope and the unknown target
  reference

#### Scenario: Dangling scope subject is a warning

- **WHEN** a Scope names a `subject` id that no asset defines
- **THEN** validation does not fail; a non-blocking warning names the dangling subject

#### Scenario: Unknown disposition rejected

- **WHEN** a Scope declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error names the scope and the bad disposition

#### Scenario: Duplicate mapping rejected

- **WHEN** two Scopes name the same `(subject, standard)` pair
- **THEN** validation fails and the error names the duplicated pair

