# organisation-model Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Scope binds one organisation to one standard with a disposition

The system SHALL model a `Scope` as a mapping from one organisation asset (an
`Asset` of `type: Company` or `type: Department`) to one `Standard` carrying a
`disposition`. `disposition` SHALL be an enumeration whose values are `In` and
`Out`. A Scope keeps its own immutable `id`. A Scope SHALL reference an existing
`Company`/`Department` asset and an existing standard; validation SHALL fail on a
reference that does not resolve to the correct target. At most one Scope SHALL
exist per `(organisation, standard)` pair.

#### Scenario: Scope references resolve

- **WHEN** a Scope names an `organisation` id that resolves to a `Company` or
  `Department` asset and a `standard` id that exists, with disposition `In`
- **THEN** it loads as a valid mapping for that organisation asset and standard

#### Scenario: Dangling scope reference rejected

- **WHEN** a Scope names an `organisation` that is not a `Company`/`Department`
  asset, or a `standard` id that no resource defines
- **THEN** validation fails and the error names the scope and the unknown or
  wrong-type reference

#### Scenario: Unknown disposition rejected

- **WHEN** a Scope declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error names the scope and the bad disposition

#### Scenario: Duplicate mapping rejected

- **WHEN** two Scopes name the same `(organisation, standard)` pair
- **THEN** validation fails and the error names the duplicated pair

### Requirement: RequirementScope binds one organisation to one requirement with a disposition

The system SHALL model a `RequirementScope` as a mapping from one organisation
asset (an `Asset` of `type: Company` or `type: Department`) to one `Requirement`
carrying a `disposition`. `disposition` SHALL be the same enumeration as `Scope`,
whose values are `In` and `Out`. A `RequirementScope` keeps its own immutable `id`.
It SHALL reference an existing `Company`/`Department` asset and an existing
requirement; validation SHALL fail on a reference that does not resolve to the
correct target. A `RequirementScope` SHALL NOT name a `standard`: the owning
standard is derived from the requirement (`Requirement.standard`). At most one
`RequirementScope` SHALL exist per `(organisation, requirement)` pair.

A `RequirementScope` is a scope at requirement granularity: it layers under the
standard-level `Scope` for the same organisation asset and is resolved by the same
nearest-ancestor inheritance over the organisation asset's `parent` chain (see the
statement-of-applicability capability). The symmetric `In`/`Out` disposition lets a
child node re-include (`In`) a requirement its ancestor excluded (`Out`).

#### Scenario: RequirementScope references resolve

- **WHEN** a `RequirementScope` names an `organisation` id that resolves to a
  `Company`/`Department` asset and a `requirement` id that exists, with disposition
  `Out`
- **THEN** it loads as a valid requirement-level mapping for that organisation asset
  and requirement

#### Scenario: Dangling requirement-scope reference rejected

- **WHEN** a `RequirementScope` names an `organisation` that is not a
  `Company`/`Department` asset, or a `requirement` id that no resource defines
- **THEN** validation fails and the error names the requirement-scope and the
  unknown or wrong-type reference

#### Scenario: Unknown disposition rejected

- **WHEN** a `RequirementScope` declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error names the requirement-scope and the bad
  disposition

#### Scenario: Duplicate mapping rejected

- **WHEN** two `RequirementScope` documents name the same `(organisation,
  requirement)` pair
- **THEN** validation fails and the error names the duplicated pair

#### Scenario: Same organisation across requirements is allowed

- **WHEN** an organisation asset has a requirement-scope for requirement A and
  another for requirement B
- **THEN** both load, because the uniqueness key is the `(organisation, requirement)`
  pair, not the organisation alone

