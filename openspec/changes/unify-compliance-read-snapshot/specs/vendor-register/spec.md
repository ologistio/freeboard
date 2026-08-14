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
computed by a second narrowing rule of its own. Each surface SHALL draw its rows AND the
asset list that narrows them from ONE snapshot, named with exactly the sets that surface needs:
the register page names the assets, the assurances, and the unified scopes, because it renders
each excluded scope's justification behind the same vendor visibility; the rail badge and the
vendors endpoint name the assets and the assurances. Where one surface's snapshot already covers
another's sets, the second SHALL reuse it rather than take its own, so a request does not read
the same lists twice. Where it does not, each surface SHALL narrow with the asset list of its OWN
snapshot and SHALL NOT be served an accessible asset set resolved from another surface's asset
list. A count from one snapshot beside a table from another can lag by one sync, which
self-corrects on the next request; a surface narrowing its rows with another surface's owner
edges cannot be corrected and is what this rule forbids.

The shared asset read that the organisation gates, the route- and body-anchored compliance write
selectors, and the role-assignment guards draw on SHALL NOT be widened to carry these assurance
rows. It names the assets alone, and reuse SHALL serve it only from a snapshot the request has
already taken for a surface that needed one. The scope write's stored-owner lookup takes its own
snapshot rather than this one, and that snapshot SHALL NOT carry the assurances either. A schema with no assurance table therefore leaves the badge unbadged and
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
  document, and the Vendors nav badge counts the readable vendor only, because every
  surface is derived from an accessible asset set resolved from its own snapshot's asset rows

#### Scenario: A vendor with no assurance is never counted

- **WHEN** readable vendors hold no certifications at all
- **THEN** the Vendors nav item renders with no badge, because a vendor with nothing on
  file is not an actionable count

#### Scenario: The count is resolved once per render

- **WHEN** one page render asks the shell for the resolved navigation more than once
- **THEN** the count is computed once for that request and the later asks reuse it, so the
  badge costs one store read per page rather than one per ask

#### Scenario: The register and the rail share one snapshot

- **WHEN** the register page and the navigation rail both render in one request, and the
  page takes its snapshot - the assets, the assurances, and the scopes - before the rail asks
  for the assets and the assurances
- **THEN** the rail is served the page's snapshot rather than taking its own, because it
  covers every set the rail named, and the accessible asset set narrowing both is resolved
  from that snapshot's asset rows

#### Scenario: The gate path does not read the assurance table

- **WHEN** an organisation gate resolves in a request that renders no vendor surface
- **THEN** the gate takes its asset list from a read of the assets alone, no assurance row is
  read, and the gate answers even when the assurance table is absent from the schema

#### Scenario: A justification cannot outlive the visibility that admitted it

- **WHEN** the register renders while a GitOps sync commits a change to a vendor's `owner`
  that moves the vendor out of the caller's reach
- **THEN** the vendor's row, its assurances, and its excluded scopes' justifications are all
  decided by the owner edges of the same snapshot they were read with, so no justification
  survives a reparenting the same snapshot already hides

#### Scenario: An unreachable store leaves the item unbadged

- **WHEN** the compliance store is unreachable while a page renders
- **THEN** the Vendors nav item renders with no badge and the page still renders,
  rather than the whole shell failing

### Requirement: CLI vendor register command

The CLI SHALL provide a `freeboard vendor list` command that reads the vendor
register through the HTTP API (not by direct database access), mirroring the
existing HTTP-backed read commands and matching the web page's owner-narrowed read
model (read-model parity is required). It SHALL call the authenticated
`GET /api/v1/freeboard/vendors` and `GET /api/v1/freeboard/scopes` endpoints
using the configured API base URL and admin token, print each readable vendor
with its scopes (the unified scopes whose `subject` is that vendor): target,
disposition, and, for every `Out` scope, its justification. Because both endpoints narrow
by vendor `owner` (see the web requirement and the compliance-web-read capability), the
CLI prints only the vendors and scopes the caller may read; a vendor hidden by owner
narrowing has its scopes hidden too, so no hidden vendor id or `Out` justification is
printed. An `Out` exception SHALL never be printed without its justification.
The command SHALL follow the CLI exit-code convention: `0` on success, `1` on a
validation response, and `3` on an operational failure (unauthorized, forbidden,
server error, or connection failure).

The two responses are two requests, so they are two snapshots and a sync MAY commit between
them. The one-snapshot-per-decision rule SHALL NOT be read as a promise that they agree, and
the command SHALL NOT be given a combined endpoint to make them agree. The composition is
safe without one because each response is narrowed on the server against ITS OWN snapshot,
and the client joins scopes onto vendors BY VENDOR ID: a scope whose vendor is absent from the
vendor response SHALL be dropped rather than printed. A printed justification has therefore
passed BOTH narrowings, which is stricter than either alone. The residue is a listing that
lags by one sync - a vendor printed with none of its scopes, or a scope withheld - which is
staleness, not disclosure.

#### Scenario: vendor list prints vendors and justifications

- **WHEN** the user runs `freeboard vendor list` against a reachable API with
  readable vendors and vendor-subject scopes
- **THEN** the command prints each vendor with its scopes, including the
  justification for every `Out` scope, and exits `0`

#### Scenario: A scope with no matching vendor row prints nothing

- **WHEN** the scopes response carries a vendor-subject scope whose vendor id is absent from
  the vendors response, because a sync committed between the two requests
- **THEN** the command prints neither that vendor id nor that scope's justification, because
  the join is keyed on the vendor ids the vendors response admitted

#### Scenario: Operational failure maps to exit 3

- **WHEN** the user runs `freeboard vendor list` and the API is unauthorized,
  forbidden, unreachable, or returns a server error
- **THEN** the command prints an error and exits `3`
