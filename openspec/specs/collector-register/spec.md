# collector-register Specification

## Purpose
TBD - created by archiving change object-model-v2-collector-merge. Update Purpose after archive.
## Requirements
### Requirement: Web collector register page

The web app SHALL serve a read-only collector register page at `/settings/collectors`
that lists controls and, under each control, its `evaluation` rule and its attached
collectors. For each collector the page SHALL show its `type`, its `provider` (when set),
its `vendor` (when set AND readable by the caller), its `frequency`, and its `threshold`
(when set); and, for a `manual` or `training` collector, the form carried in its `config` -
the `body` (when set), the `fields` (each field's `label`, `type`, and `options` when set),
and, for a `training` collector, the `pass_mark` and the `quiz` items (each item's `prompt`
and `options`). For an `integration` collector the page SHALL show the `checks` carried in
its `config` (each check's `name` and `severity`), so a reader can see which checks the
collector tracks.

A collector's `vendor` SHALL be shown only when that vendor id is in the caller's
ACCESSIBLE ASSET set (as defined by the authorization enforcement capability), and SHALL
be omitted otherwise, rendering exactly as a collector with no vendor renders. This
narrows one FIELD and SHALL NOT narrow the row: the collector is still listed. Without it
a hidden vendor's id would be readable from the register even though vendor readability
follows the `owner` edge. The rule bounds the vendor ASSET id only; a collector's `title`
and `config` describe the integration rather than the vendor asset and SHALL NOT be
narrowed by it.

This one page replaces the retired evidence-collector and attestation-template register
pages. The page SHALL NOT render a quiz `answer` (the correct answer is redacted from the
read model). The page SHALL render the markdown `body` as HTML-encoded text and SHALL NOT
emit it as raw or unsanitized HTML, so a git-authored body cannot inject markup into the
page. The page SHALL NOT render a collector's `connection`.

The page SHALL read through the compliance store in-process (like the Statement of
Applicability and Vendor Register pages), SHALL be GET-only and served in GitOps read-only
mode, and SHALL require an authenticated user: an anonymous browser GET SHALL redirect to
`/login`. When the store is unreachable the page SHALL render an in-page notice rather
than an error page. The page SHALL be reachable from the shell navigation as a single
`Collectors` entry. Unlike the per-org compliance pages, the register SHALL NOT narrow its
ROWS to the caller's accessible set: controls and collectors are org-independent
reference data, so any authenticated user - including one with zero organisation grants
under strict enforcement - SHALL see every control and every collector.

The page file remains in the `Pages/Compliance` Razor Pages folder, so the existing
`/Compliance` folder authorization convention still gates it; only its route URL is
`/settings/collectors`. The prior `/settings/evidence-collectors` and
`/settings/attestation-templates` URLs are retired with no redirect (a deliberate clean
break in pre-release software).

#### Scenario: Register lists controls with their evaluation rule and collectors

- **WHEN** an authenticated user opens `/settings/collectors` with persisted controls and
  collectors
- **THEN** the page lists each control with its `evaluation` rule and, under it, each
  attached collector's `type`, `provider` (when set), `vendor` (when set and readable),
  `frequency`, and `threshold` (when set)

#### Scenario: An unreadable vendor is omitted but the collector still renders

- **WHEN** an authenticated user with no grant reaching a vendor's `owner` opens
  `/settings/collectors` and a collector names that vendor
- **THEN** the collector is still listed under its control with all its other fields and
  shows no vendor, so the hidden vendor's id does not appear on the register

#### Scenario: Manual and training collectors show their form

- **WHEN** an authenticated user opens `/settings/collectors` and a control has a `manual`
  collector with fields and a `training` collector with a pass mark and quiz
- **THEN** the page shows the manual collector's body and fields and the training
  collector's pass mark and quiz prompts and options, and renders no quiz `answer`

#### Scenario: Integration collectors show their tracked checks

- **WHEN** an authenticated user opens `/settings/collectors` and a control has an
  `integration` collector declaring checks in its `config`
- **THEN** the page shows each tracked check's `name` and `severity` under that collector,
  and shows no `connection`

#### Scenario: Control with no collectors renders without collectors

- **WHEN** a control has no attached collectors
- **THEN** the page renders the control and indicates it has no collectors rather than
  omitting it or failing

#### Scenario: Markdown body is HTML-encoded

- **WHEN** an authenticated user opens `/settings/collectors` and a collector's `config`
  `body` contains HTML markup (for example a `<script>` tag)
- **THEN** the page renders the markup as HTML-encoded text rather than emitting it as
  live HTML, so the body cannot inject script or other markup into the page

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests `/settings/collectors`
- **THEN** the response redirects to `/login` rather than rendering the register

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user opens
  `/settings/collectors`
- **THEN** the page renders normally and is not blocked by read-only mode

#### Scenario: Store unreachable renders a notice

- **WHEN** the compliance store is unreachable and an authenticated user opens
  `/settings/collectors`
- **THEN** the page renders an in-page notice rather than an error page

#### Scenario: Zero-grant caller under strict enforcement sees every collector

- **WHEN** authorization runs in strict enforce mode and an authenticated user with
  no organisation grants opens `/settings/collectors`
- **THEN** the page renders every control and every collector, not narrowed to the
  caller's empty accessible asset set, because the register intentionally does
  not filter its rows by that set - while no collector shows a vendor, since none is in
  that empty set

#### Scenario: Retired register routes no longer answer

- **WHEN** a browser requests `/settings/evidence-collectors` or
  `/settings/attestation-templates`
- **THEN** neither route renders a page and neither redirects to the new register

### Requirement: CLI collector register command

The CLI SHALL provide a `freeboard collector list` command that reads the
collector register through the HTTP API (not by direct database access),
mirroring the existing HTTP-backed read commands. It SHALL call the authenticated
`GET /api/v1/freeboard/controls` and `GET /api/v1/freeboard/collectors`
endpoints using the configured API base URL and admin token, and print each control
with its `evaluation` rule and its attached collectors (type, provider, vendor,
frequency, threshold) and, from each collector's `config`, either an `integration`
collector's tracked checks (each check's name and severity) or a `manual` or `training`
collector's `body` indicator, fields, pass mark, and quiz items (each item's prompt and
options). The `body` indicator states only whether a body is present, carrying forward what
the retired `attestation-template list` printed; the markdown itself is a page concern. The
command SHALL NOT print any quiz `answer`; the API returns none. This one command replaces the retired
`freeboard attestation-template list`, and the `attestation-template` command group SHALL
be removed. The command SHALL follow the CLI exit-code convention: `0` on success,
`1` on a validation response, and `3` on an operational failure (unauthorized,
forbidden, server error, or connection failure).

#### Scenario: collector list prints controls with their collectors

- **WHEN** the user runs `freeboard collector list` against a reachable API with
  persisted controls and collectors
- **THEN** the command prints each control with its evaluation rule and its attached
  collectors, and exits `0`

#### Scenario: collector list prints an attestation collector's form

- **WHEN** the user runs `freeboard collector list` and the register holds a `manual`
  collector with a body and fields and a `training` collector with a pass mark and quiz but
  no body
- **THEN** the command prints the manual collector's body indicator and fields and the
  training collector's no-body indicator, pass mark, and quiz prompts and options, and
  prints no quiz answer

#### Scenario: attestation-template command group is gone

- **WHEN** the user runs `freeboard attestation-template list`
- **THEN** the command is not registered and the CLI reports an unknown command rather
  than listing templates

#### Scenario: Operational failure maps to exit 3

- **WHEN** the user runs `freeboard collector list` and the API is unauthorized,
  forbidden, unreachable, or returns a server error
- **THEN** the command prints an error and exits `3`

