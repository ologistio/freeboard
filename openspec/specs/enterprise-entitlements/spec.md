# enterprise-entitlements Specification

## Purpose
TBD - created by archiving change add-enterprise-entitlement-seam. Update Purpose after archive.
## Requirements
### Requirement: Single enterprise entitlement seam in Freeboard.Core

The system SHALL expose one entitlement seam that answers whether an install is
entitled to a named Enterprise Edition feature. The seam SHALL be an interface in
`Freeboard.Core` (MIT) exposing a single method that takes an entitlement
identifier and returns a boolean. The identifier SHALL be an enumeration so that a
future entitlement is added without changing the interface. The seam SHALL be
consumable by MIT code and SHALL NOT require any reference to `Freeboard.Enterprise`.

#### Scenario: Interface is in the Core assembly

- **WHEN** the entitlement interface type is inspected
- **THEN** it resides in the `Freeboard.Core` assembly and depends on no web,
  persistence, or `Freeboard.Enterprise` type

#### Scenario: Gate is a single call

- **WHEN** a caller checks entitlement to a feature
- **THEN** it calls one method with the feature's enumeration value and receives a
  single boolean answer

### Requirement: Config-backed implementation registered in the web app

The web app (`Freeboard`) SHALL provide the config-backed implementation of the
entitlement seam and register it in dependency injection. The implementation SHALL
read each entitlement from configuration under an `Enterprise` section (the
`CustomPolicies` entitlement from the `Enterprise:CustomPolicies` boolean key). The
implementation SHALL NOT live in `Freeboard.Core` or `Freeboard.Enterprise`.

#### Scenario: CustomPolicies enabled by configuration

- **WHEN** `Enterprise:CustomPolicies` is set to a true value and the entitlement
  is checked for `CustomPolicies`
- **THEN** the seam returns entitled (true)

#### Scenario: Resolvable from the web app service provider

- **WHEN** the web app builds its service provider
- **THEN** the entitlement seam resolves to the config-backed implementation

### Requirement: Default disabled and fail-safe

An entitlement SHALL default to not entitled when its configuration key is absent
or false, so an MIT build runs with the entitlement off and no configuration
change is required to keep it off. An entitlement value that the implementation
does not recognise SHALL return not entitled.

#### Scenario: Absent configuration is not entitled

- **WHEN** no `Enterprise` configuration is supplied and the entitlement is checked
  for `CustomPolicies`
- **THEN** the seam returns not entitled (false)

#### Scenario: Explicit false is not entitled

- **WHEN** `Enterprise:CustomPolicies` is set to false and the entitlement is
  checked for `CustomPolicies`
- **THEN** the seam returns not entitled (false)

### Requirement: Seam does not disturb the authorization foundation when off

Adding the entitlement seam SHALL NOT change seeded roles, authorization tables,
or authorization decisions. With no entitlement enabled and no consumer of the
seam, existing authorization behavior SHALL be unchanged.

#### Scenario: Authorization behavior unchanged when off

- **WHEN** the entitlement seam is registered but no feature consumes it and no
  entitlement is enabled
- **THEN** seeded roles, authorization tables, and authorization decisions are
  identical to before the seam was added

