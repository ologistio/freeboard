## MODIFIED Requirements

### Requirement: Accessible organisation set bounds selection and scoping

The web app SHALL determine the set of organisation ids a user may access through a
single seam and use that set both to bound the selector tree and to bound every
org-scoped view. Selecting an organisation outside the accessible set SHALL fail
closed: it SHALL NOT become the resolved selection and its data SHALL NOT be
rendered. The accessible set SHALL be derived from authorization: it is the union
of organisation subtrees on which the user holds a read-granting role, and all
persisted organisations for a super-admin. Under the Observe rollout mode reads are
NOT narrowed: the accessible set SHALL be all persisted organisations for every
caller regardless of grants, so read behaviour is unchanged while decisions are
observed. Under the Compat rollout mode a user with no assignments SHALL retain
access to all persisted organisations through an audited fallback; under Enforce a
user with no read-granting role SHALL have an empty accessible set. Because the seam
is async (it loads the user's grants), it
SHALL resolve the accessible set once per request, memoized alongside the
authorization fact load.

#### Scenario: Accessible set bounds the selector and views

- **WHEN** the selector tree and an org-scoped view are rendered
- **THEN** both include only organisations in the accessible set

#### Scenario: All Organisations is bounded by the accessible set

- **WHEN** the resolved selection is "All Organisations" and the accessible set is
  a strict subset of the persisted organisations
- **THEN** the selector tree and every org-scoped view render only the accessible
  organisations, and no organisation outside the accessible set appears even though
  no subtree filter is applied

#### Scenario: Selecting an inaccessible organisation fails closed

- **WHEN** a user submits a selection for an organisation not in the accessible set
- **THEN** the selection does not take effect and no data for that organisation is
  rendered

#### Scenario: Super-admin accesses every organisation

- **WHEN** a super-admin renders the selector or an org-scoped view
- **THEN** the accessible set is all persisted organisations

#### Scenario: Observe does not narrow reads

- **WHEN** the rollout mode is Observe and a caller holds a grant on only part of the
  organisation tree
- **THEN** the accessible set is all persisted organisations, so the caller sees the
  full tree and read behaviour is unchanged
