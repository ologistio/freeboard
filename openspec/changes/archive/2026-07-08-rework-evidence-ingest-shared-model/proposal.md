## Why

Issue #55 built an authenticated evidence ingest endpoint together with its own
evidence persistence (`evidence_runs` / `evidence_run_checks`,
`IEvidenceIngestStore`). While that work was in progress, `main` merged a
parallel, general-purpose evidence model (`evidence_runs`, `evidence_checks`,
`attestation_responses`, `IEvidenceWriteStore`) that stores the same runtime
evidence. Shipping the original branch as-is would land a second, duplicate
persistence system for the same data. This change reworks the ingest endpoint
onto `main`'s evidence model and drops the duplicate store, so there is one
evidence write path.

## What Changes

- Add the authenticated evidence ingest endpoint
  `POST /api/v1/freeboard/evidence`, writing through `main`'s existing
  `IEvidenceWriteStore.AppendEvidenceAsync` (no new evidence store).
- Add the per-collector machine credential: a `collector_credentials` table, the
  `CollectorBearerAuthenticationHandler` bearer scheme, and the named ingest
  authorization policy. Credentials carry no human identity, are revocable, and
  store only a keyed HMAC of the token.
- Add the system-admin credential admin API
  (`POST`/`DELETE /api/v1/freeboard/evidence-collectors/{id}/credentials[/{credId}]`)
  and the CLI `freeboard collector credential issue|revoke` commands.
- Publish the ingest contract: `docs/evidence-ingest.md` and the JSON Schema
  `docs/schemas/evidence-ingest.v1.schema.json`, narrowed to `main`'s check
  shape (severity `hard`/`soft`, result `pass`/`fail`).
- Add the reference collector container under `collectors/`.
- Mark the ingest route with an `IngestEndpoint` metadata marker so the GitOps
  read-only middleware exempts it (evidence is runtime data), and extend the
  route-authz guard test to recognise the collector-scheme gate.
- Add migration `014_collector_credentials.sql` (the credential table only).
- **BREAKING** (relative to the unshipped original branch, not to `main`): the
  ingest payload's per-check `status` (`pass|fail|unknown|not_applicable`) is
  replaced by `result` (`pass|fail`); per-check `data` and the dedicated
  `metadata`/`collector_version`/`started_at` columns are dropped from the typed
  contract. The payload gains `organisation_id`, `requirement_id`, and
  `collected_at` to satisfy `main`'s required columns.
- **DROPPED** from the original branch: migration `014_evidence_ingest.sql`'s
  `evidence_runs` / `evidence_run_checks` tables, `IEvidenceIngestStore` /
  `MySqlEvidenceIngestStore`, `EvidenceAppendResult`, and the request-body-hash
  replay mechanism.

## Capabilities

### New Capabilities

- `evidence-ingest`: the authenticated `POST /evidence` endpoint, its request
  contract, its mapping onto `main`'s `IEvidenceWriteStore`, idempotency and
  error semantics, and its GitOps read-only exemption.
- `collector-credentials`: the per-collector machine credential - issuance,
  revocation, storage (keyed HMAC only), the bearer scheme, and the ingest
  authorization policy.

### Modified Capabilities

<!-- None. main's evidence-persistence capability is consumed unchanged. -->

## Impact

- New: `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`,
  `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs`,
  `src/Freeboard/Auth/CollectorBearerAuthenticationHandler.cs`,
  `src/Freeboard.Persistence/ICollectorCredentialStore.cs`,
  `src/Freeboard.Persistence/MySqlCollectorCredentialStore.cs`,
  `src/Freeboard.Persistence/Migrations/014_collector_credentials.sql`,
  `docs/evidence-ingest.md`, `docs/schemas/evidence-ingest.v1.schema.json`,
  `collectors/`.
- Modified: `src/Freeboard/Program.cs` (scheme, policy, endpoint wiring),
  `src/Freeboard/Api/ApiRoutes.cs` (`IngestEndpoint` marker),
  `src/Freeboard/GitOps/GitOpsReadOnlyMiddleware.cs` (exempt marked ingest),
  `src/Freeboard.Persistence/PersistenceServiceCollectionExtensions.cs`
  (register the credential store beside the existing `AddEvidenceWriteStore`),
  `src/Freeboard.CLI/` (credential subcommands + API client methods),
  `tests/Freeboard.Web.Tests/RouteAuthzMetadataTests.cs`.
- All code is MIT: it lives in `Freeboard` (web), `Freeboard.Persistence`, and
  `Freeboard.CLI`, none of which reference `Freeboard.Enterprise`.
