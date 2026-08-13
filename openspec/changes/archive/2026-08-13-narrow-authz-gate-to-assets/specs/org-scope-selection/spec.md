## MODIFIED Requirements

### Requirement: Accessible organisation set bounds selection and scoping

The web app SHALL determine the set of ids a user may access through a
single seam and use that set both to bound the selector tree and to bound every
org-scoped view. That seam SHALL resolve an ACCESSIBLE ASSET set over the unified asset tree
(see the authz-enforcement capability): the union of organisation subtrees on which the user
holds a read-granting role, closed over the asset `parent` chain and the vendor `owner` edge,
with retired discovered assets excluded. The selector and the selection resolver present and
validate ORGANISATION nodes only, so each SHALL intersect that set with the `Company`- and
`Department`-typed assets at the point of use; a `Machine` or `Vendor` id in the accessible
set SHALL NOT become a selectable entry or a resolved selection.

Selecting an organisation outside the accessible set SHALL fail
closed: it SHALL NOT become the resolved selection and its data SHALL NOT be
rendered. All persisted organisations are accessible for a super-admin. The rollout mode SHALL
relax the ORGANISATION union only, never the edge closure over it. Under the Observe rollout
mode reads are NOT narrowed: the union SHALL be all persisted organisations for every caller
regardless of grants, and the accessible set SHALL be that union closed over the asset tree, so
read behaviour is unchanged while decisions are observed. Under the Compat rollout mode a user
with no assignments SHALL retain the same all-organisations union, closed the same way, through
an audited fallback; under Enforce a user with no read-granting role SHALL have an empty union
and therefore an empty accessible set. In every mode an asset whose inclusive chain reaches no
organisation in the union - a missing or dangling edge yields no hit - and a retired discovered
asset remain outside the accessible set; an organisation whose own id is in the union stays
inside it whatever its `parent` says, because the chain starts at the asset itself. Because the seam
is async (it loads the user's grants), it
SHALL resolve the accessible set at most once per principal PER ASSET LIST within a request,
memoized alongside the authorization fact load. A surface that takes its own store read SHALL be
served a set resolved from THAT read's asset rows, never a set resolved from another surface's
asset list, so two surfaces in one request cannot narrow each other's rows. Surfaces that share
one asset list SHALL share one resolution, so the selector and the views it bounds walk the asset
tree once between them rather than once each.

The requirement's name is historical. The set it now governs is the accessible ASSET set; the
organisation union survives inside it as the step the rules above are anchored on, and the
selector and the selection resolver still present organisations only.

#### Scenario: Accessible set bounds the selector and views

- **WHEN** the selector tree and an org-scoped view are rendered
- **THEN** both include only organisations in the accessible set

#### Scenario: The selector's bound comes from the asset list it read

- **WHEN** the selector and another surface in the same request take different store reads
- **THEN** each is bounded by an accessible set resolved from its own read's asset rows, and
  neither is served the other's set

#### Scenario: Non-organisation assets are not selectable

- **WHEN** the accessible set contains a `Machine` and a `Vendor` reachable through the
  user's grants and the selector is rendered
- **THEN** neither appears as a selector entry, and a selection cookie naming either
  resolves to "All Organisations"

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
- **THEN** the organisation union is all persisted organisations and the accessible set is
  that union closed over the asset tree, so every organisation is selectable while an
  ownerless vendor and a retired discovered asset stay outside the set

#### Scenario: Observe does not narrow reads

- **WHEN** the rollout mode is Observe and a caller holds a grant on only part of the
  organisation tree
- **THEN** the organisation union is all persisted organisations and the accessible set is
  that union closed over the asset tree, so the caller sees the full organisation tree and
  read behaviour is unchanged, while an ownerless vendor and a retired discovered asset
  stay outside the set
