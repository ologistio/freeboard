## 1. The accessible-set memo (`fix(authz)`)

- [x] 1.1 In `src/Freeboard/Authz/AuthzRequestCache.cs`, change the accessible-set memo key from
  the principal to the principal AND the asset list instance:
  `AccessibleAssetIdsAsync(string principalKey, IReadOnlyList<AssetNode> assets,
  Func<ValueTask<IReadOnlySet<string>>> resolve)`.
- [x] 1.2 Pin reference identity explicitly rather than relying on the default comparer: key the
  list half of the pair through `ReferenceEqualityComparer.Instance`, or through a key wrapper that
  compares by `ReferenceEquals` and hashes by `RuntimeHelpers.GetHashCode`. The default comparer is
  reference equality only while no list implementation overrides `Equals`, which is not this seam's
  to guarantee.
- [x] 1.3 Update `src/Freeboard/Authz/AuthzAssetAccess.cs` to pass the caller's asset list through
  to the memo. `IAssetAccess.AccessibleAssetIdsAsync` keeps its signature and its unfiltered-list
  contract.

## 2. The shared asset read (`fix(authz)`)

- [x] 2.1 Point `AuthzRequestCache.GetAssetsAsync` at `IComplianceStore.GetAssetsAsync` and
  memoize the result, so an organisation gate reads the `assets` table and no payload table. The
  memo pins the request's shared asset list. The first list `GetAssetsAsync` serves is the list it
  serves for the rest of the request.
- [x] 2.2 Give `GetAssetsAsync` opportunistic reuse: when the shared memo is still empty and the
  assurance snapshot memo is already populated, serve that snapshot's asset list and take no read.
  Reuse POPULATES the shared memo rather than being re-evaluated per call, so a caller that arrives
  after an assurance snapshot lands is still served the pinned list. One request therefore never
  serves two different lists from `GetAssetsAsync`, and the ancestry map built from the shared read
  cannot disagree with what a later caller is served. `GetAssetsAsync` MUST NOT trigger the
  assurance read. A faulted assurance read populates nothing, so a gate is still served the
  assets-only read.
- [x] 2.3 Keep `GetVendorAssuranceInputsAsync` on the cache as its own memoized read, taken at
  most once per request.
- [x] 2.4 Confirm the three assurance surfaces still take that read and narrow with ITS asset
  list: `src/Freeboard/Compliance/ComplianceEndpoints.cs` (`/vendors`),
  `src/Freeboard/Pages/Compliance/Vendors.cshtml.cs`, and
  `src/Freeboard/Navigation/ShellNavResolver.cs`.
- [x] 2.5 Confirm every other consumer of the cache now narrows with the list `GetAssetsAsync`
  served it, and that no call site outside those three names the assurance read.
- [x] 2.6 Rewrite the `AuthzRequestCache` class comment: what it memoizes, that the memo is keyed
  by the asset list, that the shared asset read carries no payload table, and that it may be served
  from an assurance snapshot the request already took but never takes one. Delete the paragraph
  saying the request's asset read IS the assurance snapshot and the paragraph naming the two
  Statement of Applicability pages as an exception, which the memo key removes. Rewrite the
  `GetVendorAssuranceInputsAsync` doc comment, which gives sharing the correctness role.
- [x] 2.7 Correct the other comments the change falsifies:
  - `src/Freeboard/Navigation/ShellNavResolver.cs` class comment - it says the badge reads the
    request's snapshot because the accessible set is resolved once per request from whichever list
    reaches the seam first. The badge still takes that snapshot, but for the read count, not to
    avoid another surface's owner edges.
  - `src/Freeboard/Web/AssetAccess.cs`, the `AccessibleAssetIdsAsync` doc comment - the
    unfiltered-list contract survives, its stated reason does not. The reason is that the seam
    closes an organisation union over `parent` and `owner` edges, so a narrowed list breaks the
    closure by hiding the ancestors and owners it walks.
  - `src/Freeboard/Authz/AuthzAssetAccess.cs` class comment - "at most once per principal per
    request" becomes per principal per asset list, and the audit row count follows the resolutions
    rather than the request.
  - `src/Freeboard/Web/OrgSelection.cs`, the `OrgSelectionResolver` comment - it takes the asset
    list from the cache, which is no longer "the request's one snapshot".
  - `src/Freeboard/Pages/Compliance/Vendors.cshtml.cs`, the `VendorsModel` class comment, and
    `src/Freeboard/Compliance/ComplianceEndpoints.cs`, the comment above the `/vendors` handler -
    both call the assurance read "the request's one snapshot". A request may now take two reads, so
    it is one snapshot of two. Both surfaces still take it and still narrow with its asset list.

## 3. Tests (`test`)

- [x] 3.1 Extend `AccessibleSetResolvesOncePerRequest` in
  `tests/Freeboard.Web.Tests/AuthorizerTests.cs` rather than adding a second memo test beside it.
  It already pins one list resolving once, per request, with the audit row as the observable. Add
  the new half - two DISTINCT list instances for one principal in one request resolve two sets and
  neither is served the other's answer - and rename it and its comment to the per-asset-list rule.
- [x] 3.2 Make an assurance-only failure expressible in
  `tests/Freeboard.Web.Tests/FakeComplianceStore.cs`. `AssetsUnreachable` faults `GetAssetsAsync`
  and `GetVendorAssuranceInputsAsync` together, so a gate-path test written against it passes
  whatever the code does. Add a flag that faults the assurance read alone, or override the already
  `virtual` `GetVendorAssuranceInputsAsync` in a test-local subclass, as `ShellNavCatalogTests`
  does. Make the double hand out a distinct list instance per read, so two reads do not collapse
  into one memo key.
- [x] 3.3 Add the gate-path test on that double: an unreadable assurance table still answers a
  gated compliance write and still renders the role-assignment page.
- [x] 3.4 Keep `GatedWriteAnswers403WhenTheAssetSnapshotReadFails` in
  `tests/Freeboard.Web.Tests/ComplianceWriteEndpointTests.cs`. An unreadable `assets` table must
  still answer 403, which is the coverage the name and comment now misdescribe: rename it to the
  `assets` read it now pins and rewrite the comment, which says the gate resolves through the
  asset-and-assurance snapshot.
- [x] 3.5 Correct `TheRailAndAnEarlierReaderShareOneSnapshot` in
  `tests/Freeboard.Web.Tests/ShellNavCatalogTests.cs`. The assertion still holds, its stated reason
  does not: sharing keeps the read count down, and the memo key is what stops either surface
  narrowing with the other's owner edges.
- [x] 3.6 Keep the assurance-surface coverage green - `VendorsPageTests`, `ShellNavCatalogTests`,
  and the `/vendors` cases in `ComplianceEndpointTests` - and assert each narrows with the asset
  list of the assurance read.
- [x] 3.7 Pin the two properties the memo key rests on. The memo compares lists by reference rather
  than by contents, so two equal but distinct lists resolve twice. A list mutated after the seam
  resolved from it is still served the memoized set, which is the observable behind the seam's
  stated requirement that a caller does not mutate a list it has handed over.

- [x] 3.8 Correct `Resolver_RepeatedReads_HitStoreOnce` in
  `tests/Freeboard.Web.Tests/OrgSelectionTests.cs`, which this change turns red. Its local
  `CountingComplianceStore` counts `GetVendorAssuranceInputsAsync` only, so once the resolver's
  assets come from `GetAssetsAsync` the count is zero and `Assert.Equal(1, store.SnapshotReads)`
  fails. Count the assets read as well (or move the counter onto it), rename the counter to the
  read it now pins, and correct the two comments that call the resolver's source the request's one
  asset-and-assurance snapshot: the counter's doc comment and the comment in the `Resolver` helper.
- [x] 3.9 Correct the constructor-shape comments in
  `tests/Freeboard.Web.Tests/CollectorsPageTests.cs` and
  `tests/Freeboard.Web.Tests/IntegrationConnectionsTests.cs`. Both say the assets come from the
  request's one snapshot on the cache. Both pages now take the shared assets-only read.

## 4. Documentation (`docs`)

- [x] 4.1 State the relaxed deploy-order consequence in the "Migrate first, then sync" section of
  `docs/gitops.md`: an unmigrated `vendor_assurances` table degrades the nav badge and `/vendors`
  rather than failing every gated compliance write with 403. The `README.md` quickstart carries the
  command order only, so it needs no change.

## 5. Verification

- [x] 5.1 `dotnet format Freeboard.slnx --verify-no-changes`
- [x] 5.2 `dotnet build Freeboard.slnx --configuration Release -warnaserror`
- [x] 5.3 `dotnet test`
- [x] 5.4 `docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d`, then
  `export FREEBOARD_TEST_DB="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"`
  and `dotnet test`
- [x] 5.5 `export FREEBOARD_TEST_E2E=1` and `dotnet test tests/Freeboard.WebE2E`, after
  `dotnet build tests/Freeboard.WebE2E` and
  `pwsh tests/Freeboard.WebE2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`
- [x] 5.6 `dotnet run --project src/Freeboard.CLI -- gitops validate --path examples/fixture-corp`
  and the same for `examples/gitops`
- [x] 5.7 `npx markdownlint-cli2 "**/*.md"`
- [x] 5.8 `openspec validate "narrow-authz-gate-to-assets" --strict`
