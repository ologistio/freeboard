## Context

`main` already holds a general-purpose, append-only evidence model:

- `evidence_runs(id, kind, organisation_id, requirement_id, vendor,
  collector_ref, result, collected_at, received_at, raw_payload, created_at)`
  with `UNIQUE (vendor, collector_ref)` and BEFORE UPDATE/DELETE triggers.
- `evidence_checks(id, evidence_id, name, severity, result, ordinal, detail)`.
- `IEvidenceWriteStore.AppendEvidenceAsync(NewEvidenceRun{ OrganisationId,
  RequirementId, Vendor, CollectorRef, Result(Pass|Fail), CollectedAt,
  ReceivedAt?, RawPayload?, Checks[NewEvidenceCheck(Name, Severity(Hard|Soft),
  Result(Pass|Fail), Detail?)] })` returning `WriteResult`. It has **no consumer
  yet** on `main` - the ingest endpoint is its first caller.
- `WriteResult(Error?, IsConflict)` - success is `Error == null`; a duplicate
  `(vendor, collector_ref)` returns `IsConflict == true`; a validation failure
  returns a non-conflict error. The store does **not** compare a body hash.

The collector register (`evidence_collectors`) maps a collector `id` to a
`control_id` and an optional `vendor_id`; it has no `requirement_id` and no
`organisation_id`. `control_requirements` maps a control to requirements
**many-to-many**. `organisation` is a separate axis (`requirement_scopes` maps
`(organisation, requirement)` for scoping); nothing ties a collector to an
organisation.

The original branch (`feat/evidence-ingest-endpoint`, do not check out) holds the
salvageable ingest endpoint, the collector bearer scheme, the credential store,
the contract, the reference collector, and the CLI commands. Its own evidence
persistence is the duplicate being dropped.

## Goals / Non-Goals

**Goals:**

- One evidence write path: the ingest endpoint writes through `main`'s
  `IEvidenceWriteStore`.
- Preserve the salvaged endpoint, credential, contract, collector, CLI, and
  read-only guard treatment.
- Map the published contract onto `main`'s columns with the smallest coherent
  change and no new evidence store.

**Non-Goals:**

- No second evidence store, no schema change to `evidence_runs` /
  `evidence_checks`.
- No scoring or control-level rollup at ingest (assessment stays a read-time
  derivation in the existing evidence-persistence capability).
- No change to the human session bearer scheme.

## Decisions

### Field mapping onto `NewEvidenceRun`

| `NewEvidenceRun` field | Source | Notes |
| --- | --- | --- |
| `OrganisationId` | payload `organisation_id` | Register has no org axis; validated in-scope (Decision 1, RD1). |
| `RequirementId` | payload `requirement_id` | Control maps to many requirements; validated to be one of the collector control's `MapsTo` (Decision 1, RD1). |
| `Vendor` | register `EvidenceCollectorRow.Vendor` | Required. A registered collector with a null vendor CANNOT ingest; the endpoint rejects the request (Decision 5). No synthetic fallback. |
| `CollectorRef` | `"{collector_id}:{run_id}"` | Namespaces the run under the collector so idempotency is collector-scoped even if two collectors share a vendor. Validated <= 190 chars to fit `collector_ref VARCHAR(190)`, else 422. |
| `Result` | derived server-side | Decision 3. |
| `CollectedAt` | payload `collected_at` | UTC ISO 8601. |
| `ReceivedAt` | server `DateTime.UtcNow` | |
| `RawPayload` | the exact submitted JSON body | Retains provenance fields (`collector_version`, `metadata`, per-check `data`) that have no first-class column. |
| `Checks[].Name/Severity/Result/Detail` | payload checks | Decision 4. |

Identity that the credential proves (collector, control, vendor) is snapshotted
from the register; the endpoint asserts the payload `collector_id` equals the
authenticated credential's collector before writing.

### Decision 1: organisation_id and requirement_id come from the payload, validated

The register gives control and vendor, not requirement or organisation. `main`'s
columns are NOT NULL. `control -> requirement` is 1:many and there is no
collector-to-organisation link, so neither field is derivable from the credential
alone.

The endpoint requires `organisation_id` and `requirement_id` in the payload and
validates them against `main`'s real read surfaces (all on `IComplianceStore`):

- Collector lookup: `IComplianceStore.GetEvidenceCollectorsAsync()` ->
  `EvidenceCollectorRow`. The row for the authenticated `collector_id` gives its
  `Control` and its nullable `Vendor`. An unknown collector is 422.
- Requirement-under-control: `IComplianceStore.GetControlsAsync()` ->
  `ControlRow.MapsTo` (the resolved requirement ids of a control). The payload
  `requirement_id` MUST be in the `MapsTo` of the collector's `Control`; otherwise
  422. There is no `control_requirements` query method - `MapsTo` is the resolved
  form exposed on the read model.
- Organisation-in-scope: `IComplianceStore.GetStatementOfApplicabilityInputsAsync()`
  -> `SoaInputs`, resolved by
  `StatementOfApplicability.Resolve(organisations, scopes, requirements,
  requirementScopes, standardId)` -> `IReadOnlyList<SoaNode>`. `standardId` is the
  requirement's owning `RequirementRow.Standard`. The `SoaNode` for
  `organisation_id` must exist and its effective disposition for `requirement_id`
  must resolve `In` (node `Disposition == In` and the requirement is not an
  inherited/explicit `Out` deviation in `SoaNode.Requirements`); otherwise 422.
  A raw `IComplianceStore.GetRequirementScopesAsync` exists, but there is no single
  "is (org, requirement) in scope" predicate; the SoA projection is the codebase's
  scope-resolution surface (it reads organisations, scopes, requirements, and
  requirement-scopes in one snapshot and applies inheritance), and the endpoint
  reuses it rather than reimplementing scope resolution from raw rows.

`StatementOfApplicability` lives in the web project (`src/Freeboard/Compliance/`),
the same project as the ingest endpoint, so reusing it introduces no reference-
graph change.

Rationale: smallest change that fits `main`'s model with no schema churn and
keeps idempotency collector-scoped. A physical collector deployment is thus bound
to one `(organisation, requirement)` target (or loops and posts once per target).

Alternatives considered:

- Fan out one run per `(organisation, requirement)` the control resolves to:
  write amplification, complex idempotency, scope resolution on the hot path.
  Rejected.
- Bind `organisation_id` to the credential (per-org credential) instead of the
  body: stronger trust boundary, but still leaves `requirement_id` 1:many and
  needs a credential-schema change. Deferred to future hardening (see RD1 and the
  V1 trust-limitation Risk); V1 keeps org payload-declared.
- Extend `main`'s model (add `control_id` / nullable columns): reopens the model
  and risks a second divergence. Rejected by the goal.

### Decision 2: idempotency and duplicate handling

Idempotency key on `main` is `UNIQUE (vendor, collector_ref)`. With
`vendor = EvidenceCollectorRow.Vendor` (required; a null vendor is rejected, see
Decision 5) and `collector_ref = "{collector_id}:{run_id}"`, the effective key is
`(vendor, collector_id, run_id)` - collector-scoped, matching the original
design's `(collector_id, run_id)` intent.

`main`'s store maps a duplicate to `WriteResult.IsConflict` and does **not**
compare a body hash, so it cannot tell an identical replay from a changed body.
The original design used a stored `request_body_sha256` to return 200 for an
identical replay and 409 for a changed body; that mechanism is dropped.

Decision (RD2, ratified): a store conflict returns **200 OK** (accepted replay),
NOT 409.

`WriteResult` is `(string? Error, bool IsConflict)` (see
`IComplianceWriteStore.cs`). On a duplicate the store returns
`WriteResult.Conflict(message)`: `IsConflict == true`, `Error` is a fixed human
string, and there is **no** `evidence_id`, `received_at`, or any stored-row
readback. The endpoint therefore cannot echo the original run's identity on a
replay and MUST NOT fabricate one. The 200 body carries only request-derived
values: `collector_id`, `run_id`, and the derived hard-fail / soft-fail / total
counts - the same shape as the 201 body minus any server-assigned id.

`MySqlEvidenceWriteStore` also returns `Conflict` for a duplicate check name
within one run. The endpoint pre-validates check-name uniqueness and blankness
(422) before calling the store, so by the time `AppendEvidenceAsync` runs the
only reachable conflict is the `(vendor, collector_ref)` idempotency collision.
Mapping `IsConflict -> 200` is therefore unambiguous here.

Rationale: the reference collector's retry loop resends the identical body under
the same `run_id` on any transient failure, including a lost `201` response after
a committed write. Returning 409 there would report a genuinely-landed evidence
run as a failure. 200-replay keeps retries safe.

Accepted loss (documented): a **different** body re-sent under the same `run_id`
is silently accepted as a 200 replay rather than rejected, because `main` stores
no body hash and the endpoint does no body comparison. Evidence is append-only
and the original run is unchanged, so this is a benign misbehaving-collector case.
The 200 response reflects the *original* landed run's identity only implicitly
(via the echoed `collector_id`/`run_id`); it does not and cannot confirm the
resent body matches what was stored.

### Decision 5: a null-vendor collector is rejected at ingest (RD3)

`main`'s `evidence_runs.vendor` is NOT NULL and is half the idempotency key, and
`EvidenceCollectorRow.Vendor` is nullable (a collector may be registered without a
vendor). A registered collector whose `Vendor` is null therefore has no valid
value for the run's `Vendor`.

Decision: the endpoint **rejects** such a request with **422 Unprocessable
Entity** and a distinct problem title (collector not configured for ingest -
missing vendor). It does **not** synthesise `collector_id` as the vendor.

Why 422 (not a 5xx or a bespoke config error): every other "well-formed request
that cannot be satisfied against the collector's registration" already maps to 422
here - unknown collector, `requirement_id` not in the control's `MapsTo`,
`organisation_id` not in scope. A null vendor is the same class: the payload is
syntactically valid but cannot be processed given the collector's current
registration. Keeping it 422 gives the ingest surface one semantic-rejection code
(400 malformed JSON, 401/403 auth, 413 oversize, 422 every value/config problem,
200 replay, 201 created) instead of introducing a fourth 4xx family for one case.
The fix is operator-side (set the collector's vendor in GitOps config), which the
problem detail names.

### Decision 3: run-level result is derived, not posted

`main` requires `evidence_runs.result` in `{Pass, Fail}`. The collector does not
post a verdict; the endpoint derives it: **Fail if any check has
`severity == hard` and `result == fail`, else Pass.** This matches the original
"hard fail is the V1 gate" rule. The original constraint "no verdict at ingest"
no longer applies because `main`'s model has a verdict column.

### Decision 4: check shape narrows to `main`

`main`'s `evidence_checks` is `severity(Hard|Soft) + result(Pass|Fail) + detail`,
with no status enum and no per-check `data`. The contract narrows accordingly:

- Per-check `status` (`pass|fail|unknown|not_applicable`) becomes `result`
  (`pass|fail`). `unknown` and `not_applicable` are removed from the contract:
  the collector resolves them before posting (a not-applicable check is omitted;
  an unknown is posted as `fail` or omitted). This keeps `main`'s two-value
  result honest rather than silently coercing four states into two on the server.
- Per-check `data` is dropped from the typed contract; the full submitted body is
  stored in `evidence_runs.raw_payload`, so structured findings survive at run
  level.
- Wire values stay lowercase (`hard`/`soft`, `pass`/`fail`); the endpoint maps
  them to `main`'s Pascal-case store values at the boundary.

`schema_version` stays `freeboard.evidence.v1`: the original contract never
shipped, so v1 is (re)defined here rather than bumped.

### Credential (unchanged from the salvaged work)

- `collector_credentials(id, collector_id, token_hash, token_key_version,
  created_at, last_seen_at, expires_at, revoked_at)`, `UNIQUE (token_hash)`, FK
  to `evidence_collectors(id)` **ON DELETE CASCADE** (a credential is live config,
  not history). Migration `014_collector_credentials.sql`.
- `CollectorBearerAuthenticationHandler` (scheme `FreeboardCollectorBearer`):
  shares the `v<keyId>.<secret>` wire format but queries only
  `collector_credentials`, so a session token 401s at ingest and a collector
  token 401s elsewhere. Unknown/malformed/hash-miss -> `Fail` (401). A recognised
  credential authenticates and carries the collector-id claim; it adds the active
  claim only when neither revoked nor expired.
- Ingest policy `FreeboardEvidenceIngest` binds the collector scheme and requires
  the collector-id claim AND the active claim, so a revoked/expired credential
  authenticates but is Forbidden (403); an unknown token 401s in the handler.
- Reuses the existing `ITokenHasher` (`MintPrefixed`/`TryHashPrefixed`); the raw
  token is shown once at issue and never stored.
- Credential admin routes reuse `RequirePermission(SystemAdmin,
  alwaysEnforce: true)`; they carry no ingest marker, so GitOps read-only mode
  409s them (minting a credential is admin config, not runtime evidence).

### Migration ordinal

`main` head migrations end at `013_attestation_templates.sql` (two `011`s:
`011_evidence.sql` and `011_vendors.sql`). The new credential migration is
**`014_collector_credentials.sql`** and contains the credential table only.
Confirmed.

### DI registration

Register `MySqlCollectorCredentialStore` beside `main`'s existing
`AddEvidenceWriteStore`. Prefer extending the web app's existing evidence
registration rather than reintroducing the original `AddEvidenceIngest` (which
also registered the now-dropped `IEvidenceIngestStore`). The endpoint resolves
`IEvidenceWriteStore` (append), `IComplianceStore` (register + control/requirement
lookups), and `ICollectorCredentialStore`.

### File changes

New:

- `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs` (reworked: writes via
  `IEvidenceWriteStore`, drops the body-hash/`EvidenceRunInput` path).
- `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs` (salvaged as-is).
- `src/Freeboard/Auth/CollectorBearerAuthenticationHandler.cs` (salvaged as-is).
- `src/Freeboard.Persistence/ICollectorCredentialStore.cs`,
  `src/Freeboard.Persistence/MySqlCollectorCredentialStore.cs` (salvaged as-is).
- `src/Freeboard.Persistence/Migrations/014_collector_credentials.sql`.
- `docs/evidence-ingest.md`, `docs/schemas/evidence-ingest.v1.schema.json`
  (reworked to the narrowed contract).
- `collectors/` (Dockerfile, README, entrypoint.sh, example/collect.sh; the
  example payload updated to the narrowed check shape and the new required
  fields).

Modified:

- `src/Freeboard/Program.cs`: add the collector scheme, the ingest policy, and
  `MapEvidenceIngestEndpoints` / `MapCollectorCredentialEndpoints`; register the
  credential store.
- `src/Freeboard/Api/ApiRoutes.cs`: add the `IngestEndpoint` marker +
  `MarkIngestEndpoint` helper.
- `src/Freeboard/GitOps/GitOpsReadOnlyMiddleware.cs`: exempt an endpoint carrying
  the `IngestEndpoint` marker (beside the existing `AuthEndpoint` exemption).
- `src/Freeboard.Persistence/PersistenceServiceCollectionExtensions.cs`: register
  `ICollectorCredentialStore`.
- `src/Freeboard.CLI/`: `collector credential issue|revoke` subcommands and the
  `IssueCollectorCredentialAsync` / `RevokeCollectorCredentialAsync` client
  methods (the `collector list` command is already on `main`).
- `tests/Freeboard.Web.Tests/RouteAuthzMetadataTests.cs`: the two credential
  admin inline cases + the ingest marker/policy assertions.

Dropped from the original branch (not carried into this change):

- `src/Freeboard.Persistence/Migrations/014_evidence_ingest.sql`'s `evidence_runs`
  and `evidence_run_checks` tables.
- `src/Freeboard.Persistence/IEvidenceIngestStore.cs`,
  `MySqlEvidenceIngestStore.cs`, `EvidenceAppendResult`, `EvidenceRunInput`,
  `EvidenceCheckInput`.
- The request-body-SHA-256 replay mechanism and the `AddEvidenceIngest`
  DI extension.

### Verification strategy

Follow `main`'s test tiers (CLAUDE.md):

- Web tests (`WebApplicationFactory`, in-memory fakes): ingest happy path (201),
  auth (401 session token at ingest, 403 revoked/expired, 401 unknown),
  validation (422 for wrong `schema_version`, bad severity/result, empty/duplicate
  check names, non-UTC/absent timestamp, `collector_id` mismatch, unknown
  collector, null-vendor collector, over-190-char `collector_ref`, requirement not
  under the control, org not in-scope), 400 malformed
  JSON, 413 oversize, 200 duplicate replay, and the read-only exemption (ingest
  works, credential admin 409s). Reuse a fake `IEvidenceWriteStore` and
  `ICollectorCredentialStore`.
- `RouteAuthzMetadataTests`: the ingest route carries the marker and binds the
  collector scheme; credential admin routes force-enforce SystemAdmin.
- Schema test: the published example validates against the JSON Schema.
- MySQL integration (gated on `FREEBOARD_TEST_DB`, skips cleanly otherwise):
  credential issue/find/revoke/touch, the `(vendor, collector_ref)` idempotency
  conflict, and the FK cascade on collector delete.
- CLI tests: `credential issue` prints the token once; `revoke` maps outcomes.
- `dotnet build` and `dotnet test`.

## Risks / Trade-offs

- [V1 trust limitation: no org-binding on the credential] -> The credential proves
  only the collector (and thus its control and vendor), not the org or requirement.
  A valid collector token can report for **any** in-scope `(organisation,
  requirement)` pair under its control, because both come from the payload and are
  validated only for existence/scope, not against the credential. This is an
  accepted V1 limitation. Mitigation now: server-side validation via
  `ControlRow.MapsTo` and the SoA projection. Future hardening: org-scoped (or
  org+requirement-scoped) credentials, which needs a credential-schema change -
  out of scope for V1.
- [200-replay accepts a changed body under a repeated run_id] -> Undetected
  because `main` stores no body hash and the endpoint does no body comparison. The
  original run is unchanged (append-only) and the contract mandates identical-body
  retries. Accepted, per RD2.
- [Provenance fields (collector_version, metadata, per-check data) have no typed
  column] -> Retained only inside `raw_payload`. Acceptable: they are provenance,
  not queried at ingest.
- [A null-vendor collector cannot ingest] -> By design (Decision 5 / RD3): such a
  request is 422 with an operator-actionable problem detail, rather than writing a
  synthetic vendor. Acceptable: registering a vendor is a one-line GitOps config
  fix.

## Migration Plan

Additive, forward-only: `014_collector_credentials.sql` creates one table and
alters nothing. No data backfill. Rollback is dropping the table (no other schema
depends on it). The endpoint and scheme are inert until a credential is issued.

## Plan provenance and resolved decisions

This change synthesises two independent rework plans. Attribution:

- From Plan A (kept): the manual-body / `JsonElement` validation approach (400
  only on malformed JSON, 422 for value/type problems), the `JsonElement`
  `ValueKind` typing rule, the field-mapping table onto `NewEvidenceRun`, the
  credential/scheme/policy salvage, the GitOps read-only exemption via the
  `IngestEndpoint` marker, and the file kept/modified/dropped list.
- From Codex (folded in): `collector_id` body must equal the credential claim;
  `collector_ref = "{collector_id}:{run_id}"` with the 190-char cap; run `Result`
  derived server-side (Fail iff any Hard check Fails); `RawPayload` = the full
  validated JSON so `collector_version`/`metadata`/dropped per-check `data`
  survive; the check contract narrowed to `severity(Hard|Soft)` +
  `result(Pass|Fail)` with `unknown`/`not_applicable` rejected 422; migration
  `014_collector_credentials.sql` (that table only); reuse `AddEvidenceWriteStore`
  and drop `IEvidenceIngestStore` / `MySqlEvidenceIngestStore` /
  `EvidenceAppendResult` / the body-hash path.

Binding mediator decisions (were open questions; now ratified):

1. **RD1 - org/requirement sourcing:** PAYLOAD-DECLARED and validated (Decision 1).
   Payload carries `organisation_id` and `requirement_id`; the endpoint validates
   the collector exists, `requirement_id` is in the collector control's
   `ControlRow.MapsTo`, and `(organisation_id, requirement_id)` resolves `In` via
   the SoA projection. No org-binding on the credential for V1; the trust
   limitation is documented in Risks. Codex and Plan A agreed on payload+validate;
   the divergence (Codex weighed a credential-bound org) is resolved in favour of
   payload-declared for V1.
2. **RD2 - duplicate semantics:** 200 REPLAY on any store conflict (Decision 2),
   NOT 409. Codex initially recommended 409-strict; resolved to 200-replay to keep
   the reference collector's lost-response retry safe. The accepted loss (changed
   body under a repeated `run_id`) and the exact 200 body (no `evidence_id`) are
   documented.
3. **RD3 - null vendor:** REJECT at ingest with 422 (Decision 5). Plan A's earlier
   `vendor_id ?? collector_id` synthetic fallback is corrected: a null-vendor
   collector cannot ingest. Codex and the mediator agreed on rejection.
