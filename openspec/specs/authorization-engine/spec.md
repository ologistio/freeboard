# authorization-engine Specification

## Purpose
TBD - created by archiving change add-authz-foundation. Update Purpose after archive.
## Requirements
### Requirement: Single authorization decision seam

The system SHALL expose one authorization decision seam that answers whether a
principal may perform an action on a resource. The core decision SHALL be a pure
function in `Freeboard.Core` that takes an immutable request (principal attributes,
the principal's effective permissions and organisation grants, the action, and a
resource reference with its inclusive organisation ancestry) and returns a `Permit`
or `Deny` effect with an audit-only reason. The seam SHALL be callable without a
database or web host so policy behavior is unit-testable in isolation.

#### Scenario: Decision is a pure function of the supplied request

- **WHEN** the engine evaluates a request
- **THEN** the result depends only on that request, performs no I/O, and returns a
  single `Permit` or `Deny` effect

#### Scenario: Reason is audit-only

- **WHEN** the engine returns a decision
- **THEN** the reason string is available for logging but is never part of a
  user-facing response body

### Requirement: Deny by default with deny-overrides combining

The engine SHALL evaluate an ordered set of policies, each returning `Permit`,
`Deny`, or `NotApplicable`, and SHALL combine them deny-overrides: any `Deny`
yields `Deny`; otherwise any `Permit` yields `Permit`; otherwise the result SHALL
be `Deny`. A request that matches no policy SHALL be denied.

#### Scenario: No matching policy denies

- **WHEN** every policy returns `NotApplicable` for a request
- **THEN** the engine returns `Deny`

#### Scenario: A permit with no deny permits

- **WHEN** at least one policy returns `Permit` and no policy returns `Deny`
- **THEN** the engine returns `Permit`

#### Scenario: A deny overrides a permit

- **WHEN** one policy returns `Permit` and another returns `Deny` for the same
  request
- **THEN** the engine returns `Deny`

### Requirement: Unauthenticated or limited session is a hard deny

The engine SHALL deny any action for a principal that is not authenticated, and
SHALL deny any action outside the limited-session allowlist for a principal whose
session is in the force-reset limited state. This SHALL be evaluated before any
permitting policy so that a later policy cannot permit an anonymous or limited
principal.

#### Scenario: Anonymous principal is denied

- **WHEN** the principal is not authenticated
- **THEN** the engine returns `Deny` regardless of any other policy

#### Scenario: Force-reset limited session is denied a non-allowlisted action

- **WHEN** the principal's session is force-reset limited and the action is not on
  the limited-session allowlist
- **THEN** the engine returns `Deny`

### Requirement: Super-admin permission permits every action

The engine SHALL permit every action for a principal whose effective permissions
include `system.admin`. This preserves existing admin gates and provides a
break-glass path so the system is never unadministrable.

#### Scenario: A system.admin holder is permitted any action

- **WHEN** the principal's effective permissions include `system.admin`
- **THEN** the engine permits the action regardless of organisation grants

#### Scenario: A principal without system.admin is not permitted by it

- **WHEN** the principal does not hold `system.admin` and no other policy permits
- **THEN** the decision is `Deny`

### Requirement: Roles and permissions are data loaded through a fact provider

The system SHALL treat roles and their permission sets as data, not code
constants. The engine SHALL decide against the principal's effective permissions
supplied in the request, loaded through a fact-provider port, rather than a
hard-coded role-to-permission map. Action-identifier strings SHALL be defined as
code constants in `Freeboard.Core` (the `AuthzActions` catalog) because endpoints
reference them at their call sites. The permission (action) identifiers SHALL be
`system.admin`, `authz.assignment.write`, `org.read`, `org.write`,
`compliance.read`, `compliance.scope.write`, `compliance.requirement-scope.write`,
and `user.manage`.

#### Scenario: The engine decides against supplied effective permissions

- **WHEN** the request supplies the principal's effective permissions for the
  resource organisation and its ancestry
- **THEN** the engine permits the action only if those effective permissions
  contain the action, and does not consult any hard-coded role map

#### Scenario: Action identifiers are compile-time constants

- **WHEN** an endpoint declares the permission it requires
- **THEN** it references an `AuthzActions` constant, and the set of action
  identifiers is exactly the eight listed keys

### Requirement: The policy pipeline exposes a self-access extension slot

The ordered policy pipeline SHALL include a self-access policy slot for
attribute-based rules about a principal acting on its own resources, evaluated
after the super-admin policy and before the organisation RBAC policy. In this
increment the slot SHALL ship with no default rule (returning `NotApplicable`), so
adding a self-service rule later requires no change to the engine seam.

#### Scenario: Self-access slot is inert by default

- **WHEN** the self-access policy evaluates a request in this increment
- **THEN** it returns `NotApplicable` and does not affect the decision

