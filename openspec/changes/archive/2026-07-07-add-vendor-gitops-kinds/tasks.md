## 1. Config kinds: parse and model (feat(gitops): core model + loader)

- [x] 1.1 In `src/Freeboard.Core/GitOps/ConfigModel.cs` add `KindVendor` and `KindVendorScope` constants to `GitOpsSchema`; add a `sealed record Vendor` (`ApiVersion, Kind, Id, Title`); add a `sealed record VendorScope` (`ApiVersion, Kind, Id, Title, Vendor, Requirement, Control, Disposition, Justification`); add `List<Vendor> Vendors` and `List<VendorScope> VendorScopes` to `GitOpsConfig`.
- [x] 1.2 In `src/Freeboard.Core/GitOps/ConfigLoader.cs` add `SchemaKeys` entries for both kinds (Vendor keys: apiVersion, kind, id, title; VendorScope keys: apiVersion, kind, id, title, vendor, requirement, control, disposition, justification); add the `apiVersion` attribute override for each record; add the two `switch (kind)` cases; add both kinds to the unknown-kind diagnostic message list.

## 2. Config kinds: validation (feat(gitops): validation rules)

- [x] 2.1 In `src/Freeboard.Core/GitOps/ConfigValidator.cs` add `ValidateVendors` (required id/title, apiVersion, duplicate id) and call it in phase order; collect the vendor id set.
- [x] 2.2 Add `ValidateVendorScopes` called after vendors, requirements, and controls are validated: require id/title/vendor/disposition; require exactly one of `requirement`/`control` (error if both or neither); resolve `vendor` against vendor ids and the named target against requirement/control ids; parse disposition with `TryParseDisposition`; enforce duplicate id and one-per-pair uniqueness over `(vendor, requirement)` and `(vendor, control)`.
- [x] 2.3 Add the justification rule: when a VendorScope disposition parses as `Out` and `justification` is null/whitespace, emit a diagnostic naming the vendor-scope. `In` requires no justification.

## 3. Core tests (test(gitops): vendor parse and validate)

- [x] 3.1 Add loader/validator unit tests: valid Vendor and VendorScope parse into the model; unknown field rejected; missing required fields rejected; both-targets and neither-target rejected; unknown vendor/requirement/control reference rejected; duplicate id rejected; duplicate `(vendor, target)` pair rejected; bad disposition rejected; wrong apiVersion rejected.
- [x] 3.2 Add tests for the justification rule: `Out` without justification fails; `Out` with justification passes; `In` without justification passes.
- [x] 3.3 Add a valid multi-kind fixture including vendors and vendor-scopes to the existing gitops test fixtures.
- [x] 3.4 Run `dotnet test tests/Freeboard.Core.Tests` (or the relevant Core test project) - all green.

## 4. Persistence: schema and importer (feat(persistence): vendor tables and import)

- [x] 4.1 Add `src/Freeboard.Persistence/Migrations/011_vendors.sql` creating `vendors` and `vendor_scopes` per design D5 (utf8mb4_bin ids, DATETIME(6) timestamps, InnoDB, `IF NOT EXISTS`, `ON DELETE RESTRICT` FKs, nullable `requirement_id`/`control_id`/`justification`, `UNIQUE (vendor_id, requirement_id)` and `UNIQUE (vendor_id, control_id)`, FK indexes, and a `CHECK ((requirement_id IS NULL) <> (control_id IS NULL))` exactly-one-target backstop - MySQL 8.4 enforces CHECK; the Core validator stays the primary gate).
- [x] 4.2 In `src/Freeboard.Persistence/GitOps/ImportPlan.cs` expose vendors as `IReadOnlyList<DomainRow>` (reusing the existing `DomainRow(Id, ApiVersion, Title)`, as `controls` does - no vendor-specific row type) and add `VendorScopeRowPlan`, populated from the config (null blank justification via `NullIfBlank`), plus the vendor id lists.
- [x] 4.3 In `src/Freeboard.Persistence/GitOps/MySqlGitOpsImporter.cs` UPSERT `vendors` (insert/update only, no prune) in the FK-safe upsert phase, full-replace `vendor_scopes` in the delete-all+insert phase, and prune absent `vendors` ONLY in the reverse-FK phase after `vendor_scopes` are gone (e.g. a `DeleteAbsentAsync("vendors", ...)` call alongside the existing `controls`/`requirements`/`standards` prunes). Pruning a vendor in the upsert phase would be FK-unsafe: the prior `vendor_scopes` still reference it under `ON DELETE RESTRICT`, so the delete throws. Matches design D5.

## 5. Persistence: read store and counts (feat(persistence): vendor read model)

- [x] 5.1 In `src/Freeboard.Persistence/ComplianceReadModels.cs` add `VendorRow` and `VendorScopeRow`; extend `ComplianceCounts` with `Vendors` and `VendorScopes`.
- [x] 5.2 In `src/Freeboard.Persistence/IComplianceStore.cs` and `MySqlComplianceStore.cs` add `GetVendorsAsync` and `GetVendorScopesAsync`; include the two new counts in the counts read.
- [x] 5.3 Add importer round-trip and read-store tests gated on `FREEBOARD_TEST_DB` (skip cleanly when unset), asserting vendors and vendor-scopes persist and read back, including justification. Mirror the existing RequirementScope resync tests in `tests/Freeboard.Persistence.Tests/MySqlIntegrationTests.cs` (`ResyncRemovesOrganisationAndRequirementThatHadRequirementScope`, `ResyncRenamedRequirementScopeKeepingSamePairSurvives`, `ResyncSwappingRequirementScopePairsKeepingIdsSurvives`) with vendor equivalents that prove FK safety: removing a vendor still referenced by a vendor-scope; removing a requirement targeted by a vendor-scope; removing a control targeted by a vendor-scope; and a rename/pair-swap resync that keeps the `(vendor, target)` pair. Each must resync without hitting a RESTRICT or unique-key violation.

## 6. Web API read endpoints (feat(web): vendor read endpoints)

- [x] 6.1 In `src/Freeboard/Compliance/ComplianceEndpoints.cs` add `GET /api/v1/freeboard/vendors` and `GET /api/v1/freeboard/vendor-scopes` (GET-only, `RequireAuthorization`, payload keys matching the existing endpoints - all vendor fields are single-word incl. `justification`); each endpoint wraps its store read in the existing `try/catch (Exception ex) when (IsStoreFailure(ex))` and returns `Unreachable()` (HTTP 503) rather than throwing when the store is unavailable. Add `vendors` and `vendorScopes` (camelCase, matching the existing `requirementScopes`) to the `/compliance/status` `persisted` object in BOTH branches: the reachable branch (integer counts from `GetCountsAsync`) AND the store-unreachable catch branch (both new keys `null`, matching the other all-null keys).
- [x] 6.2 Add endpoint tests (WebApplicationFactory + compliance store double): both endpoints return the seeded rows; justification is present; anonymous request returns 401; GET still served in GitOps read-only mode; both new endpoints return 503 (not an unhandled exception) when the store is unreachable; status reports the new integer counts when reachable. Update the existing `tests/Freeboard.Web.Tests/ComplianceEndpointTests.cs` null-status assertion (the all-null `persisted` check, ~lines 338-344) to also assert `vendors` and `vendorScopes` are `JsonValueKind.Null`. Add a zero-org-access test proving the intentional non-narrowing (design D6, `specs/compliance-web-read` "Zero-grant caller under strict enforcement still reads every vendor"): use the `AuthWebFactory` seam that exposes `AuthzMode` and `Authz` - set `AuthzMode = "Enforce"` with an empty `FakeAuthzStore` (no grants), exactly as `ComplianceAuthzTests.SuperAdminSeesAllOrganisationsButReaderIsNarrowed` sets `Authz:Mode`/grants - then a member client (`AuthWebFactory.MakeUser("u1")`) requesting `GET /vendors` and `GET /vendor-scopes` still receives every seeded row. This is the case the per-org endpoints do NOT survive: `AuthzOrgAccess` returns an EMPTY accessible-org set for a zero-grant Enforce caller (`src/Freeboard/Authz/AuthzOrgAccess.cs` ~line 49), so `/organisations` narrows to nothing. Do NOT add org filtering to the vendor endpoints; the test pins the deliberate skip.

## 7. Web SSR vendor register page (feat(web): vendor register page)

- [x] 7.1 Add `src/Freeboard/Pages/Compliance/Vendors.cshtml` and `.cshtml.cs` (`@page "/compliance/vendors"`, PageModel injecting `IComplianceStore`, GET-only, store-unreachable notice) rendering each vendor with its scopes: target, disposition, and the justification for every `Out` exception.
- [x] 7.2 Add a nav link to the vendor register alongside the existing compliance links.
- [x] 7.3 Add page tests: renders vendors and their exceptions; every `Out` shows its justification; anonymous GET redirects to `/login`; served in read-only mode. Add a zero-org-access page test proving the page does not narrow (design D6, `specs/vendor-register` "Zero-grant caller under strict enforcement sees every vendor"): with the `AuthWebFactory` seam set to `AuthzMode = "Enforce"` and an empty `FakeAuthzStore` (no grants), an authenticated member GET of `/compliance/vendors` still renders every vendor and every `Out` justification - unlike the per-org pages, which narrow to the caller's empty accessible-org set under `AuthzOrgAccess`. Do NOT add org filtering. Add `/compliance/vendors` (with the required access level) to the parametrized `Pages` cases in `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs` so axe audits it. Note this E2E audit runs only under `FREEBOARD_TEST_E2E` with a launchable Chromium and skips cleanly otherwise; it does not run in a default `dotnet test`.

## 8. CLI vendor read command (feat(cli): vendor list via API)

- [x] 8.1 Add `ListVendorsAsync` (and the vendor-scopes read) to `src/Freeboard.CLI/IFreeboardApiClient.cs` with snake_case wire records; implement in `HttpFreeboardApiClient.cs` against the new routes.
- [x] 8.2 Extract the `private static` `Run` and `Translate` helpers out of `src/Freeboard.CLI/UserCommands.cs` into a new `internal static` helper class in `Freeboard.CLI` (e.g. `ApiCommandRunner`), and repoint `UserCommands` at it (no behaviour change; the 0/1/3 exit-code mapping is preserved and now defined in one place). Then add `src/Freeboard.CLI/VendorCommands.cs` (`vendor list`) modelled on `UserCommands.List`, calling the shared `Run`/`Translate`, reading over HTTP and printing each vendor with its exceptions and the justification for every `Out`; register the `vendor` group in `Program.cs`.
- [x] 8.3 Add CLI tests using the `ApiClientFactory.Create` fake seam: `vendor list` prints vendors and justifications; exit codes map correctly (0 ok, 1 validation, 3 operational). These tests mutate process-global state (the `ApiClientFactory` seam, `Console` capture, env vars), so the new vendor CLI test class MUST join the same xUnit collection as the existing user CLI tests (`[Collection("user-cli")]` in `tests/Freeboard.CLI.Tests/UserCommandTests.cs`) so it does not run in parallel with them. If the collection is renamed to a shared CLI collection, update both test classes to the new name.

## 9. Sync summary parity (chore(cli): count vendors in gitops summaries)

- [x] 9.1 In `src/Freeboard.CLI/GitOpsCommands.cs` add vendor and vendor-scope counts to `PrintSummary`, `PrintPlannedState`, and the `Sync` success line.

## 10. Verification (chore: build and test)

- [x] 10.1 Run `dotnet build` - solution builds clean.
- [x] 10.2 Run `dotnet test` - unit/web/CLI tests pass; DB-gated tests skip cleanly without `FREEBOARD_TEST_DB`.
- [x] 10.3 If a MySQL is available, run the DB-gated tests with `FREEBOARD_TEST_DB` set and confirm the migration applies and the round-trip passes.
- [x] 10.4 Run `npx markdownlint-cli2 "**/*.md"` for any touched Markdown and `openspec validate "add-vendor-gitops-kinds" --strict`.
