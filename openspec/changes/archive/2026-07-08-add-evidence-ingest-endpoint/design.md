## Context

Freeboard is an evidence + scoring compliance system driving Ologist's Cyber Essentials
Plus audit. Today the compliance domain is static, GitOps-authored data: standards,
requirements, controls, organisations, scopes, vendors, and (recently) the
`EvidenceCollector` register and `AttestationTemplate` kinds. Nothing produces runtime
Evidence: there is no evidence table, no ingest endpoint, no scheduler, and no non-human
credential. Every existing bearer token is an opaque session token tied to a human `users`
row.

The CE+ V1 collector scripts (google-workspace, fleet, github, vercel, endpoint-audit, ...)
already emit JSON with per-check severities. We want them to run as self-contained containers
that POST their results to an authenticated Evidence ingest endpoint, so the web service holds
no shell toolchain. This change adds the ingest endpoint, the runtime Evidence persistence
behind it, a per-collector machine credential, and the published ingest contract.

Grounding facts from the current codebase:

- Endpoints are minimal APIs on `app.MapGroup("/api/v1/freeboard").RequireAuthorization()`;
  `ComplianceWriteEndpoints` is the closest write template (record request bodies, RFC 7807
  problem bodies, `.RequirePermission(...)` filter). There are no MVC controllers.
- Auth is a single default scheme `FreeboardBearer` (`BearerAuthenticationHandler`) over
  opaque session tokens. The keyed HMAC primitive is `ITokenHasher` / `HmacTokenHasher`
  (`MintPrefixed` / `TryHashPrefixed`, wire format `v<keyId>.<secret>`, versioned
  `Auth:TokenKeys:<version>`). `SessionIssuer` always needs a `userId`. There is no
  machine/service credential.
- `GitOpsReadOnlyMiddleware` 409s mutating methods unless the matched endpoint carries the
  `AuthEndpoint` metadata marker (routing runs before it).
- Migrations are forward-only hand-written `NNN_slug.sql` embedded resources applied by
  `MySqlMigrationRunner`; the next ordinal is `014`. Ids use `CHAR(26)` ULID (sessions,
  users) or `VARCHAR(190) utf8mb4_bin` (compliance ids).
- `evidence_collectors` (migration `012`) has `id`, `title`, `control_id`, `vendor_id`
  (nullable), `type`, and both config FKs are `ON DELETE RESTRICT`; the GitOps importer
  prunes referencing collectors before deleting a control or vendor.
- No Kestrel body-size or JSON-casing config exists; the framework default body cap is 30 MB
  and wire snake_case is produced by hand-built anonymous objects.

## Provenance of this design

This design unifies two independent plans:

- Plan A (the prior draft of these artifacts): endpoint shape and record binding, the
  route-scoped collector auth scheme with disjoint lookup tables, migration `014`, the
  read-only-exemption marker, the per-endpoint body-size limit, the container wrapper scope,
  and the CLI credential commands.
- Plan B (Codex, `tmp/codex-plan-round1.md`): the four-value check status enum, explicit
  `started_at`/`finished_at` with ordering, the length bounds and unique-check-name rules,
  storing a SHA-256 of the exact body, the `409` conflict on a changed body, a dedicated
  read-only marker rather than reusing the auth marker, the `503` store-failure code, and the
  full response-code matrix.

Six mediator decisions (D1-D6) are binding and are baked in below; where the two plans
diverged, the resolution cites the governing decision.

## Goals / Non-Goals

**Goals:**

- One authenticated `POST /api/v1/freeboard/evidence` that lands one immutable Evidence
  record with nested checks, idempotent on `(collector_id, run_id)` by exact-body hash.
- A per-collector machine credential reusing the keyed-HMAC primitive, issued and revoked by
  an admin, with an optional expiry, never carrying a human identity.
- Runtime Evidence and credential persistence in `Freeboard.Persistence` (MIT), migration
  `014`, with evidence history that survives collector pruning.
- A published contract as both `docs/evidence-ingest.md` and a JSON Schema, plus a reusable
  reference collector container so the flow is demonstrable end-to-end.
- A shape that a future agent (OSQuery) poster fits without rework.

**Non-Goals:** Agent collection, the in-service scheduler, secret-store retrieval,
scoring/staleness/rollup, a web read surface for Evidence, and authoring the vendor-specific
CE+ scripts. See the proposal Non-goals.

## Decisions

### D1 (mediator - evidence retention). Snapshot collector identity, no GitOps FK

BINDING. The evidence run table holds NO foreign key to `evidence_collectors`, `controls`, or
`vendors`. At ingest the endpoint reads the collector's `evidence_collectors` row and
snapshots `collector_title`, `control_id`, `vendor_id`, and `collector_type` onto the run,
and stores `collector_id` as a plain `utf8mb4_bin` string. Rationale: a GitOps sync
hard-deletes pruned collectors; compliance evidence history must survive collector/control
refactors.

Divergence resolved: Plan A used `evidence -> evidence_collectors` `ON DELETE CASCADE`, which
loses history when a collector is pruned. Plan B proposed snapshotting metadata and no FK from
evidence to config tables. The mediator adopted Plan B's approach (D1); Plan A's cascade FK is
removed. `collector_credentials` MAY still FK to `evidence_collectors` `ON DELETE CASCADE` -
a credential is live config, not history, so revoking it with the collector is correct.

### D2 (mediator - idempotency). Exact-body hash, replay vs conflict

BINDING. Unique key `(collector_id, run_id)`. The endpoint stores `request_body_sha256`, the
SHA-256 of the exact request body. To make "exact request body" real, the endpoint reads the
raw body bytes ONCE and hashes those exact bytes BEFORE any JSON deserialization (see D7 step
3); it never re-serializes a bound object to hash, because re-serialization would not match the
bytes the collector sent. An identical re-POST of the same `(collector_id, run_id)`
returns `200 OK` with a replay indicator and never mutates. The same `(collector_id, run_id)`
with a different body hash returns `409 Conflict`. Evidence is immutable/append-only, written
in one transaction; the write store inserts the run and its checks together and, on a
duplicate key, inserts nothing and reports whether the stored hash matches.

Divergence resolved: Plan A returned `200` on any duplicate `run_id` and had no conflict path.
Plan B added the body hash and the `409`. The mediator adopted the hash + replay/conflict
split (D2). `run_id` remains caller-supplied so a retried POST from the same run dedupes.

### D3 (mediator - credentials). Revocable, optional expiry, identity authoritative

BINDING. Per-collector machine credentials are revocable and support an OPTIONAL nullable
`expires_at`, enforced at validation time. The credential reuses `ITokenHasher`
(`MintPrefixed`/`TryHashPrefixed`, `v<keyId>.<secret>`). Credential identity is authoritative:
the body `collector_id` must match the credential's collector (mismatch is `422`). A human
session token at ingest fails `401`, and a collector token used elsewhere fails `401`
(disjoint lookup tables). A recognised-but-revoked-or-expired credential fails `403`.

Auth mechanism (from Plan A): a `CollectorBearerAuthenticationHandler` registered as a second
scheme, applied only to the ingest route via a named policy. It parses `v<keyId>.<secret>`,
HMACs with the selected key, looks up `collector_credentials` by `token_hash`, and checks the
key version. Separation is by lookup table: both token types share the wire format, but the
ingest scheme queries only `collector_credentials` and the session scheme only `sessions`, so
each rejects the other's token. `BearerAuthenticationHandler` is untouched.

Scheme binding (concrete). The collector scheme is NOT the process default (`FreeboardBearer`
stays the default scheme), so the named ingest authorization policy MUST bind the collector
scheme explicitly, exactly as the sudo and page-challenge policies do in `Program.cs`
(`policy.AddAuthenticationSchemes(...)`): the policy calls
`policy.AddAuthenticationSchemes(CollectorBearerAuthenticationHandler.SchemeName)`, then
`RequireAuthenticatedUser()` plus the collector-id and active-credential requirements. The
ingest route is mapped in its OWN route group whose only authorization is
`.RequireAuthorization(<ingest policy name>)`; it does NOT use the parameterless
`.RequireAuthorization()` that every other `MapGroup(ApiRoutePrefix)` module applies (that
binds the default session scheme). If the ingest route inherited the default-session
authorization, a collector bearer token would be validated against the session scheme and
`401` on every request. Binding only the ingest policy means the collector token is validated
against the collector scheme. The credential admin routes are the opposite: they keep the
default `.RequireAuthorization()` (session scheme) plus `RequirePermission(...)`.

401-vs-403 mechanism (concrete). An `AuthenticationHandler` that returns
`AuthenticateResult.Fail` yields `401`, never `403`: ASP.NET Core only emits `403` when
authentication SUCCEEDS and a later authorization requirement denies. So the split is:

- Missing / malformed / unknown-key / hash-not-found / key-version-mismatch, and any
  cross-scheme (session) token, return `AuthenticateResult.Fail` -> `401` (the challenge).
- A RECOGNISED token (hash found, key version matches) authenticates SUCCESSFULLY. The handler
  builds the non-human principal with the collector-id claim, and adds a claim recording
  whether the credential is currently usable: it sets an "active" collector-credential claim
  ONLY when `revoked_at` is null AND (`expires_at` is null OR not yet elapsed). A revoked or
  expired credential still authenticates but WITHOUT the active claim.
- The named ingest authorization policy requires both the collector-id claim and the active
  claim. A revoked/expired credential authenticates but fails the policy, so ASP.NET Core
  issues a Forbid -> the scheme's default `HandleForbiddenAsync` returns `403`. This is the
  only way the route reaches `403`; the handler itself never fabricates a `403` from `Fail`.

Issuance reuses `MintPrefixed`; the raw token is returned once. Admin endpoints
`POST /api/v1/freeboard/evidence-collectors/{id}/credentials` (issue, optional expiry) and
`DELETE .../credentials/{credId}` (revoke), plus CLI `collector credential issue`/`revoke`
reusing `ApiCommandRunner`.

Admin gate (concrete). "Admin-guarded" means the same route-metadata gate the codebase uses
for sensitive routes, not the legacy `global_role = admin` check (which is not authoritative).
The credential issue/revoke routes carry
`.RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true)`,
matching `CustomRoleEndpoints` (the closest system-admin-only precedent). This reuses the
existing `system.admin` action and `system` resource, so no new authz action, seed data, or
engine wiring is added. The route-metadata architecture test (`RouteAuthzMetadataTests`) gains
inline cases asserting both credential routes carry `SystemAdmin` + `alwaysEnforce: true`, so a
mis-wired or ungated credential route fails the build rather than shipping open. A new
`collector.credential.write` action is deliberately NOT introduced: credential administration
is a system-admin operation with no org scoping, so a dedicated action would add a permission
key, seed rows, and role wiring for no finer-grained control.

Route-metadata guard for the ingest route (concrete). The universal guard
`RouteAuthzMetadataTests.EveryMutatingApiRouteIsGatedOrAllowlisted` fails the build for any
mutating `api/v1/freeboard/*` route that neither force-enforces a permission
(`AuthzPermissionMetadata` with `AlwaysEnforce: true`) nor sits on its small self-service/setup
path allowlist. The ingest route carries NO `AuthzPermissionMetadata` (it is gated by the
collector scheme + named ingest policy, not by `RequirePermission`), so the guard would flag
it. Rather than add a broad path-only exemption for `evidence`, the guard is UPDATED to treat
collector-scheme-gated ingest as a legitimate gated route: a mutating API route passes when it
EITHER force-enforces a permission OR carries the `IngestEndpoint` marker AND is bound to the
named ingest authorization policy (an `IAuthorizeData` on the endpoint whose `Policy` equals the
ingest policy name). An ingest route that lost either the marker or the policy binding still
fails the guard, so an accidentally-ungated ingest route cannot ship. A dedicated positive test
also asserts the `evidence` route carries the `IngestEndpoint` marker and that the resolved
ingest policy binds the collector scheme (`AddAuthenticationSchemes` contains
`CollectorBearerAuthenticationHandler.SchemeName`), so the route cannot silently fall back to
the session scheme or drop its gate.

Divergence resolved (403 vs 422 for "wrong collector"): Plan B's response matrix listed both
`422 semantic validation` and `403 valid token wrong collector`, and the mediator asked to
pick one. We use `422` for a body `collector_id` that disagrees with the authoritative
credential (per D3): the credential is valid and the caller authenticated, but the payload is
unprocessable against that identity. `403` is reserved for a recognised credential that is
revoked or expired (once valid, now forbidden). `401` covers missing/malformed/unknown-key/
hash-not-found tokens and cross-scheme tokens. This maps every Plan B code to a distinct,
testable cause. Plan A added expiry only as a "later without schema change" note; D3 makes
`expires_at` a first-class nullable column now.

### D4 (mediator - scoring boundary). Raw counts, no verdict at ingest

BINDING. At ingest the store persists normalized per-check rows plus raw summary counts
(`hard_fail_count`, `soft_fail_count`, `total_count`). It does NOT compute a control-level or
rollup status; categorical rollup stays the scoring engine's responsibility. The counts are
raw/derived summaries, not a compliance verdict.

Divergence resolved: Plan A derived and stored `overall_status` = `pass`/`fail` per run - a
compliance verdict, which crosses the scoring boundary. Plan B stored raw counts and left
rollup to scoring. The mediator adopted counts only (D4); Plan A's `overall_status` column is
removed and replaced by the three count columns. `hard_fail_count` counts `severity = hard`
and `status = fail` (the V1 gate); `soft_fail_count` counts `severity = soft` and
`status = fail`; `total_count` is the check count.

### D5 (mediator - containers). curl wrapper, no .NET, one reference example

BINDING. Transport is a curl-based `entrypoint.sh` (no .NET runtime baked into collector
images). Under `collectors/`: a reusable wrapper base image + `entrypoint.sh` (runs a script,
validates and POSTs its JSON, retries safely with the same body, exits non-zero on ingest
failure) plus ONE worked reference/mock example collector. Images run non-root, bake no
credentials, and take config via env: `FREEBOARD_BASE_URL`, `FREEBOARD_COLLECTOR_ID`,
`FREEBOARD_INGEST_TOKEN`, optional `FREEBOARD_RUN_ID`. The entrypoint derives the ingest URL
from `FREEBOARD_BASE_URL`. Vendor-specific CE+ collector scripts and their real image build
inputs stay Ologist-side; that dependency is documented.

Wrapper validation level (realistic). A curl/sh wrapper cannot validate a JSON Schema, so it
does NOT claim to. Using `jq` (declared as a wrapper image dependency alongside curl), the
entrypoint performs only: (1) a syntactic JSON parse of the script's stdout (`jq empty`,
non-zero exit -> fail before POSTing), and (2) an assertion that the payload's `.collector_id`
is present and equals `FREEBOARD_COLLECTOR_ID`. Any deeper schema conformance is the server's
job: the ingest endpoint is the authoritative validator (`422` on any semantic violation), and
`docs/schemas/evidence-ingest.v1.schema.json` is the authoritative schema. The wrapper's checks
just catch a broken script early and prevent posting for the wrong collector.

Divergence resolved: both plans agreed on a curl wrapper over shipping the CLI into images.
Plan A used `FREEBOARD_INGEST_URL`; the mediator's env contract uses `FREEBOARD_BASE_URL`
(D5), adopted here. Both plans agreed the CLI still gains the credential admin commands (a
genuine reuse of its HTTP pattern), but not as the in-image poster.

### D6 (mediator - contract docs). Human doc + JSON Schema + drift test

BINDING. Publish both `docs/evidence-ingest.md` (matching `docs/gitops.md` /
`docs/authentication.md` style) and `docs/schemas/evidence-ingest.v1.schema.json`. The human
doc specifies: payload schema, severity semantics (hard/soft), status enum, `collector_id`,
`run_id`, auth, idempotency/replay/conflict, RFC 7807 error responses, body size limit, and
contract/schema versioning. A test validates the in-repo example payload(s) against the JSON
Schema so docs and code cannot drift.

Divergence resolved: Plan A published only the human doc; Plan B added the JSON Schema. The
mediator requires both plus the drift test (D6).

### D7. Ingest endpoint shape and payload contract

`POST /api/v1/freeboard/evidence`, minimal API in a new
`src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`, following `ComplianceWriteEndpoints`.

The endpoint MUST NOT bind the request via automatic `[FromBody]` deserialization, and MUST
NOT strongly deserialize the untrusted body into `EvidenceIngestRequest` at all. Automatic
binding consumes the request stream (so the exact bytes needed for the D2 body hash are gone
before hashing), and any strong deserialization turns a JSON type mismatch on ANY field into a
framework `400` that bypasses the `422` semantic-validation pass. The contract requires
malformed JSON -> `400` but every value-level problem in a well-formed body (a wrong-shape
`metadata`, a numeric `collector_id`, a string `checks`, a numeric `severity`) -> `422`, so the
endpoint handles the body MANUALLY. The handler takes `HttpContext` (not a bound record) and
runs this control flow:

1. Enforce the 1 MiB size limit (D9) via endpoint metadata; an oversize body is rejected `413`
   before it is buffered.
2. Read the raw request body into a single `byte[]` (bounded by the size limit), once.
3. Compute `SHA-256` over those exact bytes -> `request_body_sha256` (D2). Hashing the raw
   bytes, not a re-serialization, guarantees the stored hash matches the bytes received.
4. Parse the bytes into a `JsonElement` tree with `JsonDocument.Parse` (equivalently
   `JsonSerializer.Deserialize<JsonElement>`) inside a `try/catch (JsonException)`. This parse
   fails ONLY when the bytes are not well-formed JSON; it emits the RFC 7807 `400` and stops,
   and nothing is persisted. Crucially, parsing to `JsonElement` does NOT throw on a JSON type
   mismatch (a numeric `collector_id`, a string `checks`, a numeric `severity`): a wrong
   `ValueKind` is data the parser accepts, so it reaches step 5 as a `422`, never a `400`.
5. Run the semantic validation pass by reading EVERY externally-supplied field directly from
   the parsed `JsonElement` tree and checking its `ValueKind` and value against the
   evidence-ingest spec (all `422` rules). The root must be a JSON object; `schema_version`,
   `collector_id`, `run_id` must be present JSON strings meeting the exact/length rules;
   `collector_version` if present must be a JSON string; `checks` must be a non-empty JSON
   array whose every element is a JSON object with JSON-string `name`/`severity`/`status`
   (enum-constrained) and object-or-absent `data`; `metadata` object-or-absent; `started_at`/
   `finished_at` present JSON strings parsed as UTC ISO 8601 with `finished_at >= started_at`.
   Any wrong `ValueKind` or value is `422` (not `400`); the first failure emits the RFC 7807
   `422` and stops.
6. Only AFTER validation passes, project the validated `JsonElement` values into the internal
   typed record(s), snapshot the collector identity, compute the summary counts, and call the
   write store.

The ONE general rule (steps 4-6) closes the gap for EVERY externally-supplied field, so no
future field can reintroduce it: parse the untrusted body into a `JsonElement` tree, never into
the strongly-typed record. `JsonDocument.Parse` / `JsonSerializer.Deserialize<JsonElement>`
throws ONLY on malformed JSON (-> `400`); it does NOT throw on a JSON type mismatch, so a wrong
`ValueKind` on ANY field reaches the step-5 validator as a `422`. The step-5 validator reads
each field from the `JsonElement` tree and asserts its `ValueKind` and value (the exact rules
enumerated in step 5). Do NOT call `JsonSerializer.Deserialize<EvidenceIngestRequest>`, and do
NOT deserialize any single externally-supplied field into its .NET type: a strongly-typed member
makes System.Text.Json throw on a wrong `ValueKind` - `{"collector_id": 123}`, `{"checks":
"x"}`, `{"severity": 1}` - and that `JsonException` surfaces as a framework `400`, violating the
invariant that `400` means ONLY a JSON syntax error.

`EvidenceIngestRequest`/`CheckInput` still exist, but as the INTERNAL post-validation mapping
target that step 6 populates FROM the validated `JsonElement` values - NOT as a deserialization
target for untrusted input. (Snake_case is therefore a validator concern, not a binding one: the
validator reads members by their snake_case JSON names, so no global naming policy is touched.)
The record shape, with the step-5 rule each field must pass before it is projected:

```
EvidenceIngestRequest(
  string schema_version,          // present JSON string, exactly "freeboard.evidence.v1"
  string collector_id,            // present JSON string, <= 190 chars, equals credential's collector
  string run_id,                  // present JSON string, <= 190 chars, caller-supplied
  string? collector_version,      // absent or JSON string (provenance, e.g. image digest)
  DateTimeOffset started_at,      // from present JSON string, UTC ISO 8601 (else 422)
  DateTimeOffset finished_at,     // from present JSON string, UTC ISO 8601, >= started_at (else 422)
  IReadOnlyList<CheckInput> checks,          // present non-empty JSON array
  JsonElement? metadata)          // absent or JSON object (ValueKind == Object, else 422)
CheckInput(
  string name,                    // present JSON string, non-blank, <= 190 chars, unique within the run
  string severity,                // present JSON string, hard | soft
  string status,                  // present JSON string, pass | fail | unknown | not_applicable
  string? detail,                 // absent or JSON string
  JsonElement? data)              // absent or JSON object (ValueKind == Object, else 422)
```

Because these records are populated only from already-validated `JsonElement` values, the
timestamps can be the typed `DateTimeOffset` the store needs, and the scalars are plain strings:
the wrong-shape cases were already rejected as `422` in step 5, so no projection can throw a
`400`.

Checks are an ARRAY (Plan A), not a name-keyed map (Plan B). Both plans enforce unique,
non-blank check names; the array projects cleanly to a .NET record list, preserves order via
`seq`, and keeps each check self-describing and forward-extensible. The uniqueness the map
would give for free is enforced in validation and by a DB unique key `(evidence_run_id, name)`.

Timestamps use `started_at`/`finished_at` (Plan B) rather than Plan A's single `collected_at`,
so the `finished_at >= started_at` ordering rule has both endpoints to check.

Responses: `201 Created` (new) and `200 OK` with a replay indicator (identical replay), both
returning `{ evidence_id, collector_id, run_id, received_at, hard_fail_count, soft_fail_count,
total_count }`; `409` on a changed body for a known run; `400` on malformed JSON; `422` on
semantic validation; `401`/`403` on auth; `413` on oversize; `503` on store failure. RFC 7807
problem bodies for the error cases.

### D8. Read-only-mode exemption via a dedicated marker

Add an `IngestEndpoint` marker (distinct from `AuthEndpoint`) in `ApiRoutes.cs` and one
condition in `GitOpsReadOnlyMiddleware` so a marked endpoint is exempt from the `409`. Mark
only the ingest POST. Credential issuance/revocation are admin config actions and are NOT
marked (they `409` in read-only mode). Plan B's "dedicated marker, do not overload
`AuthEndpoint`" is adopted over reusing the auth marker: overloading blurs the middleware's
intent. The same `IngestEndpoint` marker is also read by the route-metadata guard test (see D3)
to recognise the ingest route as gated; one marker serves both the read-only middleware and the
guard, so there is no second marker to keep in sync.

### D9. Payload size limit

Explicit per-endpoint request body cap of 1 MiB (1048576 bytes) via endpoint metadata
(`IRequestSizeLimitMetadata`), not a lowered global Kestrel default, so only the ingest route
is constrained and an oversize body is rejected `413` before the whole body is buffered.
Evidence JSON is small; 1 MiB is generous for it.

### D10. Persistence (migration 014)

One migration `014_evidence_ingest.sql` creates three tables:

- `evidence_runs`: `id CHAR(26)` PK; `collector_id VARCHAR(190) utf8mb4_bin` (plain, NO FK);
  snapshot columns `collector_title VARCHAR(512) NULL`, `control_id VARCHAR(190) utf8mb4_bin
  NOT NULL` (no FK; the source `evidence_collectors.control_id` is NOT NULL, so an ingest always
  has a control id to snapshot), `vendor_id VARCHAR(190) utf8mb4_bin NULL` (no FK; nullable
  because its source `evidence_collectors.vendor_id` is nullable), `collector_type
  VARCHAR(32) NULL`; `run_id VARCHAR(190)`; `schema_version VARCHAR(64)`; `collector_version
  VARCHAR(512) NULL`; `started_at DATETIME(6)`; `finished_at DATETIME(6)`; `received_at
  DATETIME(6)`; `request_body_sha256 BINARY(32)`; `hard_fail_count INT`, `soft_fail_count
  INT`, `total_count INT`; `metadata JSON NULL`; unique key `(collector_id, run_id)`.
- `evidence_run_checks`: `id CHAR(26)` PK; `evidence_run_id CHAR(26)` FK to `evidence_runs`
  `ON DELETE CASCADE`; `name VARCHAR(190)`; `severity VARCHAR(8)`; `status VARCHAR(16)`;
  `detail TEXT NULL`; `data JSON NULL`; `seq INT`; index on `evidence_run_id`; unique
  `(evidence_run_id, name)`.
- `collector_credentials`: `id CHAR(26)` PK; `collector_id VARCHAR(190) utf8mb4_bin` FK to
  `evidence_collectors(id)` `ON DELETE CASCADE`; `token_hash BINARY(32)` unique;
  `token_key_version INT`; `created_at DATETIME(6)`; `last_seen_at DATETIME(6) NULL`;
  `expires_at DATETIME(6) NULL`; `revoked_at DATETIME(6) NULL`.

New abstractions in `Freeboard.Persistence`: `IEvidenceIngestStore`
(`TryAppendAsync(run) -> EvidenceAppendResult`) and `ICollectorCredentialStore`
(`FindByTokenHashAsync`, `IssueAsync`, `RevokeAsync`, `TouchLastSeenAsync`, plus a
collector-exists check via the existing store). Concrete `MySql*` implementations with Dapper,
matching `MySqlComplianceStore`. Web DI registers them; the migration runner and importer are
unchanged.

`TryAppendAsync` returns an `EvidenceAppendResult(EvidenceId, WasNew, BodyMatches, ReceivedAt,
HardFailCount, SoftFailCount, TotalCount)`. The three-value `(evidenceId, wasNew, bodyMatches)`
tuple is not enough to build the specified `200`/`201` body: on a replay the `received_at` and
the counts belong to the EXISTING row, not the incoming request, so the store must surface the
PERSISTED run's values. For a new insert the store returns the values it just wrote; for a
replay (`WasNew == false`, `BodyMatches == true`) it returns the ORIGINAL stored `received_at`
and the stored counts, read back from the existing row in the same transaction, so the endpoint
can return the identical `{ evidence_id, collector_id, run_id, received_at, hard_fail_count,
soft_fail_count, total_count }` body for both new and replay. `collector_id`/`run_id` echo the
request. On a conflict (`WasNew == false`, `BodyMatches == false`) the counts/`received_at` are
irrelevant; the endpoint returns `409` from the flags alone.

`TouchLastSeenAsync(credentialId, seenAt)` writes `collector_credentials.last_seen_at` as a
best-effort update on successful collector authentication, mirroring the session handler's
`ISessionStore.TouchLastSeenAsync` so operators see real collector activity. A failure here
must never fail an already-authenticated request, so the handler swallows it (same pattern as
`BearerAuthenticationHandler`). This is the sole writer of `last_seen_at`, so the column is
not dead schema.

### D11. File changes

New:

- `src/Freeboard.Persistence/Migrations/014_evidence_ingest.sql`
- `src/Freeboard.Persistence/IEvidenceIngestStore.cs`,
  `src/Freeboard.Persistence/MySqlEvidenceIngestStore.cs`
- `src/Freeboard.Persistence/ICollectorCredentialStore.cs`,
  `src/Freeboard.Persistence/MySqlCollectorCredentialStore.cs`
- Evidence read/write models (extend `ComplianceReadModels.cs` or a new `EvidenceModels.cs`)
- `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`,
  `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs`
- `src/Freeboard/Auth/CollectorBearerAuthenticationHandler.cs`
- `docs/evidence-ingest.md`, `docs/schemas/evidence-ingest.v1.schema.json`
- `collectors/Dockerfile`, `collectors/entrypoint.sh`, `collectors/example/*`,
  `collectors/README.md`
- Tests under `tests/` for ingest, auth, idempotency/replay/conflict, read-only exemption,
  migration, the JSON Schema drift check, and a container smoke test.

Modified:

- `src/Freeboard/Api/ApiRoutes.cs` (add `IngestEndpoint` marker)
- `src/Freeboard/GitOps/GitOpsReadOnlyMiddleware.cs` (exempt the marker)
- `src/Freeboard/Program.cs` (register the collector scheme + named policy, DI for the new
  stores, map the new endpoints)
- `src/Freeboard.Persistence/PersistenceServiceCollectionExtensions.cs` (register stores)
- `src/Freeboard.CLI/CollectorCommands.cs`, `IFreeboardApiClient.cs`,
  `HttpFreeboardApiClient.cs` (credential issue/revoke)

### D12. Verification strategy

- Web tests via `WebApplicationFactory` with in-memory fakes for `IEvidenceIngestStore` and
  `ICollectorCredentialStore` (mirrors the suite's existing store fakes, no MySQL needed):
  valid ingest `201` + checks and snapshot captured + counts correct; malformed JSON `400`;
  a JSON type mismatch on any field is `422` not `400` (non-string `collector_id`/
  `schema_version`, non-string or numeric `severity`/`status`, a non-array `checks`);
  `metadata`/`data` present but not a JSON object `422`; wrong `schema_version`, bad
  severity/status, empty checks, duplicate check name, a missing/JSON-null/non-timestamp
  `started_at`/`finished_at`, `finished_at < started_at`, collector-id mismatch, unknown
  collector all `422`; missing/invalid credential `401`; revoked/expired
  `403` (produced by the ingest authorization policy, not the handler); session token at ingest
  `401`; duplicate run same body `200` replay with the ORIGINAL stored `received_at` and counts;
  duplicate run different body `409`; oversized body `413`; store failure `503`; read-only mode
  does NOT `409` ingest but DOES `409` credential issuance; non-admin caller `403` on credential
  issue/revoke. The valid-`201` test posts the SAME in-repo example payload used by the JSON
  Schema drift test, so the example, the published schema, and the endpoint's `[JsonPropertyName]`
  bindings are transitively linked by automated tests and cannot silently drift.
- Auth handler unit tests: valid collector token resolves the collector principal WITH the
  active-credential claim; malformed/unknown-key token `AuthenticateResult.Fail` (`401`) without
  a lookup; a revoked or expired credential authenticates SUCCESSFULLY but WITHOUT the active
  claim (the `403` is then produced by the ingest policy, asserted in the WebApplicationFactory
  tests); collector token rejected at a session endpoint and a session token rejected at ingest.
- Route-metadata test: `RouteAuthzMetadataTests` gains cases asserting the credential issue and
  revoke routes carry `SystemAdmin` + `alwaysEnforce: true`, proving the admin gate is enforced.
  `EveryMutatingApiRouteIsGatedOrAllowlisted` is updated so the `evidence` ingest route passes
  by carrying the `IngestEndpoint` marker AND being bound to the named ingest policy (not by a
  path-only exemption), and a dedicated positive test asserts the ingest route carries the marker
  and that the resolved ingest policy binds the collector scheme - so an accidentally-ungated
  ingest route still fails the build.
- JSON Schema drift test: validate the in-repo example payload(s) against
  `docs/schemas/evidence-ingest.v1.schema.json`.
- Persistence integration tests gated on `FREEBOARD_TEST_DB`: migration `014` applies on a
  fresh database; `(collector_id, run_id)` unique key enforced; transactional all-or-nothing
  write; duplicate same-hash vs different-hash reporting; a GitOps sync that prunes a collector
  with existing evidence does not delete or block that evidence (no FK); `collector_credentials`
  cascade on collector deletion; credential lookup/issue/revoke round-trip.
- Architecture/dependency tests: prove `Freeboard.Agent` and `Freeboard.CLI` carry no
  `Freeboard.Enterprise` dependency (kept from both plans).
- Container: a scripted smoke test runs the worked example against a running instance with an
  issued credential and asserts Evidence landed. Full `docker` E2E is CI-gated / manual (like
  the Playwright suite), since building and running an image is not part of `dotnet test`.

## Risks / Trade-offs

- A long-lived machine bearer token is a standing secret. -> Mitigation: per-collector scope,
  keyed-HMAC storage (a DB dump is not replayable), revocable, optional `expires_at`, and
  TLS-only transport. Revocation and expiry are the retire controls.
- Two bearer token types share one wire format. -> Mitigation: route-scoped schemes with
  disjoint lookup tables; each rejects the other's token with `401`. Covered by cross-token
  tests.
- Snapshot drift from the live register. -> Accepted by design (D1): the snapshot is a
  point-in-time record of collector identity; evidence history must not change when the
  register is refactored. The scoring engine joins on `collector_id`/`control_id` values, not
  a FK.
- snake_case binding drift. -> Mitigation: `[JsonPropertyName]` on the request records plus
  contract tests, and the JSON Schema drift test asserting the example matches the published
  schema.
- Ologist-side script dependency. -> Mitigation: freeze and version the contract in
  `docs/evidence-ingest.md` and the JSON Schema (a contract/schema version); the reference
  wrapper + example prove the shape before scripts adapt.
- Full container E2E is not part of `dotnet test`. -> Mitigation: gate it like the existing
  Playwright E2E; keep automated coverage on the endpoint, stores, migration, and schema.

## Migration Plan

- Ship migration `014` as a new forward-only file; operators apply it with the existing
  `system migrate` command. No existing table changes, so rollback is a table drop; no data is
  rewritten.
- Deploy the web change (new scheme, endpoints, DI). Existing endpoints and the human auth
  path are unchanged.
- An admin issues a collector credential per collector (optionally with an expiry); the
  container wrapper is configured with `FREEBOARD_BASE_URL`, `FREEBOARD_INGEST_TOKEN`, and
  `FREEBOARD_COLLECTOR_ID`.
- Rollback: revoke credentials, remove the ingest route, drop the three `014` tables. Because
  Evidence is additive runtime data, removing it does not affect the static compliance domain.

## Open Questions

1. Check payload shape: array with a DB unique `(run_id, name)` (chosen) vs a name-keyed map.
   The array binds cleanly and stays forward-extensible; revisit only if downstream consumers
   strongly prefer a map.
2. Revoked/expired credential response: `403` (chosen, distinguishes once-valid from unknown)
   vs a uniform `401` with no oracle. The `403` aids operator debugging at the cost of a minor
   validity oracle on a machine credential.
3. Body size limit: 1 MiB (chosen). Raise if a real collector's evidence JSON approaches it.
