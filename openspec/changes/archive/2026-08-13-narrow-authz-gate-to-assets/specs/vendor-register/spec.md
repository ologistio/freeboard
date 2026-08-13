## MODIFIED Requirements

### Requirement: The register warns before a certification lapses

The register SHALL warn about a lapsing certification in two places beyond the cell
that holds it, so a lapse is visible without opening the page and without reading every
row.

The summary notice above the register's tabs SHALL take a warning appearance and state
how many readable vendors hold an expiring or expired assurance, whenever that count is
above zero. When the count is zero the notice SHALL keep its existing neutral appearance
and SHALL NOT mention assurances.

The Vendors navigation item SHALL badge the same count. The count SHALL be narrowed to
the vendors the caller may read, by the same accessible-asset-set test every other
vendor read uses, so a badge never counts a vendor whose row the caller cannot see. It
SHALL count a vendor once however many lapsing assurances it holds, and SHALL NOT count
a vendor that holds no assurance at all: a badge that is permanently non-zero stops
being read, which is what the actionable-count rule exists to prevent (N6). An expired
assurance SHALL count on both surfaces exactly as an expiring one does: a lapsed
certificate is at least as actionable as one about to lapse, so a vendor whose only
assurance is expired SHALL raise the notice and increment the badge.

The count SHALL be computed for the request that renders the rail rather than carried
across requests, because a badge that lags the page it points at is worse than no badge.
Within one request it SHALL be resolved at most once, however many times the shell asks
for the resolved navigation: a value resolved during the render it is shown in cannot lag
that render, and the shell resolves its navigation more than once per page. When the store
is unreachable the item SHALL render with no badge rather than a stale or zero one, which
is what the shell already requires of an item with no count source.

Neither surface SHALL be derived from a stored status column, and neither SHALL be
computed by a second narrowing rule of its own. The assets and the assurances SHALL be read
as ONE snapshot of the store, and each surface here - the cell, the notice, the badge, and
the API row - SHALL narrow the assurance rows with the asset list of THAT snapshot. The
snapshot SHALL be taken at most once per request and shared by all four rather than once per
surface. Sharing keeps the read count down; it is not what makes the narrowing honest. The
accessible asset set is resolved per asset list (see the authz-enforcement capability), so a
surface reading its own snapshot is narrowed by that snapshot's own owner edges whatever order
the surfaces of a request run in.

The shared asset read that every organisation gate, every compliance write selector, and every
role-assignment guard draws on SHALL NOT be widened to carry these assurance rows. It names the
assets alone, and reuse SHALL serve it only from a snapshot the request has already taken for a
surface that needed one. A schema with no assurance table therefore leaves the badge unbadged and
`/vendors` on its unreachable-store response, and SHALL NOT make a gated compliance write or the
role-assignment page fail.

#### Scenario: Notice turns to a warning and names the count

- **WHEN** an authenticated user opens `/compliance/vendors` and two readable vendors
  hold an expiring or expired assurance
- **THEN** the summary notice renders in its warning appearance and states that two
  vendors have a lapsing certification

#### Scenario: An already-expired assurance raises both surfaces

- **WHEN** the only lapsing certification a readable vendor holds has already expired, and
  no readable vendor holds an expiring one
- **THEN** the summary notice still renders in its warning appearance counting that vendor,
  and the Vendors nav item still badges it, because an expired certificate is at least as
  actionable as one about to lapse

#### Scenario: Nothing lapsing leaves the notice neutral

- **WHEN** every readable vendor's assurances are valid, or no readable vendor holds
  one
- **THEN** the summary notice keeps its neutral appearance and says nothing about
  assurances

#### Scenario: Nav badge shows the owner-narrowed count

- **WHEN** two vendors hold a lapsing assurance and only one of them is in the
  caller's accessible asset set
- **THEN** the Vendors nav item badges `1`, counting only the vendor the caller may
  read

#### Scenario: A hidden vendor's assurance leaks into no surface

- **WHEN** a caller who may read one vendor but not another opens `/compliance/vendors`,
  and the vendor they may not read holds an expiring assurance
- **THEN** neither that vendor's id nor its assurance text appears anywhere in the rendered
  document, and the Vendors nav badge counts the readable vendor only, because each surface is
  derived from an accessible asset set resolved from its own snapshot's asset rows

#### Scenario: A vendor with no assurance is never counted

- **WHEN** readable vendors hold no certifications at all
- **THEN** the Vendors nav item renders with no badge, because a vendor with nothing on
  file is not an actionable count

#### Scenario: The count is resolved once per render

- **WHEN** one page render asks the shell for the resolved navigation more than once
- **THEN** the count is computed once for that request and the later asks reuse it, so the
  badge costs one store read per page rather than one per ask

#### Scenario: The register and the rail narrow from one snapshot

- **WHEN** the register page and the navigation rail both render in one request
- **THEN** both are served the request's one assurance snapshot, and the accessible asset set
  narrowing both is resolved from that snapshot's asset rows, so neither narrows assurance rows
  with owner edges from a different read

#### Scenario: The gate path does not read the assurance table

- **WHEN** an organisation gate resolves in a request that renders no vendor surface
- **THEN** the gate takes its asset list from a read of the assets alone, no assurance row is
  read, and the gate answers even when the assurance table is absent from the schema

#### Scenario: An unreachable store leaves the item unbadged

- **WHEN** the compliance store is unreachable while a page renders
- **THEN** the Vendors nav item renders with no badge and the page still renders,
  rather than the whole shell failing
