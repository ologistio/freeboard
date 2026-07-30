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

#### Scenario: vendor list prints vendors and justifications

- **WHEN** the user runs `freeboard vendor list` against a reachable API with
  readable vendors and vendor-subject scopes
- **THEN** the command prints each vendor with its scopes, including the
  justification for every `Out` scope, and exits `0`

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

