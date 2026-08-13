## ADDED Requirements

### Requirement: Register, API, and CLI carry vendor assurances

The vendor register page, the vendors API row, and the CLI vendor listing SHALL all
carry a vendor's assurances. All three read the same store rows, and read-model parity
is an acceptance rule for this project, so the three surfaces SHALL ship together rather
than one leading the others.

The web register at `/compliance/vendors` SHALL render one provenance stamp per
assurance in each readable vendor's Assurance column, naming the standard's title and
the state of its expiry. A vendor with no assurance SHALL render the explicit empty
"None on file" rather than a guessed or defaulted value, and SHALL NOT render a stamp:
a stamp is a provenance claim, and there is no provenance for a value that does not
exist. Each assurance SHALL be rendered as its own mark rather than rolled up into a
count, matching the Data column, because hiding certifications behind a number
fabricates completeness (O2).

A stamp SHALL carry the mark's untinted neutral rendering when the assurance is `Valid`,
amber when `Expiring`, and red when `Expired`. Green SHALL NOT be used for any assurance: a
certification is a fact on file rather than a Freeboard verdict, and the row's Status column
correctly continues to read "Not evaluated" while no vendor review exists. Red is earned
here under S3 because a lapsed expiry is an overdue fact rather than a judgement.

The stamp's words SHALL differ across the three states, so the state survives with colour
removed (S2). The two states that ask for action SHALL state the expiry relatively when near
and absolutely when far, with the expired state stated in words (T6). `Valid` SHALL state
its expiry absolutely at every distance, because a warning window of zero days makes an
assurance expiring tomorrow `Valid`, and a relative `Valid` would then read word-for-word
like an `Expiring` one, leaving colour as the only difference between them.

The register's row order SHALL NOT change: it remains vendor id. This change SHALL add
no filtering, grouping, or sorting control, and nothing SHALL read an assurance to
derive a status, a review cadence, a scope decision, or a score.

`GET /api/v1/freeboard/vendors` SHALL return, on each vendor row it already returns,
that vendor's assurances, each carrying its `standard`, its `expires`, and its derived
`status`. The endpoint SHALL derive the status rather than returning the expiry alone,
so the warning window lives in one process and the web page and the CLI cannot
disagree about it. The endpoint SHALL keep its existing owner-narrowed read model: a
vendor hidden by owner narrowing discloses none of its assurances, because the row
itself is absent.

`freeboard vendor list` SHALL print each of a vendor's assurances under that vendor,
carrying the standard id, the expiry, and the status, alongside the vendor's existing
scope lines. A vendor with no assurance SHALL print no assurance line. The command's
exit-code behavior is unchanged: `0` on success, `1` on a validation response, `3` on
an operational failure.

#### Scenario: Register renders one toned stamp per assurance

- **WHEN** an authenticated user opens `/compliance/vendors` and a readable vendor
  holds a valid certification, an expiring one, and an expired one
- **THEN** the Assurance column shows three stamps, each naming its standard and its
  expiry, rendered untinted, amber, and red respectively, with a different word per state

#### Scenario: A vendor with no assurance reads as an explicit empty

- **WHEN** a readable vendor holds no certification
- **THEN** its Assurance column reads "None on file" with no stamp, rather than a
  blank cell, a guess, or a fabricated provenance

#### Scenario: Row order is unchanged

- **WHEN** the register lists vendors whose assurances are in differing states
- **THEN** the rows are ordered by vendor id, unaffected by assurance state

#### Scenario: Vendors API returns each assurance with its derived status

- **WHEN** an authorized caller requests `GET /api/v1/freeboard/vendors`
- **THEN** each returned vendor row carries its assurances with their standard,
  expiry, and derived status, and a vendor excluded by owner narrowing is absent along
  with all of its assurances

#### Scenario: CLI prints each assurance under its vendor

- **WHEN** the user runs `freeboard vendor list` against a reachable API
- **THEN** each vendor's assurances print under it with their standard, expiry, and
  status, a vendor with none prints no assurance line, and the command exits `0`

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
as ONE snapshot of the store, and that snapshot SHALL be taken at most once per request and
shared by every surface here - the cell, the notice, the badge, and the API row - rather
than once per surface. A snapshot per surface would not be enough: the accessible asset set
is resolved once per request, from whichever asset list reaches the authorization seam
first, so one surface would end up narrowing its assurance rows with another surface's owner
edges. The request's shared asset read SHALL be served from that snapshot, so a consumer of
that read resolves the accessible set from the same asset rows the assurances were read
with.

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
  surface is derived from the one accessible asset set

#### Scenario: A vendor with no assurance is never counted

- **WHEN** readable vendors hold no certifications at all
- **THEN** the Vendors nav item renders with no badge, because a vendor with nothing on
  file is not an actionable count

#### Scenario: The count is resolved once per render

- **WHEN** one page render asks the shell for the resolved navigation more than once
- **THEN** the count is computed once for that request and the later asks reuse it, so the
  badge costs one store read per page rather than one per ask

#### Scenario: The register and the rail narrow from one snapshot

- **WHEN** the register page and the navigation rail both render in one request, and the
  page reads the snapshot before the rail does
- **THEN** the rail reads the same snapshot rather than taking its own, and the accessible
  asset set narrowing both is resolved from that snapshot's asset rows, so the rail cannot
  narrow assurance rows with owner edges from a different read

#### Scenario: An unreachable store leaves the item unbadged

- **WHEN** the compliance store is unreachable while a page renders
- **THEN** the Vendors nav item renders with no badge and the page still renders,
  rather than the whole shell failing
