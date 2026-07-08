# Evidence ingest

Freeboard collects runtime Evidence from self-contained collector containers. A
collector runs a vendor-specific check script, then POSTs the results as JSON to
an authenticated ingest endpoint. Each POST lands one immutable Evidence run
with its nested per-check results, written through the shared append-only
evidence model. The web service holds no shell toolchain; the collectors do the
gathering and Freeboard is the authoritative validator and store.

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
- Ingest is idempotent on `(vendor, collector_id, run_id)`: a re-POST under the
  same `run_id` is an accepted replay (`200`). A successful ingest returns NO
  server-assigned evidence id.
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
  "organisation_id": "org-acme",
  "requirement_id": "req-mfa",
  "run_id": "2026-07-08T09-00-00Z-a1b2c3",
  "collector_version": "sha256:abc123def456",
  "collected_at": "2026-07-08T09:00:12Z",
  "checks": [
    { "name": "admin-2sv-enforced", "severity": "hard", "result": "pass",
      "detail": "All super-admins enrolled." },
    { "name": "user-2sv-coverage", "severity": "soft", "result": "fail",
      "detail": "3 of 42 users without 2SV." }
  ],
  "metadata": { "tenant": "ologist.io" }
}
```

| Field | Type | Rules |
| --- | --- | --- |
| `schema_version` | string | Required. Exactly `freeboard.evidence.v1`. |
| `collector_id` | string | Required, non-blank, <= 190 chars. Must equal the credential's collector AND exist in the register. |
| `organisation_id` | string | Required, non-blank, <= 190 chars. Must resolve `In` for the requirement's standard in the Statement of Applicability. |
| `requirement_id` | string | Required, non-blank, <= 190 chars. Must be a requirement the collector's control maps to. |
| `run_id` | string | Required, non-blank. Caller-supplied; the idempotency key with `collector_id`. `collector_id` and `run_id` together must be <= 190 chars. |
| `collector_version` | string | Optional. Provenance, e.g. an image digest. |
| `collected_at` | string | Required. UTC ISO 8601 (a `Z` or `+00:00` designator). |
| `checks` | array | Required, non-empty. See below. |
| `metadata` | object | Optional. Free-form JSON object. |

Each `checks[]` element:

| Field | Type | Rules |
| --- | --- | --- |
| `name` | string | Required, non-blank, <= 190 chars, unique within the run. |
| `severity` | string | Required. `hard` or `soft`. |
| `result` | string | Required. `pass` or `fail`. |
| `detail` | string | Optional, human-readable, <= 4096 chars. |

An optional field (`collector_version`, check `detail`, `metadata`) may be
omitted or sent as JSON `null`; the two are equivalent and both accepted.

`collected_at` must be UTC: the timestamp must end in `Z` or `+00:00`. A non-zero
offset such as `+02:00` is rejected with `422`.

### organisation_id and requirement_id

The credential proves the collector (and thus its control and vendor), not the
organisation or requirement. Both come from the payload and are validated
server-side against the real read surfaces:

- `requirement_id` must be one of the requirements the collector's control maps
  to.
- `(organisation_id, requirement_id)` must resolve `In` in the Statement of
  Applicability for the requirement's owning standard.

A well-formed request that fails either check is `422`.

### Severity and result semantics

`severity` marks how much a failure matters: a `hard` check is gating, a `soft`
check is advisory. `result` is the check outcome (`pass` or `fail`). A
not-applicable check is omitted by the collector; an unknown outcome is resolved
to `fail` or omitted before posting. Freeboard derives the run-level verdict:

- The run is `Fail` if any check has `severity=hard` AND `result=fail` (the V1
  gate); otherwise `Pass`.

The response also echoes three derived counts:

- `hard_fail_count`: checks with `severity=hard` AND `result=fail`.
- `soft_fail_count`: checks with `severity=soft` AND `result=fail`.
- `total_count`: the number of checks.

### Idempotency and replay

The idempotency key is `(vendor, collector_id, run_id)` (the collector's vendor
is snapshotted from its registration). Re-POSTing the same `(collector_id,
run_id)` returns `200` (accepted replay); the original run is unchanged, since
Evidence is append-only. A collector that retries a failed POST MUST resend under
the same `run_id` so the retry dedupes.

Freeboard stores no request-body hash, so a `200` replay does not compare the
resent body against the original. A different body re-sent under the same
`run_id` is accepted as a replay and does not overwrite the landed run.

## Responses

Success bodies (both `201` and `200`) are identical in shape and carry NO
server-assigned evidence id:

```json
{
  "collector_id": "google-workspace-mfa",
  "run_id": "2026-07-08T09-00-00Z-a1b2c3",
  "hard_fail_count": 0,
  "soft_fail_count": 1,
  "total_count": 2
}
```

| Code | Meaning |
| --- | --- |
| `201 Created` | New Evidence landed. |
| `200 OK` | Idempotent replay of an existing `(collector_id, run_id)`. |
| `400 Bad Request` | The body is not well-formed JSON. This is the ONLY cause of `400`. |
| `401 Unauthorized` | Missing, malformed, unknown, or cross-scheme (session) token. |
| `403 Forbidden` | A recognised credential that is revoked or expired. |
| `409 Conflict` | A mutating request in read-only mode for the credential admin routes (not the ingest route). |
| `413 Payload Too Large` | The body exceeds 1 MiB. |
| `422 Unprocessable Entity` | Any value or type problem in a well-formed body: wrong `schema_version`, a wrong-typed field, a bad severity/result, an empty or duplicate-named `checks`, a non-UTC or absent timestamp, a `collector_id` that disagrees with the credential, an unknown collector, a collector with no vendor, a `requirement_id` the control does not map to, an `organisation_id` not in scope, or an over-190-char `collector_id:run_id`. |
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
