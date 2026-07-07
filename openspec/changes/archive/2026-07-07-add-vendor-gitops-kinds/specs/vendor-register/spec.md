## ADDED Requirements

### Requirement: Web vendor register page

The web app SHALL serve a read-only vendor register page at `/compliance/vendors`
that lists every persisted vendor and, under each vendor, its vendor-scopes: the
target (a requirement or a control), the disposition (`In` or `Out`), and, for every
`Out` vendor-scope, its `justification`. An `Out` exception SHALL never be shown
without its justification. The page SHALL read through the compliance store
in-process (like the Statement of Applicability page), SHALL be GET-only and served
in GitOps read-only mode, and SHALL require an authenticated user: an anonymous
browser GET SHALL redirect to `/login`. When the store is unreachable the page SHALL
render an in-page notice rather than an error page. The page SHALL be reachable from
the compliance navigation. Unlike the per-org compliance pages, the register SHALL
NOT narrow its rows to the caller's accessible organisations: vendors and
vendor-scopes are org-independent reference data (the flat model of design D4), so
any authenticated user - including one with zero organisation grants under strict
enforcement - SHALL see every vendor and every `Out` justification.

#### Scenario: Register lists vendors and their exceptions

- **WHEN** an authenticated user opens `/compliance/vendors` with persisted vendors
  and vendor-scopes
- **THEN** the page lists each vendor with its scopes, showing each scope's target
  and disposition, and the justification for every `Out` scope

#### Scenario: Out exceptions are never silent

- **WHEN** a vendor has a vendor-scope with `disposition: Out`
- **THEN** the page renders that exception together with its justification text

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests `/compliance/vendors`
- **THEN** the response redirects to `/login` rather than rendering the register

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user opens
  `/compliance/vendors`
- **THEN** the page renders normally and is not blocked by read-only mode

#### Scenario: Zero-grant caller under strict enforcement sees every vendor

- **WHEN** authorization runs in strict enforce mode and an authenticated user with
  no organisation grants opens `/compliance/vendors`
- **THEN** the page renders every persisted vendor and every `Out` justification,
  not narrowed to the caller's empty accessible-organisation set, because the
  register intentionally does not filter by accessible organisations

### Requirement: CLI vendor register command

The CLI SHALL provide a `freeboard vendor list` command that reads the vendor
register through the HTTP API (not by direct database access), mirroring the
existing HTTP-backed read commands. It SHALL call the authenticated
`GET /api/v1/freeboard/vendors` and `GET /api/v1/freeboard/vendor-scopes` endpoints
using the configured API base URL and admin token, and print each vendor with its
vendor-scopes: target, disposition, and, for every `Out` scope, its justification.
An `Out` exception SHALL never be printed without its justification. The command
SHALL follow the CLI exit-code convention: `0` on success, `1` on a validation
response, and `3` on an operational failure (unauthorized, forbidden, server error,
or connection failure).

#### Scenario: vendor list prints vendors and justifications

- **WHEN** the user runs `freeboard vendor list` against a reachable API with
  persisted vendors and vendor-scopes
- **THEN** the command prints each vendor with its scopes, including the
  justification for every `Out` scope, and exits `0`

#### Scenario: Operational failure maps to exit 3

- **WHEN** the user runs `freeboard vendor list` and the API is unauthorized,
  forbidden, unreachable, or returns a server error
- **THEN** the command prints an error and exits `3`
