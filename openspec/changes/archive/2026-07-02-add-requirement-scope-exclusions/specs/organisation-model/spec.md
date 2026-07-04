## ADDED Requirements

### Requirement: RequirementScope binds one organisation to one requirement with a disposition

The system SHALL model a `RequirementScope` as a mapping from one `Organisation` to
one `Requirement` carrying a `disposition`. `disposition` SHALL be the same
enumeration as `Scope`, whose values are `In` and `Out`. A `RequirementScope` keeps
its own immutable `id`. It SHALL reference an existing organisation and an existing
requirement; validation SHALL fail on a reference that does not resolve. A
`RequirementScope` SHALL NOT name a `standard`: the owning standard is derived from
the requirement (`Requirement.standard`), so a requirement-scope cannot name a
standard the requirement does not belong to. At most one `RequirementScope` SHALL
exist per `(organisation, requirement)` pair; because a requirement determines its
standard, this pair is equivalent to `(organisation, standard, requirement)`.

A `RequirementScope` is a scope at requirement granularity: it layers under the
standard-level `Scope` for the same organisation node and is resolved by the same
nearest-ancestor inheritance over the organisation tree (see the
statement-of-applicability capability). The symmetric `In`/`Out` disposition lets a
child node re-include (`In`) a requirement its ancestor excluded (`Out`).

#### Scenario: RequirementScope references resolve

- **WHEN** a `RequirementScope` names an `organisation` id and a `requirement` id
  that both exist, with disposition `Out`
- **THEN** it loads as a valid requirement-level mapping for that organisation and
  requirement

#### Scenario: Dangling requirement-scope reference rejected

- **WHEN** a `RequirementScope` names an `organisation` or `requirement` id that no
  resource defines
- **THEN** validation fails and the error names the requirement-scope and the unknown
  reference

#### Scenario: Unknown disposition rejected

- **WHEN** a `RequirementScope` declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error names the requirement-scope and the bad
  disposition

#### Scenario: Duplicate mapping rejected

- **WHEN** two `RequirementScope` documents name the same `(organisation,
  requirement)` pair
- **THEN** validation fails and the error names the duplicated pair

#### Scenario: Same organisation across requirements is allowed

- **WHEN** an organisation has a requirement-scope for requirement A and another for
  requirement B
- **THEN** both load, because the uniqueness key is the `(organisation, requirement)`
  pair, not the organisation alone
