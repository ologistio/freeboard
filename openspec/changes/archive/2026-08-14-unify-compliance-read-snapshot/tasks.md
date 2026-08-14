## 1. The snapshot read model (`refactor(persistence)`)

- [x] 1.1 Add `ComplianceReadSet` to `src/Freeboard.Persistence/ComplianceReadModels.cs`: a
  `[Flags]` enum with `Assets`, `Standards`, `Requirements`, `Controls`, `Scopes`, `Collectors`,
  `IntegrationConnections`, `VendorAssurances`.
- [x] 1.2 Add `ComplianceSnapshot` to the same file: one record carrying the requested lists and
  the `ComplianceReadSet` it was read with. Each list is exposed through a property backed by a
  nullable field.
- [x] 1.3 Add `ComplianceReadSetNotRequestedException` in `Freeboard.Persistence`, thrown when a
  caller reads a list its snapshot did not name. Its message names the set. Do NOT derive it
  from `InvalidOperationException`.
- [x] 1.4 Replace the ten read methods on `src/Freeboard.Persistence/IComplianceStore.cs` with
  `Task<ComplianceSnapshot> GetSnapshotAsync(ComplianceReadSet sets, CancellationToken ct =
  default)`. Keep `GetCountsAsync`. Delete `SoaInputs`, `SoaDrilldownInputs`, and
  `VendorAssuranceInputs`.
- [x] 1.5 Implement `GetSnapshotAsync` in `src/Freeboard.Persistence/MySqlComplianceStore.cs`:
  one method that opens a connection, runs only the SELECTs the named sets need, and wraps them
  in a `RepeatableRead` transaction when the snapshot needs more than one statement. Reuse the
  existing SELECT constants and row mappers unchanged. `Controls` counts as two statements.
- [x] 1.6 Delete `GetStandardsAsync`, `GetRequirementsAsync`, `GetControlsAsync`,
  `GetAssetsAsync`, `GetScopesAsync`, `GetCollectorsAsync`, `GetIntegrationConnectionsAsync`,
  `GetStatementOfApplicabilityInputsAsync`, `GetStatementOfApplicabilityDrilldownInputsAsync`,
  and `GetVendorAssuranceInputsAsync` from the interface and the implementation. Leave no
  wrapper behind: a wrapper returns a list rather than a snapshot, so its caller records nothing
  a structural test can assert against.
- [x] 1.7 Update the XML docs on `IComplianceStore` to state the rule (one decision names its
  sets and gets one snapshot) rather than restating the enum.

## 2. The request cache and snapshot reuse (`fix(authz)`)

- [x] 2.1 In `src/Freeboard/Authz/AuthzRequestCache.cs`, replace the single
  `VendorAssuranceInputs` memo with a list of taken `ComplianceSnapshot` values and a
  `GetSnapshotAsync(ComplianceReadSet sets, CancellationToken ct)` that returns the first taken
  snapshot whose sets cover `sets`, and otherwise reads and keeps a new one. A FAULTED read keeps
  nothing, exactly as the assurance memo it replaces: a later gate then reads the `assets` table
  alone and answers, which is what stops an unmigrated payload table closing every gated write.
  Keep the existing test that pins this, retargeted at the snapshot memo.
- [x] 2.2 Point `GetAssetsAsync` at `GetSnapshotAsync(ComplianceReadSet.Assets)` and delete the
  standalone assurance read it replaces, keeping the gate path on the `assets` table and no
  payload table.
- [x] 2.3 Key the `AssetsById` ancestry index by the asset list it indexes, and add an
  `OrganisationResource` taking the `ComplianceSnapshot` the caller already holds. It is
  synchronous: a caller holding a snapshot needs no read. Keep `OrganisationResourceAsync` as it
  is, over the assets-only snapshot.
- [x] 2.4 Update the `AuthzRequestCache` class comment for the snapshot memo: which snapshots it
  keeps, when a taken one is reused, that a faulted read keeps nothing, and that `GetAssetsAsync`
  names the assets alone - so every gate that reaches its assets through it reads the `assets`
  table alone, and the one gate that does not is the scope write, which brings its own snapshot.
  Leave the accessible-set memo's documented keying as it stands.

## 3. Read endpoints draw one snapshot each (`fix(web)`)

- [x] 3.1 `src/Freeboard/Compliance/ComplianceEndpoints.cs`: `/organisations` takes
  `Assets`; `/scopes` takes `Assets | Scopes`; `/vendors` takes `Assets | VendorAssurances`;
  `/collectors` takes `Assets | Collectors`; `/integration-connections` takes
  `Assets | IntegrationConnections`. Each takes it from `AuthzRequestCache` and passes that
  snapshot's assets to `AccessibleAssetIdsAsync`. No handler makes a second store read for its
  asset list.
- [x] 3.2 Same file: `/standards`, `/requirements`, and `/controls` move onto
  `IComplianceStore.GetSnapshotAsync` directly with their own single set. They narrow nothing, so
  there is nothing for the request to share and no reason to memoize them.
- [x] 3.3 Same file: `/statement-of-applicability/{standardId}` narrows, so it takes
  `Assets | Scopes | Requirements` from `AuthzRequestCache`, like the pages - the request's later
  asset asks are then served from it rather than taking a second read. Its standards existence
  check stays a separate single-set snapshot read from `IComplianceStore` directly, because it is
  a catalog read that decides not-found rather than visibility.
- [x] 3.4 Update the endpoint comments that describe the read shape, and delete the ones the
  change makes untrue.

## 4. Pages and shell surfaces (`fix(web)`)

- [x] 4.1 `src/Freeboard/Pages/Compliance/Vendors.cshtml.cs`: one snapshot of
  `Assets | VendorAssurances | Scopes`. Delete the separate `GetScopesAsync` call. Keep the
  standards read separate and keep the comment saying why.
- [x] 4.2 `src/Freeboard/Pages/Compliance/Collectors.cshtml.cs`: one snapshot of
  `Assets | Collectors`, plus the separate controls read, which is an unnarrowed catalog read
  and stays outside for the reason the delta states.
- [x] 4.3 `src/Freeboard/Pages/Compliance/IntegrationConnections.cshtml.cs`: one snapshot of
  `Assets | IntegrationConnections`.
- [x] 4.4 `src/Freeboard/Pages/Compliance/StatementOfApplicability.cshtml.cs` and
  `ControlDetail.cshtml.cs`: take `Assets | Scopes | Requirements | Controls | Collectors`
  through `AuthzRequestCache` rather than through `IComplianceStore` directly, so the request's
  other surfaces can reuse the snapshot and so the page's accessible set is keyed to it.
- [x] 4.5 `src/Freeboard/Navigation/ShellNavResolver.cs` and `src/Freeboard/Web/OrgSelection.cs`:
  take `Assets | VendorAssurances` and `Assets` respectively through the cache.
- [x] 4.6 No ordering work: Razor Pages runs the page handler to completion before it executes
  the view, and the rail is a view component in the layout, so the register page's snapshot is
  always taken before the rail asks. The reuse test in section 7 asserts it rather than leaving
  it to be confirmed by hand.

## 5. Write and ingest paths (`fix(web)`)

- [x] 5.1 `src/Freeboard/Compliance/ComplianceWriteEndpoints.cs`: `StoredOrgSubjectAsync` takes
  one `Assets | Scopes` snapshot for the stored row and the subject asset, and returns a small
  record carrying BOTH the organisation it found and that snapshot.
- [x] 5.2 Same file: all four call sites pass that snapshot on to `OrganisationResource`, so the
  gate anchors on the same asset rows the row came from. `StoredScopeSelectorAsync` passes it directly; `UpsertScopeAsync` and
  `UpsertRequirementScopeAsync` pass it through `AuthorizeOrgAsync`, which gains a snapshot
  parameter and forwards it. A PUT handler left on the request's pinned assets-only read is the
  straddle unfixed. `AuthorizeOrgAsync` has one other caller: `AuthorizeParentAsync`, the
  organisation reparent's parent-side gate in `UpsertOrganisationAsync`. Its organisation comes
  from the route or the body, not from a stored row, so it passes no snapshot and keeps the
  assets-only path.
- [x] 5.3 Same file: every route- or body-anchored selector keeps reading the assets alone.
- [x] 5.4 `src/Freeboard/Evidence/EvidenceIngestEndpoints.cs`: replace the three reads with one
  snapshot of `Assets | Scopes | Requirements | Controls | Collectors`. Every check keeps its
  existing outcome and message.
- [x] 5.5 `src/Freeboard/Evidence/CollectorCredentialEndpoints.cs` and
  `src/Freeboard/Scheduler/CollectorSchedulerService.cs`: one snapshot of `Collectors`.
- [x] 5.6 `src/Freeboard/Program.cs`: the startup token-resolvability warning takes one snapshot
  of `Collectors | IntegrationConnections`.

## 6. The CLI vendor listing (`docs`)

- [x] 6.1 `src/Freeboard.CLI/VendorCommands.cs`: keep the two reads. Replace the comment above
  the join with the reason it is safe - each response is narrowed against its own server-side
  snapshot, and a scope whose vendor is absent from the vendor response is dropped, so a printed
  justification passed both narrowings and a mid-command sync costs freshness, not disclosure.
  Do NOT add a combined endpoint and do NOT widen `/vendors`.

## 7. Tests (`test`)

- [x] 7.1 Move all FOUR hand-written `IComplianceStore` doubles onto the one-method interface:
  `tests/Freeboard.Web.Tests/FakeComplianceStore.cs`, the standalone `CountingComplianceStore` in
  `OrgSelectionTests.cs`, and the two `FakeComplianceStore` subclasses - `CountingStore` in
  `AuthzRequestCacheTests.cs` and `CountingComplianceStore` in `ShellNavCatalogTests.cs`. Each
  counting double records the `ComplianceReadSet` of every snapshot it serves.
- [x] 7.2 Redesign `FakeComplianceStore`'s fault flags. `AssetsUnreachable` and
  `AssurancesUnreachable` each name a list of methods that no longer exist, and
  `CountingStore.FaultAssurances` overrides one of them. Replace all three with one
  `ComplianceReadSet` fault mask: a snapshot whose sets intersect the mask throws, so a test can
  still fault the assurance table alone (the unmigrated-schema shape) or the asset-bearing reads.
  `Unreachable` stays as it is - it faults every read.
- [x] 7.3 Update every remaining call site of the ten deleted methods across the test suite.
  About 120 sites in ten files, of which the 15 inside `FakeComplianceStore.cs` are the doubles'
  own implementations and are covered above: `MySqlIntegrationTests.cs` (~53),
  `AuthzRequestCacheTests.cs` (12), `OrgSelectionTests.cs` (10),
  `ScopeGeneralizationIntegrationTests.cs` (10), `AssetUnificationIntegrationTests.cs` (5),
  `IntegrationConnectionIntegrationTests.cs` (5), `CollectorMergeMigrationTests.cs` (3),
  `ShellNavCatalogTests.cs` (3), `VendorAssuranceIntegrationTests.cs` (2). Mechanical and
  compiler-checked, but it is the bulk of the diff, and most of it is in
  `Freeboard.Persistence.Tests` rather than the web tests.
- [x] 7.4 Add a structural test per narrowed surface: it takes exactly one snapshot, that
  snapshot names exactly the sets its decision needs, and the accessible set was resolved from
  that snapshot's asset list. Cover the SIX narrowed read endpoints (`/organisations`, `/scopes`,
  `/vendors`, `/collectors`, `/integration-connections`,
  `/statement-of-applicability/{standardId}`), the FIVE narrowing pages (`Vendors`, `Collectors`,
  `IntegrationConnections`, `StatementOfApplicability`, `ControlDetail`), the nav rail, the
  organisation selector, and the scope-write stored-owner lookup on all four of its call sites -
  both DELETE selectors and both PUT handlers.
- [x] 7.5 Add a snapshot-reuse test: a request that takes a wider snapshot first serves a later
  narrower request from it and makes no second store read, and the two decisions share one
  accessible set. Assert the register ordering directly - a render of `/compliance/vendors` takes
  the page's snapshot before the rail asks, so the rail makes no read of its own. Add the
  negative: two snapshots are never merged into a synthetic wider one.
- [x] 7.6 Add a test that reading an unrequested list throws
  `ComplianceReadSetNotRequestedException` and that the read endpoints do NOT convert it into a
  503.
- [x] 7.7 Add a structural test that a route- or body-anchored organisation gate and a
  role-assignment guard name `ComplianceReadSet.Assets` and no payload set, so those gate paths
  keep reading the `assets` table alone. Add the counterpart for the stored-row gate: a
  `DELETE /scopes/{id}` on a request that reads nothing else names exactly `Assets | Scopes` and
  no other payload set.
- [x] 7.8 Add a `tests/Freeboard.CLI.Tests/VendorCommandTests.cs` case pinning the join: a scopes
  response carrying a vendor-subject scope whose vendor is absent from the vendors response
  prints neither that vendor id nor that scope's justification.
- [x] 7.9 Add the interleaving `IDbConnectionFactory` decorator to
  `tests/Freeboard.TestInfrastructure`: it runs a supplied action on a separate connection before
  the Nth command executes on the decorated one. Deterministic, no sleeps.
- [x] 7.10 Add the `FREEBOARD_TEST_DB`-gated concurrency tests using that decorator: a read of
  `/scopes`, and a render of the vendor register, each racing a sync that reparents a vendor
  across the caller's accessible boundary. Assert the response is wholly from ONE side of the
  commit - the vendor, its scopes, and its `Out` justification together or not at all, never a
  justification without the readability. Do NOT assert the response is post-commit: a snapshot
  opened first legitimately answers pre-commit. Skip cleanly when the variable is unset.
- [x] 7.11 Add `tests/Freeboard.Persistence.Tests` coverage for the snapshot read itself: a
  multi-set snapshot runs in one `RepeatableRead` transaction, a single-set snapshot runs without
  one, and each set returns the same rows the deleted method returned. This is NEW coverage of
  the snapshot mechanism; the mechanical rewrite of that project's existing assertions onto
  `GetSnapshotAsync` is the task above.

## 8. Verification

- [x] 8.1 `dotnet format Freeboard.slnx --verify-no-changes`
- [x] 8.2 `dotnet build Freeboard.slnx --configuration Release -warnaserror`
- [x] 8.3 `dotnet test`
- [x] 8.4 `docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d`, then
  `export FREEBOARD_TEST_DB="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"`
  and `dotnet test`
- [x] 8.5 `export FREEBOARD_TEST_E2E=1` and `dotnet test tests/Freeboard.WebE2E`, after
  `dotnet build tests/Freeboard.WebE2E` and
  `pwsh tests/Freeboard.WebE2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`
- [x] 8.6 `dotnet run --project src/Freeboard.CLI -- gitops validate examples/fixture-corp`
  and the same for `examples/gitops`
- [x] 8.7 `npx markdownlint-cli2 "**/*.md"`
- [x] 8.8 `openspec validate "unify-compliance-read-snapshot" --strict`
