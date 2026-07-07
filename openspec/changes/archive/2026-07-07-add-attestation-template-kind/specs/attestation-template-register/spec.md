## ADDED Requirements

### Requirement: Web attestation-template register page

The web app SHALL serve a read-only attestation-template register page at
`/compliance/attestation-templates` that lists each control that has at least one
persisted attestation-template and, under each control, its templates: for each
template its `type`, its `body` (when set), its `fields` (each field's `label`,
`type`, and `options` when set), and, for a `training` template, its `pass_mark` and
its `quiz` items (each item's `prompt` and `options`). The page SHALL NOT render the
quiz `answer` (the correct answer is redacted from the read model). The page SHALL
render the markdown `body` as HTML-encoded text and SHALL NOT emit it as raw or
unsanitized HTML, so a git-authored body cannot inject markup into the page. The page
SHALL read
through the compliance store in-process (like the Statement of Applicability, Vendor
Register, and Evidence Collector pages), SHALL be GET-only and served in GitOps
read-only mode, and SHALL require an authenticated user: an anonymous browser GET
SHALL redirect to `/login`. When the store is unreachable the page SHALL render an
in-page notice rather than an error page. The page SHALL be reachable from the
compliance navigation. Unlike the per-org compliance pages, the register SHALL NOT
narrow its rows to the caller's accessible organisations: controls and templates are
org-independent reference data, so any authenticated user - including one with zero
organisation grants under strict enforcement - SHALL see every template.

#### Scenario: Register lists controls with their attestation templates

- **WHEN** an authenticated user opens `/compliance/attestation-templates` with
  persisted controls and attestation-templates
- **THEN** the page lists each control that has templates and, under it, each
  template's `type`, `body` (when set), `fields`, and, for a `training` template, its
  `pass_mark` and `quiz` items (prompt and options), and does not render any quiz
  `answer`

#### Scenario: Markdown body is HTML-encoded

- **WHEN** an authenticated user opens `/compliance/attestation-templates` and a
  template's `body` contains HTML markup (for example a `<script>` tag)
- **THEN** the page renders the markup as HTML-encoded text rather than emitting it as
  live HTML, so the body cannot inject script or other markup into the page

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests `/compliance/attestation-templates`
- **THEN** the response redirects to `/login` rather than rendering the register

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user opens
  `/compliance/attestation-templates`
- **THEN** the page renders normally and is not blocked by read-only mode

#### Scenario: Store unreachable renders a notice

- **WHEN** the compliance store is unreachable and an authenticated user opens
  `/compliance/attestation-templates`
- **THEN** the page renders an in-page notice rather than an error page

#### Scenario: Zero-grant caller under strict enforcement sees every template

- **WHEN** authorization runs in strict enforce mode and an authenticated user with
  no organisation grants opens `/compliance/attestation-templates`
- **THEN** the page renders every persisted template, not narrowed to the caller's
  empty accessible-organisation set, because the register intentionally does not
  filter by accessible organisations

### Requirement: CLI attestation-template register command

The CLI SHALL provide a `freeboard attestation-template list` command that reads the
attestation-template register through the HTTP API (not by direct database access),
mirroring the existing HTTP-backed read commands. It SHALL call the authenticated
`GET /api/v1/freeboard/attestation-templates` endpoint using the configured API base
URL and admin token, and print each template with its `control`, `type`, `body`
indicator, `fields`, and, for a `training` template, its `pass_mark` and `quiz`
items (each item's prompt and options). The command SHALL NOT print any quiz `answer`;
the API returns none. The command SHALL follow the CLI exit-code convention: `0` on success, `1` on
a validation response, and `3` on an operational failure (unauthorized, forbidden,
server error, or connection failure).

#### Scenario: attestation-template list prints templates

- **WHEN** the user runs `freeboard attestation-template list` against a reachable
  API with persisted attestation-templates
- **THEN** the command prints each template with its control, type, fields, and (for
  training) pass mark and quiz, and exits `0`

#### Scenario: Operational failure maps to exit 3

- **WHEN** the user runs `freeboard attestation-template list` and the API is
  unauthorized, forbidden, unreachable, or returns a server error
- **THEN** the command prints an error and exits `3`
