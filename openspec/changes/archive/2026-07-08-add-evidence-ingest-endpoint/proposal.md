## Why

Freeboard has a static `EvidenceCollector` register (GitOps-authored: type, frequency,
threshold, vendor, config) but nothing produces runtime Evidence. The CE+ V1 collector
scripts (google-workspace, fleet, github, vercel, endpoint-audit, ...) already emit JSON
with per-check severities, yet there is no authenticated endpoint to receive that JSON,
no runtime Evidence store, and no machine credential a container can present. We want
the web service to hold no shell toolchain: collectors run as self-contained containers
that POST their results to an authenticated Evidence ingest endpoint. This change adds
that endpoint, the persistence behind it, a per-collector machine credential, and the
published ingest contract so Ologist-side scripts can target a frozen schema.

## What Changes

- Add an authenticated `POST /api/v1/freeboard/evidence` ingest endpoint that accepts a
  collector run (`schema_version`, `collector_id`, `run_id`, `started_at`, `finished_at`,
  and a non-empty list of named checks each carrying a hard/soft severity and a
  pass/fail/unknown/not_applicable status), persists it as one immutable Evidence record
  with nested checks, and is idempotent on `(collector_id, run_id)` keyed by an exact-body
  SHA-256 hash.
- Add a runtime Evidence persistence layer in `Freeboard.Persistence` (new `evidence_runs`
  and `evidence_run_checks` tables via forward-only migration `014`) with an append-only
  write store. The `evidence_runs` table holds NO foreign key to any GitOps config table:
  collector identity (`collector_id`, `collector_title`, `control_id`, `vendor_id`,
  `collector_type`) is snapshotted onto each run at ingest so evidence history survives a
  GitOps sync that prunes a collector. Each run stores raw derived summary counts
  (`hard_fail_count`, `soft_fail_count`, `total_count`) - not a compliance verdict.
- Add a per-collector machine credential (`collector_credentials` table) reusing the
  existing keyed HMAC-SHA256 token primitive. A collector token is a bearer credential
  scoped to one collector id, issued once by an admin, revocable, with an optional nullable
  expiry, never tied to a human user. A new route-scoped authentication scheme validates it
  for the ingest endpoint. This table MAY cascade-delete with its collector (a credential is
  live config, not compliance history).
- Add admin issuance and revocation of collector credentials over the API, with matching
  CLI subcommands (`freeboard collector credential issue` / `revoke`) that reuse the
  existing HTTP-backed CLI pattern.
- Exempt the ingest endpoint from GitOps read-only mode via a dedicated marker (not by
  overloading the auth-endpoint marker): Evidence is runtime telemetry, not GitOps-managed
  config, so a read-only (production) instance still collects evidence. Credential issuance
  is NOT exempt.
- Publish the ingest contract as both a human document `docs/evidence-ingest.md` and a
  machine-readable JSON Schema `docs/schemas/evidence-ingest.v1.schema.json`: payload schema,
  severity and status semantics, collector id, run id, auth, idempotency/replay/conflict,
  RFC 7807 error responses, size limit, and contract/schema versioning. A test validates the
  in-repo example payload against the JSON Schema so docs and code cannot drift.
- Add a reusable collector container wrapper under `collectors/`: a curl-based base image and
  entrypoint that runs a collector script, validates and POSTs its JSON to the ingest
  endpoint, retries safely with the same body, and exits non-zero on ingest failure, plus one
  worked reference/mock example. No .NET runtime and no credentials are baked into the image;
  it runs non-root and takes config via env (`FREEBOARD_BASE_URL`, `FREEBOARD_COLLECTOR_ID`,
  `FREEBOARD_INGEST_TOKEN`, optional `FREEBOARD_RUN_ID`). The CE+ V1 collector scripts and
  their real image build inputs are Ologist-side; this repo owns the contract, the endpoint,
  and the reusable wrapper.

## Capabilities

### New Capabilities

- `evidence-ingest`: the authenticated ingest endpoint (route, request/response schema,
  validation, idempotency/replay/conflict, payload size limit, store-failure and read-only
  behaviour), the published human contract and JSON Schema, and the reference collector
  container packaging.
- `collector-credentials`: the per-collector machine credential lifecycle (keyed-HMAC
  storage, optional expiry, admin issuance and revocation over API and CLI, one-collector
  scope) and the route-scoped authentication scheme that validates a collector bearer token
  without accepting a human session token.

### Modified Capabilities

- `compliance-persistence`: add runtime Evidence persistence (`evidence_runs` and
  `evidence_run_checks` tables, migration `014`, append-only immutable write store, exact-body
  idempotency, snapshot collector identity with no GitOps FK) alongside the existing static
  compliance tables. This extends the existing persistence capability rather than introducing
  a parallel one.

## Impact

- New code: `Freeboard.Persistence` (migration `014`, Evidence write store, collector
  credential store, read models), `Freeboard` web (ingest endpoint, credential issuance
  endpoints, collector authentication scheme, read-only-exemption marker, DI wiring,
  request size limit), `Freeboard.CLI` (credential subcommands + API client methods).
- New non-code: `docs/evidence-ingest.md` and `docs/schemas/evidence-ingest.v1.schema.json`
  (published contract), `collectors/` (reference container wrapper, entrypoint, one worked
  example, README).
- Licensing: all MIT. Persistence, web, and CLI are MIT projects; nothing here is an EE
  carve-out and nothing is placed in `src/Freeboard.Enterprise`. The CLI stays EE-free and
  cross-platform.
- Dependencies: no new NuGet packages in shipped code (the JSON Schema drift test may use a
  test-only schema validator). Reuses `ITokenHasher`/`HmacTokenHasher`, the
  `AuthenticationHandler` pattern, the minimal-API route-group pattern, and the
  `GitOpsReadOnlyMiddleware` marker mechanism.

## Non-goals

- Agent (OSQuery) collection. The ingest endpoint and credential are designed so a future
  agent poster is just another holder of a collector credential, but no agent collection is
  built here.
- The in-service background scheduler that runs due collectors on their frequency.
  Container collectors are triggered by their own runtime (cron / orchestrator). The
  scheduler for in-process integration collectors is separate work.
- Secret-store retrieval. Collectors hold their own target secrets; the endpoint never
  fetches or stores collector target credentials, and collector `config` holds no secrets.
- Authoring the CE+ V1 collector scripts. Those are Ologist-side. This change owns the
  contract, the endpoint, the persistence, the credential, and the reusable container
  wrapper, not the vendor-specific script bodies or their real image build inputs.
- Scoring, staleness, and control roll-up over Evidence. The endpoint stores raw per-run
  summary counts, but the categorical rollup and gating/scoring engine are separate
  downstream work; ingest computes no compliance verdict.
- A web read surface for Evidence. This change lands Evidence; reading and rendering it is
  downstream.
