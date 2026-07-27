## MODIFIED Requirements

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
readable when its `owner` (a `Company`/`Department` asset) is in the caller's
accessible-organisation set. The prior global-readability behavior - every
authenticated user seeing every vendor regardless of grants - SHALL NOT apply. A
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
  is not in the user's accessible-organisation set (or the vendor has no readable
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
