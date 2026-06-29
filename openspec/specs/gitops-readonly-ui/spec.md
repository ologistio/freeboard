# gitops-readonly-ui Specification

## Purpose
TBD - created by archiving change add-gitops-config-management. Update Purpose after archive.
## Requirements
### Requirement: GitOps read-only mode flag

The web app SHALL read a configuration flag `Freeboard:GitOps:ReadOnly`
(boolean, default false). When true, the app is in GitOps mode: git is the
source of truth and the UI must not accept changes. The web app SHALL also read
`Freeboard:GitOps:RepositoryUrl` (string, optional, default empty), the git repo
URL surfaced to callers; when empty it is omitted from responses.

#### Scenario: Default is off

- **WHEN** the flag is not set
- **THEN** the app behaves normally and does not advertise GitOps mode

#### Scenario: Flag enables GitOps mode

- **WHEN** `Freeboard:GitOps:ReadOnly` is true
- **THEN** the app reports that it is in GitOps mode

### Requirement: Mutating requests rejected in GitOps mode

When in GitOps mode the web app SHALL reject mutating HTTP requests (methods
POST, PUT, PATCH, DELETE) with HTTP 409 Conflict and an RFC 7807 problem-details
body that states the instance is GitOps-managed and changes must be made in the
git repository. The response SHALL set `Content-Type: application/problem+json`
and the body SHALL include at least the RFC 7807 members `type`, `title`,
`status` (value `409`), and `detail`. When `Freeboard:GitOps:RepositoryUrl` is
set, the body SHALL include the repo URL. Enforcement SHALL be server-side at the
HTTP layer (not merely disabled UI controls). Non-mutating requests (GET, HEAD,
OPTIONS) SHALL be unaffected. Requests matched to a specific AUTH ENDPOINT (login,
logout, password change/reset, MFA, sudo/step-up, session management) SHALL be EXEMPT
from this rejection, because they are auth-state operations, not GitOps-managed
compliance writes; blocking them would make a GitOps read-only instance impossible to
authenticate against. The exemption SHALL be scoped to the actual auth endpoints by an
endpoint marker (metadata) the middleware inspects via `context.GetEndpoint()?.
Metadata`, NOT to the whole `/api/v1/freeboard/` path prefix (which is the global API
prefix); a mutating request to a non-auth route that happens to share that prefix
SHALL still be rejected with `409`. Because the matched endpoint is only available
after endpoint routing, the middleware SHALL run AFTER `UseRouting()` so
`context.GetEndpoint()` is populated; this is a required pipeline ordering change from
the current registration (which runs before routing).

#### Scenario: Mutating request blocked

- **WHEN** the app is in GitOps mode and a POST request to a non-auth path arrives
- **THEN** the app responds 409 Conflict with `Content-Type:
  application/problem+json` and a body containing `type`, `title`, `status`
  (`409`), and `detail`, including the repo URL when `RepositoryUrl` is set, and
  the request does not reach any handler that would change state

#### Scenario: Read request allowed

- **WHEN** the app is in GitOps mode and a GET request arrives
- **THEN** the request is served normally

#### Scenario: Disabled mode does not intercept mutations

- **WHEN** the app is not in GitOps mode and a POST request arrives at a
  test-only mutating endpoint
- **THEN** the read-only middleware does not intercept it; the downstream
  response is preserved and is not the 409 GitOps problem-details response

#### Scenario: Marked auth endpoint exempt in GitOps mode

- **WHEN** the app is in GitOps mode and a `POST /api/v1/freeboard/auth/login` (a
  marked auth endpoint) arrives
- **THEN** the read-only middleware does not return the 409 GitOps response and the
  request reaches the auth handler

#### Scenario: Non-auth route under the API prefix still blocked

- **WHEN** the app is in GitOps mode and a mutating request to a non-auth route under
  `/api/v1/freeboard/` (without the auth-endpoint marker) arrives
- **THEN** the app still responds 409 Conflict and the request does not reach a
  state-changing handler

### Requirement: GitOps mode discoverable

The web app SHALL expose whether it is in GitOps mode so a client can show a
read-only banner. A `GET /api/v1/freeboard/gitops/status` endpoint SHALL include a
boolean indicating GitOps mode, and SHALL include the repo URL when
`Freeboard:GitOps:RepositoryUrl` is set. This endpoint moves under the
`/api/v1/freeboard/` namespace from its previous `/api/gitops/status` path (a breaking
path change for the status endpoint).

#### Scenario: Status reports GitOps mode on

- **WHEN** the app is in GitOps mode and a client requests
  `GET /api/v1/freeboard/gitops/status`
- **THEN** the response indicates GitOps mode is on, and includes the repo URL
  when `RepositoryUrl` is set

#### Scenario: Status reports GitOps mode off

- **WHEN** the app is not in GitOps mode and a client requests
  `GET /api/v1/freeboard/gitops/status`
- **THEN** the response indicates GitOps mode is off

