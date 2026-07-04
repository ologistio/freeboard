# organisation-model Specification

## Purpose
TBD - created by archiving change redefine-scope-org-standard. Update Purpose after archive.
## Requirements
### Requirement: Organisation is a recursive tree with a kind

The system SHALL model an `Organisation` in `Freeboard.Core` as a resource with an
immutable `id`, a mutable `title`, a `kind`, and an optional `parent` that is the
`id` of another organisation. The `kind` is authored under the YAML key `type` so
it does not collide with the document discriminator `kind`, and persists and reads
back as the organisation's `kind`. `kind` SHALL be an enumeration whose values for
this increment are `Company` and `Department`. An organisation with no `parent` is
a tree root; an organisation with a `parent` is a child of that organisation.
Multiple roots MAY exist.

#### Scenario: Company with a department child

- **WHEN** an organisation `ologist-products` of kind `Company` and an organisation
  `ologist-products-eng` of kind `Department` with `parent` `ologist-products` are
  defined
- **THEN** both load into the model, `ologist-products` is a root, and
  `ologist-products-eng` is its child

#### Scenario: Unknown kind is rejected

- **WHEN** an organisation declares a `type` other than `Company` or `Department`
- **THEN** it is reported invalid, naming the organisation and the bad kind value

### Requirement: Organisation tree is acyclic with resolvable parents

The system SHALL treat the organisation set as a directed acyclic tree. Validation
SHALL fail when an organisation's `parent` names an id that no organisation
defines, or when following `parent` links forms a cycle (including an organisation
that is its own parent).

#### Scenario: Dangling parent rejected

- **WHEN** an organisation's `parent` names an id that no organisation defines
- **THEN** validation fails and the error names the organisation and the unknown
  parent id

#### Scenario: Parent cycle rejected

- **WHEN** organisation A has parent B and B has parent A (or an organisation names
  itself as parent)
- **THEN** validation fails and the error identifies the cycle

### Requirement: Scope binds one organisation to one standard with a disposition

The system SHALL model a `Scope` as a mapping from one `Organisation` to one
`Standard` carrying a `disposition`. `disposition` SHALL be an enumeration whose
values are `In` and `Out`. A Scope keeps its own immutable `id`. A Scope SHALL
reference an existing organisation and an existing standard; validation SHALL fail
on a reference that does not resolve. At most one Scope SHALL exist per
`(organisation, standard)` pair.

#### Scenario: Scope references resolve

- **WHEN** a Scope names an `organisation` id and a `standard` id that both exist,
  with disposition `In`
- **THEN** it loads as a valid mapping for that organisation and standard

#### Scenario: Dangling scope reference rejected

- **WHEN** a Scope names an `organisation` or `standard` id that no resource defines
- **THEN** validation fails and the error names the scope and the unknown reference

#### Scenario: Unknown disposition rejected

- **WHEN** a Scope declares a `disposition` other than `In` or `Out`
- **THEN** validation fails and the error names the scope and the bad disposition

#### Scenario: Duplicate mapping rejected

- **WHEN** two Scopes name the same `(organisation, standard)` pair
- **THEN** validation fails and the error names the duplicated pair

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

