## ADDED Requirements

### Requirement: Attestation-template read endpoint serves the persisted templates

The web app SHALL expose a read-only HTTP endpoint that returns the persisted
attestation-templates from the store, under the single `/api/v1/freeboard/` API
namespace, requiring an authenticated user (any logged-in user; no admin role). It
SHALL provide `GET /api/v1/freeboard/attestation-templates`. Each template SHALL
include its `id`, `title`, `control` id, `type`, `body` (null when unset), `fields`
(the ordered list of form fields, each with `id`, `label`, `type`, and `options` - an
array, empty for a non-`single-choice` field), `pass_mark` (null when unset), and
`quiz` (the ordered list of quiz items, each with `id`, `prompt`, and `options`; an
empty array when unset). The quiz items SHALL NOT include the `answer`: the correct
answer is a confidential quiz answer and is redacted from the read model, so it never
appears in the endpoint response even though every authenticated user may read this
endpoint. The endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only
mode, and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the
store is unavailable. Unlike the per-org resource endpoints (`/organisations`,
`/scopes`, `/requirement-scopes`), which narrow rows to the caller's accessible
organisation set via `IOrgAccess`, this endpoint intentionally does NOT filter:
attestation-templates are org-independent reference data (they carry no
`organisation` dimension), so any authenticated user - including one with zero org
access - may read every template. Responses SHALL be deterministically ordered by
`id`.

#### Scenario: Attestation-templates endpoint returns the persisted templates

- **WHEN** an authenticated client requests
  `GET /api/v1/freeboard/attestation-templates`
- **THEN** the response lists each template with its `id`, `title`, `control`, `type`,
  `body` (null when unset), `fields`, `pass_mark` (null when unset), and `quiz`,
  ordered by `id`

#### Scenario: Quiz answer is not exposed

- **WHEN** an authenticated client requests
  `GET /api/v1/freeboard/attestation-templates` for a training template whose quiz
  items each carry a correct `answer`
- **THEN** each returned quiz item includes `prompt` and `options` but no `answer` key,
  so the correct answer is not disclosed to the reader

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/attestation-templates`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests the
  attestation-templates endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Zero-grant caller under strict enforcement still reads every template

- **WHEN** authorization runs in strict enforce mode and an authenticated caller
  holding no organisation grants requests
  `GET /api/v1/freeboard/attestation-templates`
- **THEN** the endpoint returns every persisted template, not narrowed to the empty
  accessible-organisation set that strict enforcement produces for a zero-grant
  caller, because the attestation-template endpoint intentionally skips the
  `IOrgAccess` narrowing that the per-org resource endpoints apply

## MODIFIED Requirements

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT auto-connect to MySQL at startup, so an unreachable store
SHALL NOT crash the app. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`. The per-kind key set includes
`vendors`, `vendorScopes`, `evidenceCollectors`, and `attestationTemplates` alongside
the pre-existing kinds:

```json
{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null, "evidenceCollectors": null, "attestationTemplates": null } }
```

`null` (not omitted, not `{}`, not `0`) marks each count as unknown rather than
zero.

Authentication precedes every compliance read and shares the same backing store as
the compliance store. So these degradation responses (HTTP 503 for the resource
reads, HTTP 200 with an all-null persisted summary for `compliance/status`) describe
the case where the request is authenticated and only the compliance store is
unavailable to it. A full database outage that also fails authentication surfaces
first as an authentication failure (HTTP 401) - the request never reaches the
compliance handler - not as these compliance degradation responses.

The vendor, evidence-collector, and attestation-template read endpoints
(`GET /api/v1/freeboard/vendors`, `GET /api/v1/freeboard/vendor-scopes`,
`GET /api/v1/freeboard/evidence-collectors`, and
`GET /api/v1/freeboard/attestation-templates`) tolerate the unreachable store the
same way as the other resource reads: HTTP 503 with an RFC 7807 problem body, never
an unhandled exception.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "requirementScopes": null, "vendors": null, "vendorScopes": null, "evidenceCollectors": null, "attestationTemplates": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/requirements`, `/api/v1/freeboard/organisations`,
  `/api/v1/freeboard/scopes`, `/api/v1/freeboard/requirement-scopes`,
  `/api/v1/freeboard/vendors`, `/api/v1/freeboard/vendor-scopes`,
  `/api/v1/freeboard/evidence-collectors`, or
  `/api/v1/freeboard/attestation-templates`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, requirements, organisations, scopes,
requirement-scopes, vendors, vendor-scopes, evidence-collectors, and
attestation-templates are currently persisted in the store. This is the general
compliance read surface; the persisted counts live here, NOT on
`GET /api/v1/freeboard/gitops/status` (which stays a GitOps concern reporting
read-only mode and repository URL). The summary SHALL be a `persisted` object with
per-kind counts:

```json
{ "persisted": { "standards": 3, "controls": 12, "requirements": 35, "organisations": 4, "scopes": 2, "requirementScopes": 3, "vendors": 5, "vendorScopes": 4, "evidenceCollectors": 8, "attestationTemplates": 6 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, requirements, organisations, scopes, requirement-scopes,
  vendors, vendor-scopes, evidence-collectors, and attestation-templates
