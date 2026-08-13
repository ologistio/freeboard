## Why

The request-scoped authorization cache serves its asset list out of a read that also pulls
`vendor_assurances`. That cache is where every organisation gate, every compliance write
selector, and both role-assignment guards get their assets. A feature table is therefore an
input to decisions that have nothing to do with vendor assurance. An app that runs against a
schema without that table answers every gated compliance write with 403 and faults the
role-assignment page with 500.

The pairing exists for a good reason. The vendor register narrows assurance rows by the
vendors' `owner` edges, so the assurances and the assets have to come from one snapshot. One
mechanism stood in the way of giving each surface its own snapshot: the accessible-asset-set
memo is keyed by the principal alone, so the FIRST asset list to reach the authorization seam
decides the set every later surface is served. Two surfaces each taking their own honest
snapshot did not each narrow with their own owner edges. The second narrowed with the first's.
Given that memo, one snapshot for the whole request was the only shape that narrowed honestly,
and the shared asset read had to be that snapshot.

Key the memo by the asset list as well as the principal and the constraint disappears. Several
snapshots can then coexist in one request, each decision narrowing by its own rows. The reason
to make one snapshot serve everything is gone, and only its cost remains.

## What Changes

- The accessible-asset-set memo is keyed by the principal AND the asset list it was resolved
  from, instead of by the principal alone. Two decisions on two asset lists each get a set
  resolved from their own rows. `IAssetAccess.AccessibleAssetIdsAsync` keeps its signature and
  its unfiltered-list contract.
- `AuthzRequestCache.GetAssetsAsync` returns to reading the `assets` table alone. Organisation
  gates, the compliance write selectors, and both role-assignment guards stop reading
  `vendor_assurances`. It serves the assets from the assurance snapshot when the request has
  ALREADY taken one, which holds an ordinary page render at today's read count without ever
  causing that read.
- The assurance snapshot stays as its own request-scoped read, taken at most once and shared by
  the surfaces that narrow assurances by owner edges: the vendor register page, `GET /vendors`,
  and the rail badge. Those surfaces still pair the assets and the assurances in one snapshot.
  That guarantee is load-bearing and survives unchanged.
- The ratified text that says the request's shared asset read is served from the assurance
  snapshot, and that the accessible set is resolved once per principal per request, is corrected
  to match.
- The failure mode inverts. An absent `vendor_assurances` table degrades the nav badge to
  unbadged and `/vendors` to its 503 response, and leaves every gated compliance write and the
  role-assignment page working.

## Capabilities

### New Capabilities

None. This change re-scopes a pairing the compliance capabilities already own.

### Modified Capabilities

- `authz-enforcement`: the accessible set is memoized per principal per asset list, and the
  shared asset read that every organisation gate draws on reads the `assets` table alone.
- `org-scope-selection`: the seam resolves the accessible set once per principal per asset list
  per request, not once per principal per request.
- `vendor-register`: the assurance snapshot serves the register, the endpoint, and the badge. It
  no longer serves the request's shared asset read.

## Impact

MIT. No code lands in `src/Freeboard.Enterprise`, and nothing outside it gains an EE reference.
The runtime change is confined to `src/Freeboard`.

- `src/Freeboard`, behaviour: `Authz/AuthzRequestCache.cs` and `Authz/AuthzAssetAccess.cs`.
- `src/Freeboard`, comments the change falsifies: `Navigation/ShellNavResolver.cs`,
  `Web/AssetAccess.cs`, `Web/OrgSelection.cs`, `Pages/Compliance/Vendors.cshtml.cs`, and
  `Compliance/ComplianceEndpoints.cs`.
- Tests: the memo test extended, a gate-path test on a double that faults the assurance read
  alone, the test-double change that makes such a fault expressible, corrected names and comments
  on the tests whose stated mechanism changes, and the existing assurance-surface coverage kept
  green.
- Docs: a deploy-order note added to the "Migrate first, then sync" section of `docs/gitops.md`.
  That section states the command order only and never stated the 403 consequence, so the note is
  new operator guidance rather than a correction of falsified text. The README quickstart carries
  the command order only and needs no change.
- No store API change, no database migration, no configuration key, no new dependency.

## Non-goals

- Changing the `IComplianceStore` read API. This change moves which read the cache serves its
  assets from, not what the store offers.
- Closing the vendor register's separate scope read. That straddle is already stated as open in
  the persistence capability and is not closed here.
- Changing any endpoint's JSON, any page's markup, or any narrowing rule. What a caller may read
  is unchanged.
- Any change to the GitOps writer, the importer transaction, or the migration runner.
