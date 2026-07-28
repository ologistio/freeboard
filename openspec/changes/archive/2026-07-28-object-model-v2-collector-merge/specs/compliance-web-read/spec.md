## ADDED Requirements

### Requirement: Collector read endpoint serves the persisted collectors

The web app SHALL expose a read-only HTTP endpoint that returns the persisted
collectors from the store, under the single `/api/v1/freeboard/` API
namespace, requiring an authenticated user (any logged-in user; no admin role). It
SHALL provide `GET /api/v1/freeboard/collectors`. Each collector SHALL
include its `id`, `title`, `control` id, `vendor` id (null when unset), `type`,
`provider` (null when unset), `frequency`, `threshold` (null when unset), and `config`
(the typed type-specific config object).

The `config` object SHALL carry a key only for a member the collector actually has, and
SHALL omit every absent member rather than emitting it as `null` or as an empty array. So
the object MAY carry `body` (a `manual` or `training` collector's markdown), `fields` (the
ordered list of form fields, each with `id`, `label`, `type`, and `options` - an array,
empty for a non-`single-choice` field), `pass_mark`, `quiz` (the ordered list of quiz
items, each with `id`, `prompt`, and `options`), and `checks` (an `integration` collector's
ordered tracked checks, each with `source_key`, `name`, and `severity`). It SHALL be an
empty object for a collector whose registered `(type, provider)` config schema accepts no
key, and equally for one that authored none of the keys its schema does accept. This
mirrors the storage rule that an absent config member is omitted rather than stored as
JSON null, so the response object is the stored object with the answer removed and the keys
re-cased.

This omit-absent rule applies INSIDE `config` only. The collector's own nullable top-level
fields - `vendor`, `provider`, and `threshold` - SHALL still be emitted as explicit `null`
when unset, because the top level is a fixed response shape.

The quiz items SHALL NOT include the `answer`: the correct
answer is confidential authoring data redacted from the read model, so it never
appears in the endpoint response even though every authenticated user may read this
endpoint. An integration collector's `checks` ARE exposed: they are non-secret,
git-authored declarations of what the collector tracks, and they are exposed because they
are a `config` key like any other, not special-cased into or out of the response. The
endpoint SHALL NOT expose the collector's `connection`.

This single endpoint replaces the removed `GET /api/v1/freeboard/evidence-collectors` and
`GET /api/v1/freeboard/attestation-templates`. The endpoint SHALL read through the
`IComplianceStore` abstraction, SHALL be GET-only and unaffected by GitOps read-only
mode, and SHALL return the RFC 7807 / HTTP 503 unreachable-store response when the
store is unavailable. Unlike the per-org resource endpoints (`/organisations`
and `/scopes`), which narrow rows to the caller's accessible
organisation set via `IOrgAccess`, this endpoint intentionally does NOT filter:
collectors are org-independent reference data (they carry no `organisation`
dimension), so any authenticated user - including one with zero org access - may read
every collector. Responses SHALL be deterministically ordered by `id`.

#### Scenario: Collectors endpoint returns the persisted collectors

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors`
- **THEN** the response lists each collector with its `id`, `title`, `control`,
  `vendor` (null when unset), `type`, `provider` (null when unset), `frequency`,
  `threshold` (null when unset), and typed `config`, ordered by `id`

#### Scenario: Manual and training collectors carry their form in config

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` and the
  store holds a `manual` collector with fields and a `training` collector with a pass mark
  and quiz
- **THEN** each returned collector carries its `body`, `fields`, `pass_mark`, and `quiz`
  under `config`, an `integration` collector carries its `checks` under `config`, and a
  collector of a type whose config schema accepts no key carries an empty `config`

#### Scenario: Absent config members are omitted, not emitted as null or empty

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` and the
  store holds a `script` collector, a `training` collector with a pass mark and quiz but no
  body and no fields, and an `integration` collector with checks
- **THEN** the `script` collector's `config` is `{}`, the `training` collector's `config`
  carries `pass_mark` and `quiz` and no `body`, `fields`, or `checks` key, and the
  `integration` collector's `config` carries `checks` and none of the attestation keys

#### Scenario: Quiz answer is not exposed

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` for a
  training collector whose quiz items each carry a correct `answer`
- **THEN** each returned quiz item includes `prompt` and `options` but no `answer` key,
  so the correct answer is not disclosed to the reader

#### Scenario: Integration checks are exposed under config and the connection is not

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/collectors` for a
  `type: integration` collector that names a connection and declares checks
- **THEN** the returned collector carries its checks under `config` with each check's
  `source_key`, `name`, and `severity`, and carries no `connection` key

#### Scenario: Anonymous request is rejected

- **WHEN** an anonymous client requests `GET /api/v1/freeboard/collectors`
- **THEN** the endpoint returns HTTP 401

#### Scenario: Served in read-only mode

- **WHEN** GitOps read-only mode is on and an authenticated client requests the
  collectors endpoint
- **THEN** the request is served normally and is not rejected with the 409 read-only
  response

#### Scenario: Zero-grant caller under strict enforcement still reads every collector

- **WHEN** authorization runs in strict enforce mode and an authenticated caller
  holding no organisation grants requests `GET /api/v1/freeboard/collectors`
- **THEN** the endpoint returns every persisted collector, not narrowed to the empty
  accessible-organisation set that strict enforcement produces for a zero-grant
  caller, because the collector endpoint intentionally skips the `IOrgAccess`
  narrowing that the per-org resource endpoints apply

#### Scenario: Retired endpoints no longer answer

- **WHEN** an authenticated client requests `GET /api/v1/freeboard/evidence-collectors`
  or `GET /api/v1/freeboard/attestation-templates`
- **THEN** neither route is mapped and neither returns a collector payload

## MODIFIED Requirements

### Requirement: Read path tolerates an unavailable store

The web app SHALL NOT require MySQL at startup: an unreachable store SHALL NOT crash
the app or block boot. The web app MAY perform a single guarded, best-effort read
just after the server has started, off the boot-blocking path, whose sole effect is
logging integration-connection token warnings; that read SHALL NOT be awaited before
the server begins accepting requests, so a hung or unreachable store can never delay
or gate boot. That read SHALL be non-fatal and SHALL silently skip - never throwing,
and never blocking or gating boot - on any store outage, so the app still boots when
the store is unreachable. Apart from that one guarded warning read, the web app SHALL
NOT auto-connect to MySQL at startup. When the store is unreachable at request time, a read
endpoint SHALL return a clear error response (an RFC 7807 problem body, HTTP 503)
rather than an unhandled exception, and the `GET /api/v1/freeboard/compliance/status`
endpoint's `persisted` summary SHALL degrade to all-null per-kind values rather
than failing the whole status response. The `persisted` object SHALL remain
present with every per-kind key, each set to `null`. The per-kind key set includes one
unified `scopes` key (the previous separate `requirementScopes` and `vendorScopes` keys
are removed with their read endpoints) and one unified `collectors` key (the previous
separate `evidenceCollectors` and `attestationTemplates` keys are removed with their read
endpoints), alongside `vendors` and the pre-existing kinds:

```json
{ "persisted": { "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "vendors": null, "collectors": null } }
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

The vendor and collector read endpoints
(`GET /api/v1/freeboard/vendors` and `GET /api/v1/freeboard/collectors`) tolerate the
unreachable store the same way as the other resource reads: HTTP 503 with an RFC 7807
problem body, never an unhandled exception.

#### Scenario: Unreachable store does not crash the compliance status endpoint

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/compliance/status`
- **THEN** the response returns HTTP 200 with `persisted` equal to
  `{ "standards": null, "controls": null, "requirements": null, "organisations": null, "scopes": null, "vendors": null, "collectors": null }`
  rather than the request failing

#### Scenario: Unreachable store returns 503 from the read endpoints

- **WHEN** the store is unreachable and an authenticated user requests
  `GET /api/v1/freeboard/standards`, `/api/v1/freeboard/controls`,
  `/api/v1/freeboard/requirements`, `/api/v1/freeboard/organisations`,
  `/api/v1/freeboard/scopes`, `/api/v1/freeboard/vendors`, or
  `/api/v1/freeboard/collectors`
- **THEN** the endpoint returns HTTP 503 with an RFC 7807 problem body rather than
  an unhandled exception

#### Scenario: Guarded post-start warning read skips a store outage without failing boot

- **WHEN** the web app starts and the compliance store is unreachable during the
  single guarded, best-effort integration-connection token warning read that runs
  just after the server has started, off the boot-blocking path
- **THEN** the read is skipped silently, it emits no warning, boot is neither blocked
  nor failed, and the app starts

### Requirement: Compliance status endpoint reports persisted counts

The web app SHALL provide `GET /api/v1/freeboard/compliance/status` returning a
summary of how many standards, controls, requirements, organisations, scopes,
vendors, and collectors are currently persisted in the
store. This is the general compliance read surface; the persisted counts live here, NOT on
`GET /api/v1/freeboard/gitops/status` (which stays a GitOps concern reporting
read-only mode and repository URL). The summary SHALL be a `persisted` object with
per-kind counts, including one unified `scopes` count (the separate `requirementScopes`
and `vendorScopes` counts are removed with the merged tables) and one unified `collectors`
count (the separate `evidenceCollectors` and `attestationTemplates` counts are removed with
the merged tables):

```json
{ "persisted": { "standards": 3, "controls": 12, "requirements": 35, "organisations": 4, "scopes": 5, "vendors": 5, "collectors": 14 } }
```

The `persisted` object SHALL always be present: integer counts when the store is
reachable, and all-null per-kind values when the store is unreachable (see the
read-path tolerance requirement).

#### Scenario: Compliance status includes persisted counts

- **WHEN** a client requests `GET /api/v1/freeboard/compliance/status` with a
  reachable store
- **THEN** the response includes a `persisted` object with the count of persisted
  standards, controls, requirements, organisations, one unified scopes count,
  vendors, and one unified collectors count, and no `evidenceCollectors` or
  `attestationTemplates` key

## REMOVED Requirements

### Requirement: Evidence-collector read endpoint serves the persisted collectors

**Reason**: Replaced by the single `GET /api/v1/freeboard/collectors` endpoint over the
merged collector set.
**Migration**: Call `GET /api/v1/freeboard/collectors`; the response adds a `provider` key
and moves nothing else, except that the old free-form `config` string map is now the typed
config object described by the ADDED "Collector read endpoint serves the persisted
collectors" requirement.

### Requirement: Attestation-template read endpoint serves the persisted templates

**Reason**: Replaced by the single `GET /api/v1/freeboard/collectors` endpoint; an
attestation is a collector of `type: manual` or `type: training`.
**Migration**: Call `GET /api/v1/freeboard/collectors` and read `body`, `fields`,
`pass_mark`, and `quiz` from the collector's `config` object instead of from the top level.
The quiz answer is still absent.
