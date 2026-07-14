## MODIFIED Requirements

### Requirement: Web evidence-collector register page

The web app SHALL serve a read-only evidence-collector register page at
`/settings/evidence-collectors` that lists controls and, under each control, its
`evaluation` rule and its attached evidence-collectors: for each collector its
`type`, `vendor` (when set), `frequency`, `threshold` (when set), and any
type-specific `config`. The page SHALL read through the compliance store in-process
(like the Statement of Applicability and Vendor Register pages), SHALL be GET-only and
served in GitOps read-only mode, and SHALL require an authenticated user: an anonymous
browser GET SHALL redirect to `/login`. When the store is unreachable the page SHALL
render an in-page notice rather than an error page. The page SHALL be reachable from
the shell navigation. Unlike the per-org compliance pages, the register SHALL NOT
narrow its rows to the caller's accessible organisations: controls and collectors are
org-independent reference data, so any authenticated user - including one with zero
organisation grants under strict enforcement - SHALL see every control and every
collector.

The page file remains in the `Pages/Compliance` Razor Pages folder, so the existing
`/Compliance` folder authorization convention still gates it; only its route URL moves
under `/settings`. The prior `/compliance/evidence-collectors` URL is retired with no
redirect (a deliberate clean break in pre-release software).

#### Scenario: Register lists controls with their evaluation rule and collectors

- **WHEN** an authenticated user opens `/settings/evidence-collectors` with
  persisted controls and evidence-collectors
- **THEN** the page lists each control with its `evaluation` rule and, under it, each
  attached collector's `type`, `vendor` (when set), `frequency`, `threshold` (when
  set), and any `config`

#### Scenario: Control with no collectors renders without collectors

- **WHEN** a control has no attached collectors
- **THEN** the page renders the control and indicates it has no collectors rather than
  omitting it or failing

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests `/settings/evidence-collectors`
- **THEN** the response redirects to `/login` rather than rendering the register

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated user opens
  `/settings/evidence-collectors`
- **THEN** the page renders normally and is not blocked by read-only mode

#### Scenario: Zero-grant caller under strict enforcement sees every collector

- **WHEN** authorization runs in strict enforce mode and an authenticated user with
  no organisation grants opens `/settings/evidence-collectors`
- **THEN** the page renders every control and every collector, not narrowed to the
  caller's empty accessible-organisation set, because the register intentionally does
  not filter by accessible organisations
