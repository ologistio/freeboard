# Evidence ingest

Freeboard collects runtime Evidence from self-contained collector containers. A
collector runs a vendor-specific check script, then POSTs the results as JSON to
an authenticated ingest endpoint. Each POST lands one immutable Evidence record
with its nested per-check results. The web service holds no shell toolchain; the
collectors do the gathering and Freeboard is the authoritative validator and
store.

This document is for developers and operators. It covers the route, the
per-collector machine credential, the request contract, idempotency, and the
response and error codes. Plain ASCII throughout.

## At a glance

- One route: `POST /api/v1/freeboard/evidence`.
- Auth is a per-collector machine credential (a bearer token), NOT a human
  session token. Send it as `Authorization: Bearer <token>`.
- The payload is versioned by `schema_version` (currently
  `freeboard.evidence.v1`). The published JSON Schema is
  `docs/schemas/evidence-ingest.v1.schema.json`; a worked example is
  `docs/schemas/evidence-ingest.v1.example.json`.
- Ingest is idempotent on `(collector_id, run_id)` by an exact-body hash: an
  identical re-POST is a replay (`200`), a changed body for the same run is a
  conflict (`409`).
- The request body is capped at 1 MiB.
- Ingest works even in GitOps read-only mode (Evidence is runtime data, not
  GitOps-authored config). Credential issuance and revocation do NOT: they are
  admin config actions and are rejected with `409` in read-only mode.

## Authentication and credentials

Every existing Freeboard bearer token is an opaque human session token. Evidence
ingest instead uses a per-collector machine credential: a bearer token scoped to
exactly one evidence-collector, carrying no human identity, revocable, with an
optional expiry. Only its keyed HMAC is stored; the raw token is shown once at
issue time and never again.

The two token types share the wire format `v<keyId>.<secret>` but are looked up
in disjoint tables, so each rejects the other's token with `401`: a session token
presented at ingest fails `401`, and a collector token presented at any other
endpoint fails `401`.

### Issue a credential

System-admin only.

```text
POST /api/v1/freeboard/evidence-collectors/{id}/credentials
Content-Type: application/json

{ "expires_at": "2027-01-01T00:00:00Z" }   # expires_at is optional
```

Response `201`:

```json
{
  "credential_id": "01J...",
  "collector_id": "google-workspace-mfa",
  "token": "v1.<secret>",
  "expires_at": "2027-01-01T00:00:00Z"
}
```

The `token` is the raw bearer value. Store it in the collector container's
`FREEBOARD_INGEST_TOKEN`. Issuing for an unknown collector returns `422`.

### Revoke a credential

System-admin only.

```text
DELETE /api/v1/freeboard/evidence-collectors/{id}/credentials/{credId}
```

Response `204` when a live credential was revoked, `404` when it does not exist
under that collector or was already revoked.

### CLI

```sh
freeboard collector credential issue <collector-id> [--expires-at <iso8601>]
freeboard collector credential revoke <collector-id> <credential-id>
```

`issue` prints the raw token on stdout exactly once.

## Request contract (freeboard.evidence.v1)

```json
{
  "schema_version": "freeboard.evidence.v1",
  "collector_id": "google-workspace-mfa",
  "run_id": "2026-07-08T09-00-00Z-a1b2c3",
  "collector_version": "sha256:abc123def456",
  "started_at": "2026-07-08T09:00:00Z",
  "finished_at": "2026-07-08T09:00:12Z",
  "checks": [
    { "name": "admin-2sv-enforced", "severity": "hard", "status": "pass",
      "detail": "All super-admins enrolled." },
    { "name": "user-2sv-coverage", "severity": "soft", "status": "fail",
      "detail": "3 of 42 users without 2SV.",
      "data": { "total_users": 42, "without_2sv": 3 } }
  ],
  "metadata": { "tenant": "ologist.io" }
}
```

| Field | Type | Rules |
| --- | --- | --- |
| `schema_version` | string | Required. Exactly `freeboard.evidence.v1`. |
| `collector_id` | string | Required, non-blank, <= 190 chars. Must equal the credential's collector AND exist in the register. |
| `run_id` | string | Required, non-blank, <= 190 chars. Caller-supplied; the idempotency key with `collector_id`. |
| `collector_version` | string | Optional. Provenance, e.g. an image digest. |
| `started_at` | string | Required. UTC ISO 8601 (a `Z` or `+00:00` designator). |
| `finished_at` | string | Required. UTC ISO 8601, at or after `started_at`. |
| `checks` | array | Required, non-empty. See below. |
| `metadata` | object | Optional. Free-form JSON object. |

Each `checks[]` element:

| Field | Type | Rules |
| --- | --- | --- |
| `name` | string | Required, non-blank, <= 190 chars, unique within the run. |
| `severity` | string | Required. `hard` or `soft`. |
| `status` | string | Required. `pass`, `fail`, `unknown`, or `not_applicable`. |
| `detail` | string | Optional, human-readable. |
| `data` | object | Optional. Free-form JSON object of structured findings. |

An optional field (`collector_version`, check `detail`, check `data`, `metadata`)
may be omitted or sent as JSON `null`; the two are equivalent and both accepted.

`started_at` and `finished_at` must be UTC: the timestamp must end in `Z` or
`+00:00`. A non-zero offset such as `+02:00` is rejected with `422`.

### Severity and status semantics

`severity` marks how much a failure matters: a `hard` check is gating, a `soft`
check is advisory. `status` is the check outcome. Freeboard stores the raw
per-check rows plus three derived counts and does NOT compute a control-level
verdict at ingest (that is the scoring engine's job):

- `hard_fail_count`: checks with `severity=hard` AND `status=fail` (the V1 gate).
- `soft_fail_count`: checks with `severity=soft` AND `status=fail`.
- `total_count`: the number of checks.

### Idempotency, replay, and conflict

The unique key is `(collector_id, run_id)`. Freeboard stores the SHA-256 of the
exact request bytes. Re-POSTing the same `(collector_id, run_id)`:

- with an identical body returns `200` (replay) with the ORIGINAL stored
  `received_at` and counts. Nothing is mutated; Evidence is append-only.
- with a different body returns `409` (conflict).

A collector that retries a failed POST MUST resend the identical body under the
same `run_id` so the retry dedupes.

## Responses

Success bodies (both `201` and `200`) are identical in shape:

```json
{
  "evidence_id": "01J...",
  "collector_id": "google-workspace-mfa",
  "run_id": "2026-07-08T09-00-00Z-a1b2c3",
  "received_at": "2026-07-08T09:00:13Z",
  "hard_fail_count": 0,
  "soft_fail_count": 1,
  "total_count": 2
}
```

| Code | Meaning |
| --- | --- |
| `201 Created` | New Evidence landed. |
| `200 OK` | Idempotent replay of an identical body. |
| `400 Bad Request` | The body is not well-formed JSON. This is the ONLY cause of `400`. |
| `401 Unauthorized` | Missing, malformed, unknown, or cross-scheme (session) token. |
| `403 Forbidden` | A recognised credential that is revoked or expired. |
| `409 Conflict` | Same `(collector_id, run_id)`, different body; or a mutating request in read-only mode for the credential admin routes. |
| `413 Payload Too Large` | The body exceeds 1 MiB. |
| `422 Unprocessable Entity` | Any value or type problem in a well-formed body: wrong `schema_version`, a wrong-typed field, a bad severity/status, an empty or duplicate-named `checks`, a non-UTC or out-of-order timestamp, a `collector_id` that disagrees with the credential, or an unknown collector. |
| `503 Service Unavailable` | The evidence store could not be reached. |

Error responses are RFC 7807 problem+json bodies. A well-formed body with a wrong
field type is always `422`, never `400`: `400` means ONLY a JSON syntax error.

## Contract and schema versioning

The contract is frozen under `schema_version`. A backwards-incompatible change
gets a new value (for example `freeboard.evidence.v2`) and a new schema file;
the server continues to accept older versions it still supports. Ologist-side
collector scripts depend on this contract, so the JSON Schema
(`docs/schemas/evidence-ingest.v1.schema.json`) and this document are the frozen
reference.
