# vendor-register Specification

## Purpose
TBD - created by archiving change add-vendor-gitops-kinds. Update Purpose after archive.
## Requirements
### Requirement: Web vendor register page

The web app SHALL serve a read-only vendor register page at `/compliance/vendors`
that lists every vendor the caller may read and, under each vendor, its
scopes (unified `Scope` rows whose `subject` is that vendor): the target (a requirement or
a control), the disposition (`In` or `Out`), and, for every `Out` scope, its
`justification`. An `Out` exception SHALL never be shown without its justification.
Vendors are `Asset` rows of `type: Vendor` read through the compliance store in-process,
and a vendor's scopes are read from the same unified scope set (there is no separate
vendor-scope store). The page SHALL be GET-only and served in GitOps read-only mode, and
SHALL require an authenticated user: an anonymous browser GET SHALL redirect to `/login`.
When the store is unreachable the page SHALL render an in-page notice rather than an error
page. The page SHALL be reachable from the compliance navigation.

The register SHALL narrow its rows to the vendors the caller may read: a vendor is
readable when it is in the caller's accessible asset set, which admits it exactly when
its `owner` (a `Company`/`Department` asset) resolves into the caller's organisation
union. The prior global-readability behavior - every authenticated user seeing every
vendor regardless of grants - SHALL NOT apply. A
vendor whose `owner` is not in the caller's accessible set (including a vendor with
no readable owner) SHALL NOT be listed for that caller.

The narrowing SHALL extend to the vendor's scopes: those scopes and their `Out`
justifications SHALL be narrowed by the SAME vendor-owner rule, so a vendor hidden
from the caller has its scopes hidden too. A caller SHALL NOT receive any vendor-subject
scope (its target, disposition, or justification) for a vendor whose `owner`
is not in the caller's accessible set. Otherwise the hidden vendor's id and its
exception rationale would leak through the scope list even though its register
row is suppressed. Read-access is fail-closed on every vendor-register and
scope surface: a missing or dangling owner hides the vendor AND its scopes.

#### Scenario: Register lists readable vendors and their exceptions

- **WHEN** an authenticated user opens `/compliance/vendors` with vendors whose
  `owner` is in the user's accessible set
- **THEN** the page lists each such vendor with its scopes, showing each scope's
  target and disposition, and the justification for every `Out` scope

#### Scenario: Out exceptions are never silent

- **WHEN** a readable vendor has a scope with `disposition: Out`
- **THEN** the page renders that exception together with its justification text

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests `/compliance/vendors`
- **THEN** the response redirects to `/login` rather than rendering the register

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user opens
  `/compliance/vendors`
- **THEN** the page renders normally and is not blocked by read-only mode

#### Scenario: Vendor with an unreadable owner is hidden

- **WHEN** an authenticated user opens `/compliance/vendors` and a vendor's `owner`
  does not resolve into the user's organisation union (or the vendor has no readable
  owner)
- **THEN** that vendor is not listed, because vendor readability follows the owner
  edge and is not global

#### Scenario: An owner-excluded caller sees neither the vendor nor its scopes

- **WHEN** an authenticated user with no grant reaching a vendor's `owner` reads the
  vendor register and its scopes
- **THEN** neither the vendor row nor any of that vendor's vendor-subject scopes or `Out`
  justifications are returned, so the hidden vendor's id and exception rationale do
  not leak through the scope list

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

### Requirement: Register, API, and CLI carry vendor tier and data classes

The vendor register page, the vendors API row, and the CLI vendor listing SHALL all
carry a vendor's `tier` and its `data_classes`. All three read the same store rows,
and read-model parity is an acceptance rule for this project, so the three surfaces
SHALL ship together rather than one leading the others.

The web register at `/compliance/vendors` SHALL render each readable vendor's tier
in its Tier column and its data classes in its Data column. A vendor with no tier
SHALL render the explicit empty "Not tracked" rather than a guessed or defaulted
value, and so SHALL a vendor with no data classes. Both facets SHALL render as
neutral-toned tags. Red SHALL NOT be used for any tier or data class, because red is
reserved for failing and overdue states and a static attribute is neither; amber
SHALL NOT be used either, for the same reason. The tag word carries the meaning. Data
classes SHALL NOT be tone-ranked against each other, because they name regulatory
regimes rather than severities and no ordering between them exists.

The register's row order SHALL NOT change: it remains vendor id. Tier is editable
config, so ordering by it would reshuffle the register whenever a vendor is
re-tiered. This change SHALL add no filtering, grouping, or sorting control, and
nothing SHALL read either field to derive a status, a review cadence, or a score.

`GET /api/v1/freeboard/vendors` SHALL return both fields on each vendor row it
already returns, keeping the endpoint's existing owner-narrowed read model: a vendor
hidden by owner narrowing discloses neither its tier nor its data classes, because
the row itself is absent.

`freeboard vendor list` SHALL print both on each vendor's existing line, printing
`-` for an absent value, matching the CLI's established placeholder for a missing
optional field. The command's exit-code behavior is unchanged: `0` on success, `1`
on a validation response, `3` on an operational failure.

#### Scenario: Register renders tier and data classes

- **WHEN** an authenticated user opens `/compliance/vendors` and a readable vendor
  carries a tier and one or more data classes
- **THEN** the Tier column shows that tier and the Data column shows each data
  class, every tag rendered in the neutral tone

#### Scenario: Absent values render as an explicit empty

- **WHEN** a readable vendor carries no tier, or no data classes
- **THEN** the corresponding column reads "Not tracked" rather than a default,
  a guess, or a blank cell

#### Scenario: Row order is unchanged

- **WHEN** the register lists vendors of differing tiers
- **THEN** the rows are ordered by vendor id, unaffected by tier

#### Scenario: Vendors API returns both fields

- **WHEN** an authorized caller requests `GET /api/v1/freeboard/vendors`
- **THEN** each returned vendor row carries its tier and its data classes, and a
  vendor excluded by owner narrowing is absent along with both of its values

#### Scenario: CLI prints both, with a placeholder when absent

- **WHEN** the user runs `freeboard vendor list` against a reachable API
- **THEN** each vendor line carries its tier and its data classes, printing `-` for
  either value the vendor does not carry, and the command exits `0`

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

