## 1. Persistence: collector credential store and migration

- [x] 1.1 Add `src/Freeboard.Persistence/Migrations/014_collector_credentials.sql`
      creating `collector_credentials` only (keyed HMAC + key version, optional
      expiry, revoked_at, last_seen_at; UNIQUE token_hash; FK to
      `evidence_collectors` ON DELETE CASCADE).
- [x] 1.2 Add `ICollectorCredentialStore` and `CollectorCredentialRow`
      (salvaged) and `MySqlCollectorCredentialStore` (FindByTokenHash, Issue,
      Revoke scoped to collector, TouchLastSeen).
- [x] 1.3 Register `ICollectorCredentialStore` in
      `PersistenceServiceCollectionExtensions` beside the existing
      `AddEvidenceWriteStore`; do NOT reintroduce `AddEvidenceIngest` or any
      second evidence store.

Commit: `feat(persistence): add per-collector machine credential store`

## 2. Web auth: collector bearer scheme and ingest policy

- [x] 2.1 Add `src/Freeboard/Auth/CollectorBearerAuthenticationHandler.cs`
      (salvaged): scheme, claims, active-claim gating, best-effort last-seen.
- [x] 2.2 Wire the scheme and the `FreeboardEvidenceIngest` policy in
      `Program.cs` (collector scheme is a second scheme; session scheme stays
      the default).

Commit: `feat(web): add collector bearer scheme and ingest policy`

## 3. Web: evidence ingest endpoint on the shared write store

- [x] 3.1 Add the `IngestEndpoint` marker + `MarkIngestEndpoint` helper to
      `src/Freeboard/Api/ApiRoutes.cs`.
- [x] 3.2 Exempt the `IngestEndpoint`-marked route in
      `GitOpsReadOnlyMiddleware` (beside the existing `AuthEndpoint` exemption).
- [x] 3.3 Add `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs` reworked onto
      `IEvidenceWriteStore`: manual body read (1 MiB cap -> 413), malformed JSON
      -> 400, semantic validation -> 422, collector_id-vs-credential check,
      register snapshot from `IComplianceStore.GetEvidenceCollectorsAsync()`
      (unknown collector -> 422; null `Vendor` -> 422, no synthetic fallback),
      requirement-under-control via `ControlRow.MapsTo`
      (`GetControlsAsync()`), org-in-scope via
      `GetStatementOfApplicabilityInputsAsync()` + `StatementOfApplicability.Resolve`
      (`SoaNode` disposition resolves `In`), derived Pass/Fail verdict, map
      `run_id` to `collector_ref = "{collector_id}:{run_id}"` (validate <= 190
      chars -> 422), store the full body as raw payload, pre-validate check-name
      uniqueness/blankness so the only reachable store conflict is the idempotency
      key, map `WriteResult` success -> 201 and `IsConflict` -> 200 replay (body
      echoes request-derived collector_id/run_id/counts; no `evidence_id`).
- [x] 3.4 Map the endpoint in `Program.cs` (`MapEvidenceIngestEndpoints`).

Commit: `feat(web): add evidence ingest endpoint on shared evidence model`

## 4. Web: credential admin API

- [x] 4.1 Add `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs`
      (salvaged): system-admin force-enforced issue/revoke, no ingest marker.
- [x] 4.2 Map the routes in `Program.cs` (`MapCollectorCredentialEndpoints`).

Commit: `feat(web): add collector credential admin API`

## 5. CLI: credential commands

- [x] 5.1 Add `credential issue` / `credential revoke` subcommands to
      `CollectorCommands` (via the HTTP API only; print the raw token once).
- [x] 5.2 Add `IssueCollectorCredentialAsync` / `RevokeCollectorCredentialAsync`
      to `IFreeboardApiClient` + `HttpFreeboardApiClient`.

Commit: `feat(cli): add collector credential issue and revoke commands`

## 6. Contract and reference collector

- [x] 6.1 Add `docs/schemas/evidence-ingest.v1.schema.json` narrowed to the
      shared model (check `result` `pass|fail`, no `status`/`data`; required
      `organisation_id`, `requirement_id`, `collected_at`).
- [x] 6.2 Add `docs/evidence-ingest.md` documenting the route, credential,
      narrowed contract, derived verdict, and 200-replay idempotency.
- [x] 6.3 Add `collectors/` (Dockerfile, README, entrypoint.sh, example
      collector) with the example payload updated to the narrowed contract.

Commit: `docs(evidence): publish evidence ingest contract and reference collector`

## 7. Tests and verification

- [x] 7.1 Web tests: ingest 201 happy path; 401 session token / unknown token,
      403 revoked/expired; 422 cases (schema_version, severity/result, empty and
      duplicate check names, non-UTC/absent timestamp, collector mismatch,
      unknown collector, null-vendor collector, requirement-not-under-control,
      org-not-in-scope, over-190-char collector_ref); 400 malformed; 413 oversize;
      200 duplicate replay (body has no evidence_id); read-only exemption (ingest
      works, credential admin 409s). Use fake `IEvidenceWriteStore`,
      `IComplianceStore`, and `ICollectorCredentialStore`.
- [x] 7.2 Extend `RouteAuthzMetadataTests`: credential admin force-enforce cases
      + ingest marker/policy assertions.
- [x] 7.3 Schema test: the published example validates against the JSON Schema.
- [x] 7.4 MySQL integration tests (gated on `FREEBOARD_TEST_DB`): credential
      issue/find/revoke/touch, `(vendor, collector_ref)` idempotency conflict,
      FK cascade on collector delete.
- [x] 7.5 CLI tests: `credential issue` prints token once; `revoke` outcome
      mapping.
- [x] 7.6 Run `dotnet build` and `dotnet test`; run
      `npx markdownlint-cli2` on the new docs.

Commit: `test(evidence): cover ingest, credentials, and contract`
