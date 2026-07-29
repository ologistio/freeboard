## MODIFIED Requirements

### Requirement: Integration-connection web read view

The web app SHALL serve a read-only integration-connections page that lists each
connection with its `provider`, `base_url`, `discovery_cadence`, and its
`tokenResolvable` health flag composed at read time. The page SHALL read through the
compliance store in-process, SHALL be GET-only and served in GitOps read-only mode,
and SHALL require an authenticated user: an anonymous browser GET SHALL redirect to
`/login`. When the store is unreachable the page SHALL render an in-page notice
rather than an error page. When no connections exist the page SHALL render an empty
state.

The system SHALL also expose an authenticated read-only HTTP endpoint that returns
the connection list as JSON (each item carrying `id`, `provider`, `base_url`,
`discovery_cadence`, `vendor`, and the composed `token_resolvable` flag, and never
the token value), so the CLI can read connections without direct database access.

A connection's `vendor` SHALL be surfaced - on the page and in the JSON - only when that
vendor id is in the caller's ACCESSIBLE ASSET set (as defined by the authorization
enforcement capability). Otherwise the JSON SHALL emit `vendor` as `null` and the page
SHALL render it as it renders a connection with no vendor. This narrows one FIELD and
SHALL NOT narrow the row set: connections carry no organisation dimension, so every
authenticated caller still reads every connection. Without the field rule a hidden
vendor's id would be readable from a connection listing even though vendor readability
follows the `owner` edge and is fail-closed. The rule bounds the vendor ASSET id only:
`provider` and `base_url` describe the connection rather than the vendor asset and SHALL
NOT be narrowed by it.

#### Scenario: Connections page lists connections with health

- **WHEN** an authenticated user opens the integration-connections page and
  connections exist
- **THEN** the page lists each connection's provider, base URL, discovery cadence, and
  token-resolvable health, and never the token value

#### Scenario: An unreadable vendor id is withheld from both surfaces

- **WHEN** an authenticated caller with no grant reaching a vendor's `owner` reads the
  integration-connections page or its JSON endpoint and a connection names that vendor
- **THEN** the connection is still listed with its `id`, `provider`, `base_url`,
  `discovery_cadence`, and `token_resolvable`, and its `vendor` is `null` in the JSON and
  absent from the page, so the hidden vendor's id does not leak through the connection
  listing

#### Scenario: Anonymous request redirects to login

- **WHEN** an anonymous browser requests the integration-connections page
- **THEN** the response redirects to `/login`

#### Scenario: Store outage renders a notice

- **WHEN** the compliance store is unreachable while rendering the page
- **THEN** the page shows an in-page notice rather than an error page
