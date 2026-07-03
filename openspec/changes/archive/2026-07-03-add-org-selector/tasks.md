## 1. Subtree scoping helper (feat(web))

- [x] 1.1 Add `src/Freeboard/Compliance/OrgScope.cs`: pure
  `InScopeIds(IReadOnlyList<OrganisationRow> organisations, IReadOnlySet<string> accessibleIds, string? selectedId)`
  returning the in-scope id set, always intersected with `accessibleIds` - the
  accessible set when `selectedId` is null, otherwise the selected id plus all
  descendants intersected with `accessibleIds`, guarded by a visited-set so cyclic
  parent links terminate. Passing the accessible set in makes "All Organisations"
  mean every accessible org, not every persisted org.
- [x] 1.2 Add `tests/Freeboard.Web.Tests/OrgScopeTests.cs`: root selects whole
  subtree, leaf selects only itself, null selects the accessible set, unknown id
  yields empty, cyclic parent links terminate with a finite set, and a restricted
  `accessibleIds` excludes out-of-set organisations from both a null (All) selection
  and a selected subtree.

## 2. Accessible set and selection resolution (feat(web))

- [x] 2.1 Add `IOrgAccess` and a default implementation in `src/Freeboard/Web`. The
  seam is a pure function of the already-loaded organisation list -
  `IReadOnlySet<string> AccessibleOrgIds(ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)`
  - returning the accessible id subset with NO store read of its own; the v1 default
  returns every id from the supplied list. Keep the `user` parameter unused in v1 so
  a future per-organisation model can narrow by membership without a signature change.
- [x] 2.2 Add `src/Freeboard/Web/OrgSelection.cs` with the shared, request-state-free
  building blocks plus the resolver:
  - the `freeboard-org` cookie name and set/clear cookie helpers;
  - `static string? ReadCandidate(HttpContext)` returning the raw `freeboard-org`
    cookie value with NO store access (no I/O);
  - `static string? Resolve(string? cookieCandidate, IReadOnlySet<string> accessibleIds)`,
    a PURE helper (no I/O, no request state) that returns `cookieCandidate` when it is
    in `accessibleIds`, otherwise null (All Organisations). This is the single place
    the "validate the cookie candidate against the accessible set, else All" rule
    lives; both the resolver and org-scoped pages call it;
  - a request-scoped resolver that serves the layout selector ONLY: it loads the
    organisation list once (memoized), derives the accessible ids by passing that list
    to `IOrgAccess`, reads the candidate via `ReadCandidate`, and calls `Resolve` to
    expose the resolved selection to the view component. Memoize the organisation list
    and accessible set on first use so the selector's view component reads once per
    request; `IOrgAccess` adds no read of its own.
  Org-scoped pages consume the resolver for NOTHING: they read their own organisation
  list, derive their own accessible set via `IOrgAccess`, read the candidate via
  `ReadCandidate`, and call the same `Resolve` themselves (task 5.1), so a request that
  renders the layout selector and the Statement of Applicability page issues two
  organisation reads, not three, and the seam and the pure helpers add none. On a
  store-load failure the resolver MUST degrade to the null (All Organisations) resolved
  selection and an empty organisation list rather than throwing, so a store outage never
  faults the layout render. The resolver exposes no store-failure flag: the view
  component renders the same "All Organisations" entry for an empty store and an
  unreachable one, and the org-scoped page detects an outage through its own direct
  store reads (task 5.1).
- [x] 2.3 Register `IOrgAccess` and the request-scoped resolver in
  `src/Freeboard/Program.cs` (add `IHttpContextAccessor` if the resolver needs it).
- [x] 2.4 Add `tests/Freeboard.Web.Tests/OrgSelectionTests.cs`. Cover the pure
  `OrgSelection.Resolve` helper directly: a null candidate resolves to All, a candidate
  in the accessible set resolves to itself, and an unknown/inaccessible candidate is
  dropped to All (fail closed). Cover the resolver: absent cookie resolves to All,
  accessible id resolves to itself, unknown/inaccessible id is dropped to All, the
  accessible set bounds the result, repeated resolver reads hit the store once
  (memoization scoped to the selector's shared read), and a store-load failure degrades
  to All with an empty list instead of throwing.

## 3. Selection endpoint (feat(web))

- [x] 3.1 Add a GET selection endpoint `/org/select` (own endpoints file mapped in
  `Program.cs`) behind `RequireAuthorization(PageChallengeScheme.PolicyName)` (the
  `"PageAuthenticated"` page-challenge policy the `/compliance` folder uses), not a
  bare `RequireAuthorization()`. The bare form runs the process-wide bearer scheme
  and 401s an anonymous browser; the named page policy 302-redirects it to `/login`,
  which is what the spec scenario requires. The handler: set the cookie
  (`HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`) for a given `org`,
  clear it for All Organisations, then redirect to the `return` target validated via
  the existing `LocalRedirect.Sanitize` helper with an explicit app-page fallback
  (the Statement of Applicability page), not the `/account` default. The selector
  links pass the current `Request.Path` + `Request.QueryString` as the return
  target so query state (for example the SoA `?standard=`) survives a selection.
  Keep it GET-only so it works in read-only mode.
- [x] 3.2 Add `tests/Freeboard.Web.Tests/OrgSelectEndpointTests.cs`: selecting sets
  the cookie and redirects back and its `Set-Cookie` carries `HttpOnly`, `Secure`,
  `SameSite=Lax`, and `Path=/`; choosing All Organisations clears the cookie; a
  `?standard=` return target
  survives both selecting an org and choosing All Organisations, a non-local return
  redirects to the explicit fallback, an inaccessible org does not take effect, an
  anonymous request gets a 302 redirect to `/login` (not a 401) and sets no cookie,
  and the endpoint is served in read-only mode.

## 4. Menu selector view component (feat(web))

- [x] 4.1 Add the `OrgSelector` view component and `Default.cshtml` under
  `src/Freeboard/Pages/Shared/Components/OrgSelector/`: consume the request-scoped
  resolver (no direct store read of its own), build the tree from the accessible
  organisations only via `OrganisationRow.Parent`, render "All Organisations" plus
  the nested tree, mark the current selection, and link each entry to `/org/select`
  with `Request.Path` + `Request.QueryString` as `return`. Use nested `ul`/`li`
  list semantics with discernible link text, meet WCAG AAA contrast, and add no
  second `nav` landmark. Use Alpine only for branch expand/collapse; the tree
  renders collapsed by default (no ancestor pre-expansion in v1), with the current
  selection always rendered and marked even inside a collapsed branch. Render only
  the "All Organisations" entry (no tree) when there are no accessible organisations
  or the resolver degraded because the store was unreachable, so a store outage
  never throws into the layout.
- [x] 4.2 Invoke the component from `src/Freeboard/Pages/Shared/_Layout.cshtml` in
  the sidebar menu.
- [x] 4.3 Add rendering tests (`tests/Freeboard.Web.Tests`): tree reflects the
  hierarchy, the current selection is marked, only accessible organisations appear
  (a restricted fake `IOrgAccess` returning a subset of the supplied list, injected
  via the factory override in task 6.1, proves out-of-accessible-set orgs never render
  even under an "All" selection), and a layout page whose compliance store is
  unreachable still renders (HTTP 200, not 500) showing only the "All Organisations"
  entry.

## 5. Scope the Statement of Applicability page (feat(web))

- [x] 5.1 Update `StatementOfApplicability.cshtml.cs` to derive its ENTIRE scope from
  its own reads and consume the request-scoped resolver for NOTHING. Remove the
  resolver dependency from the page (inject `IOrgAccess`; keep `IComplianceStore`).
  Keep reading its standards, scopes, AND its organisation list directly from the
  compliance store inside its EXISTING try/catch that sets `StoreUnreachable` on a
  store failure. Derive the accessible set itself by passing the organisation list it
  read to `IOrgAccess`. Read the raw cookie candidate with no I/O via
  `OrgSelection.ReadCandidate(HttpContext)` and compute its own resolved selection with
  the pure `OrgSelection.Resolve(candidate, accessibleIds)`. Resolve the projection over
  the full organisation tree it read itself, then filter the node list to
  `OrgScope.InScopeIds(organisations, accessibleIds, resolvedSelection)` so the
  rendered nodes are bounded by the accessible set, inherited dispositions from
  ancestors above the selection are preserved, and out-of-scope nodes are absent from
  the model. Deriving the whole scope from the page's own reads is required on two
  counts: the resolver degrades an organisation-load failure to an empty list (a page
  that took its nodes or accessible set from the resolver would render a healthy but
  empty table on an organisations-only outage instead of the notice), and the resolver
  degrades that failure to an "All Organisations" resolved selection (a page that took
  the resolved selection from the resolver would silently drop the cookie-selected
  subtree to All whenever the resolver's own read failed). Reading and resolving the
  cookie itself against the page's own accessible set removes both couplings. The page
  reads nothing from the resolver and no resolver failure flag.
- [x] 5.2 Update `StatementOfApplicability.cshtml` to show the active scope (which
  organisation or "All Organisations") above the table; no change to the row
  markup beyond the filtered node list.
- [x] 5.3 Extend `tests/Freeboard.Web.Tests/StatementOfApplicabilityPageTests.cs`:
  a selected organisation renders only its subtree, a selected department still
  shows the disposition inherited from a company above it, "All Organisations"
  renders every node, out-of-scope ids are absent from the response body, a
  restricted fake `IOrgAccess` (injected via the factory override in task 6.1) keeps
  out-of-accessible-set organisations absent even under "All Organisations", and the
  `?standard=` query survives a selection made from the page. Active-scope label: with
  an organisation selected the rendered page names that organisation's title as the
  active scope above the table, and with no selection it names "All Organisations".
  Store-unreachable
  notice, two cases: (a) when all reads fail - via a fake store that throws on every
  read - the page renders its store-unreachable notice (not an empty table), driven by
  its own direct reads throwing; (b) when ONLY the organisation load fails while
  `GetStandardsAsync` and `GetScopesAsync` SUCCEED - via the per-method failure flag
  on the fake store (task 6.2) that throws from `GetOrganisationsAsync` only - the
  page still renders the store-unreachable notice, not a healthy empty table, proving
  it reads its organisation list directly and does not take the resolver's degraded
  empty list.
- [x] 5.4 Add a structural regression guard (test-only, no production change) in
  `tests/Freeboard.Web.Tests/StatementOfApplicabilityPageTests.cs`: a reflection
  assertion that `StatementOfApplicabilityModel`'s public constructor parameter types
  are exactly `IComplianceStore` and `IOrgAccess` (in any order) and do not include the
  request-scoped selection resolver type. This catches the real regression - re-adding
  the resolver dependency to the page - without adding any production seam, since the
  page derives its whole scope from its own reads (task 5.1) and consumes the resolver
  for nothing.

## 6. Test support (test-only)

Test-only additions to the existing web-test infrastructure that tasks 4.3 and 5.3
depend on. No production code.

- [x] 6.1 Add an `IOrgAccess` override to `AuthWebFactory`
  (`tests/Freeboard.Web.Tests/AuthWebFactory.cs`). It exposes a `Compliance` store
  override today but no `IOrgAccess` seam, so tasks 4.3 and 5.3 cannot inject a
  restricted fake. Add an optional `IOrgAccess` property that, when set, replaces the
  default registration in `ConfigureTestServices`, so a test can supply a restricted
  fake returning a strict subset of the supplied organisation list.
- [x] 6.2 Add per-method failure control to `FakeComplianceStore`
  (`tests/Freeboard.Web.Tests/FakeComplianceStore.cs`). It has only a global
  `Unreachable` that throws on every read; task 5.3b needs `GetOrganisationsAsync` to
  throw while `GetStandardsAsync` and `GetScopesAsync` succeed. Add an opt-in
  per-method failure flag (for example `OrganisationsUnreachable`) that throws only
  from `GetOrganisationsAsync`, leaving the other reads working.

## 7. Verification

- [x] 7.1 `dotnet build` (asset build runs; bun on PATH).
- [x] 7.2 `dotnet test` - all unit and web tests green with no MySQL (fakes and
  `WebApplicationFactory`).
- [x] 7.3 Confirm `Freeboard.Architecture.Tests` still pass: no new reference from
  Agent/CLI/Core to `Freeboard.Enterprise`; no new EE code added.
- [x] 7.4 Add and run a browser E2E test in `tests/Freeboard.WebE2E`
  (`FREEBOARD_TEST_E2E` gated): select an organisation, the Statement of
  Applicability filters to its subtree, "All Organisations" shows everything, and
  the selection persists across a navigation.
- [x] 7.5 Extend the existing axe-core E2E audit (`AccessibilityAuditE2ETests`) so
  the layout-with-selector passes at zero violations under every supported standard
  including WCAG AAA, and confirm the already-audited account, MFA, and admin pages
  (which now carry the selector) still pass: discernible link text, correct
  list/tree semantics, AAA contrast, no duplicate nav landmark, and expand/collapse
  toggle controls with a discernible accessible name and an `aria-expanded` state.
  The existing audit seeds only a session and no organisations, so the tree, the
  selection marker, and the toggle controls never render. Seed at least one audited
  layout-carrying page with a multi-node organisation tree
  (`App.Compliance.Organisations`, as `StatementOfApplicabilityE2ETests` does) AND a
  current selection set to a TOP-LEVEL node (the `freeboard-org` cookie added to the
  browser context beside the session cookie), so the nested tree markup, the
  selection marker, and the toggle controls exist in the DOM. Because the tree renders
  collapsed by default and axe-core skips elements that are not visible, seeding alone
  leaves the nested nodes hidden, so axe would not actually audit them and the presence
  guard would pass while they are invisible. Therefore, before calling `RunAxe`, EXPAND
  at least one branch by activating its toggle so at least one nested node becomes
  visible; the top-level selection keeps the selection marker visible without needing
  expansion. Then assert the seeded selector actually rendered AND is visible to the
  audit: at least one nested tree node is VISIBLE and at least one expand/collapse
  toggle control is present in the DOM on the seeded page (as the existing audit
  already asserts its expected page path before auditing). This stops a selector that
  silently fails to render - or that hides its whole tree while collapsed - from
  trivially passing the audit. Keep it within the existing `FREEBOARD_TEST_E2E`
  gating.
- [x] 7.6 `openspec validate "add-org-selector"` passes.
