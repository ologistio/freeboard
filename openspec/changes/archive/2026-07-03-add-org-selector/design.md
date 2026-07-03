## Context

Organisations already form a recursive tree. `Freeboard.Core` models an
`Organisation` with an id, title, kind (`Company` / `Department`), and an optional
`Parent` id (null for a root). Persistence stores it in the `organisations` table
with a self-referencing `parent_id` foreign key and exposes it to the web app as
`OrganisationRow(Id, Title, Kind, Parent)` through `IComplianceStore`. There is no
schema work to do; the selector renders and scopes the existing tree.

Current state of scoping in the web app:

- The only org-scoped view is the Statement of Applicability page
  (`/compliance/statement-of-applicability`). It loads the full organisation list
  and scope list through `IComplianceStore`, computes a projection with the pure
  `StatementOfApplicability.Resolve(...)`, and renders every node.
- The app shell menu lives in `Pages/Shared/_Layout.cshtml`: a left sidebar with a
  Home link and a Compliance section, and a top-bar account dropdown. The layout
  reads user claims directly; it has no injected services today.
- Authorization is coarse: a global `admin` / `member` role. Every authenticated
  user may read the whole compliance domain. There is no per-user, per-organisation
  membership model.
- Session and auth state ride the `__Host-freeboard-session` cookie; view
  preferences do not have a cookie yet.

Constraint recap: MIT by default, no code in `Freeboard.Enterprise`; the web app
(`Freeboard`) may reference `Freeboard.Core` and `Freeboard.Persistence`; Agent and
CLI stay EE-free and are untouched by this change. ASCII punctuation and
code-as-liability apply.

## Synthesis of the two source plans

This design merges two independently authored plans (referred to below as the
web-layer plan and the persistence-layer plan). They agreed on the substance:
reuse the existing recursive organisation tree with no migration; enforce the
subtree filter server-side rather than in client code; for the Statement of
Applicability, resolve inheritance over the full tree first and filter the node
list to the selected subtree afterwards; introduce a single accessibility seam
whose v1 implementation grants access to all organisations; fail closed on an
unknown or inaccessible selection; and verify with the same unit / web / MySQL /
E2E test tiers the repo already uses.

They diverged on three points. Each is resolved below and reflected in the
Decisions section.

1. Selected-state storage. The web-layer plan carried the selection in a cookie
   (`freeboard-org`) so it persists across navigation without threading a
   parameter onto every link. The persistence-layer plan carried it in the URL
   (`?org=`) for shareable, per-request, API-native, stateless selection.
   Resolved: the cookie is the primary mechanism for the menu-driven page
   experience, because the stated requirement is persistence across navigation
   and reload, which a query parameter cannot satisfy without being added to
   every link and lost on a plain reload of a different page. The concerns the
   query-parameter approach raised are addressed under the cookie decision below,
   and a `?org=` per-request override is left as a documented, unbuilt seam.

2. API scope surface. The persistence-layer plan made the JSON compliance
   endpoints (`/organisations`, `/scopes`, `/statement-of-applicability`)
   org-aware in v1, arguing that a "server-side, not UI-only" claim is undercut
   if those endpoints still return the full domain. The web-layer plan left them
   full-domain and listed API scoping as an open question. Resolved: leave the
   JSON endpoints full-domain in v1. The reasoning turns on what the scoping
   actually is (see the enforcement decision): in v1 the accessible set is every
   organisation, so scoping is a rendered view filter a user applies to their own
   screen, not an authorization boundary. Nothing is withheld from the user that
   the full-domain endpoints or the "All Organisations" button would not also
   show, so the endpoints leak nothing. Adding org-awareness to three endpoints
   for zero authorization benefit would be speculative surface. When the
   accessibility seam first narrows below all-access, the JSON endpoints MUST
   adopt the same seam in that same change, otherwise they would then leak; this
   is recorded as an Open Question and a Non-Goal, not forgotten.

3. Where the subtree filter lives. The web-layer plan kept a pure in-memory
   helper (`OrgScope.InScopeIds`) in the web app and touched neither
   `Freeboard.Persistence` nor `Freeboard.Core`. The persistence-layer plan
   pushed the filter into `IComplianceStore` / `MySqlComplianceStore` as a
   recursive CTE over `organisations(parent_id)`. Resolved: keep the filter in
   the web layer. The tree is small (a handful of nodes per instance) and is
   already loaded in memory for the projection, so a pure helper is trivial and
   directly unit-testable against the existing fakes. Crucially, the one
   org-scoped view (Statement of Applicability) must resolve inheritance over the
   full tree before filtering, so a pre-filtering SQL CTE cannot serve that read
   without breaking inheritance - the SQL push would help only the JSON endpoints,
   which decision 2 keeps full-domain. Pushing the filter into the store would
   also change a shared persistence signature and the in-memory fake for no v1
   benefit. The tradeoff accepted: this in-memory approach does not scale to very
   large trees and does not enforce at the store. If a future access model or
   scale demands store-level enforcement, the filter can move into a recursive
   CTE then, with the caveat that the inheritance read must still fetch the full
   tree.

## Goals / Non-Goals

**Goals:**

- A menu organisation selector rendering the accessible organisation tree plus an
  "All Organisations" entry.
- A current selection that persists across navigation and reloads.
- Org-scoped views filter to the selected organisation and its descendants, with
  the filter enforced server-side.
- "All Organisations" applies no subtree filter, bounded by the accessible set.
- A single accessibility seam so selection and scoping fail closed and a future
  access model has one place to narrow.

**Non-Goals:**

- Per-user, per-organisation access control (RBAC / membership). The seam is added;
  its v1 implementation grants access to all organisations.
- Scoping the JSON compliance read endpoints (`/organisations`, `/scopes`,
  `/statement-of-applicability/{standardId}`). They stay full-domain machine reads
  in v1.
- New org-scoped views beyond the existing Statement of Applicability page.
- Any schema migration or change to `Freeboard.Core` / `Freeboard.Persistence`.
- Multi-select or selecting several subtrees at once.

## Decisions

### Data model: reuse the existing tree, add nothing

Organisations are already a recursive tree in Core and persistence. No new model,
no migration. The selector reads `IComplianceStore.GetOrganisationsAsync` and
builds the tree from `OrganisationRow.Parent` (a node is a root when its `Parent`
is null; a child hangs under the node whose id equals its `Parent`). Alternative considered: a dedicated
denormalised tree table or a materialised path column for fast subtree queries.
Rejected: the tree is small (a handful of nodes per instance), the list is already
loaded for the projection, and in-memory subtree computation is trivial and
testable. Adding schema would be speculative cost.

### Selected state lives in a cookie, resolved server-side each request

The selection is carried in a small cookie (`freeboard-org`), set when the user
chooses an entry and read on every subsequent request. A cookie (not a URL query
parameter) is chosen because the requirement is persistence across navigation
between different views; a query parameter would have to be threaded onto every
link and lost on a plain reload of a different page. A server-side session store was
rejected as heavier than needed for a view preference.

The trade-offs a query parameter would have offered, and how they are handled:

- Shareability. A cookie selection is not shareable by URL. This is acceptable in
  v1 because scoping is a personal view preference, not a shareable data view; the
  full-domain JSON endpoints remain the shareable, API-native surface. If a
  shareable scoped page link later becomes a requirement, a `?org=` query
  parameter can override the cookie for that one request without changing the
  resolver's contract - the resolver would read query first, then cookie. That
  override is a documented but unbuilt seam here, not v1 code (code-as-liability).
- Testability. The resolver is a small request-scoped service tested directly, and
  web tests set the cookie on the test client. This is no harder than asserting a
  query parameter, so testability is not a reason to prefer the URL.

The cookie is NOT `__Host-` prefixed and is not a security token: it is a view
preference that is re-validated server-side on every request. It is `HttpOnly`
(only the server reads it), `Secure`, `SameSite=Lax`, `Path=/`. Even a forged or
stale cookie cannot widen what the user sees, because resolution intersects the
candidate with the accessible set (see below).

A request-scoped resolver (`OrgSelection` resolver) serves the layout selector and
nothing else. It loads the organisation list once (memoized), derives the accessible
ids by passing that list to `IOrgAccess`, reads the raw cookie candidate, and calls
the pure `OrgSelection.Resolve` helper (see the next decision) to produce the
resolved selection: the candidate id when it is in the accessible set, otherwise
"All Organisations". The `OrgSelector` view component consumes its memoized
organisation list, accessible set, and resolved selection, so the layout resolves
and reads once per request. `IOrgAccess` adds no read of its own - it is a pure
function of the list the resolver already loaded. Being request-scoped, the memo is
per-request and never shares data across requests or users.

Org-scoped pages consume the resolver for NOTHING. They derive their entire scope
from their own reads: a page reads its own organisation list directly from the store
(see the store-failure decision below), derives its own accessible set from that list
via `IOrgAccess`, reads the same raw cookie candidate with no I/O, and calls the same
pure `OrgSelection.Resolve` helper to get its own resolved selection. So a page and
the resolver share the pure `OrgSelection.Resolve` helper, the cookie-read helper,
and the org-scope helpers, but no request state: a page never reads the resolver's
organisation list, accessible set, or resolved selection. This matters because the
resolver degrades an organisation-load failure to an empty list and an "All
Organisations" resolved selection; a page that took the resolved selection from the
resolver would silently drop to "All Organisations" - losing the cookie-selected
subtree - whenever the resolver's own read failed even though the page's own read
succeeded. Deriving the selection from the page's own reads removes that coupling
entirely.

The net effect keeps reads bounded: the resolver issues one `GetOrganisationsAsync`
read for the layout selector, and each org-scoped page issues one direct read of its
own - two organisation reads for a request that renders the layout selector and the
Statement of Applicability page, not three, and the seam and the pure helpers add
none.

### Selection resolution is a pure helper over a candidate and the accessible set

The rule "validate the cookie candidate against the accessible set, else All
Organisations" lives in one pure function:
`string? OrgSelection.Resolve(string? cookieCandidate, IReadOnlySet<string> accessibleIds)`.
It returns `cookieCandidate` when that id is in `accessibleIds`, otherwise null
("All Organisations"). It does no I/O and reads no request state, mirroring the
existing pure `StatementOfApplicability.Resolve` and `OrgScope.InScopeIds`, so it is
unit tested directly and both the resolver and every org-scoped page share the exact
same fail-closed rule with no duplicated logic.

The raw cookie candidate is read with no I/O by a tiny helper that returns the
`freeboard-org` cookie value from the request cookies, `string?
OrgSelection.ReadCandidate(HttpContext)`. It touches no store. Sharing this one
reader keeps the two consumers (the resolver and an org-scoped page) reading the same
cookie the same way without either taking the value from the other. Both helpers are
static members of `OrgSelection` alongside the cookie name and the set/clear cookie
helpers; they add no service and no dependency.

### "All Organisations" is the absence of a selection

"All Organisations" is represented by no cookie (or an empty value), i.e. a null
resolved selection. Queries branch on it: null selection means the in-scope set is
the full accessible set; a non-null selection means the in-scope set is that node
plus its descendants. Choosing "All Organisations" clears the cookie. A sentinel
string was rejected in favour of plain absence, which needs no reserved id and no
collision rule.

### Selection endpoint is a GET that sets the cookie and redirects back

Choosing an entry hits `GET /org/select?org=<id>&return=<target>` (and an
`org`-absent form for "All Organisations"). It sets or clears the cookie and
redirects to the return target, validated as a local path with the existing
`LocalRedirect` helper. The return target is the current `Request.Path` plus
`Request.QueryString`, not the path alone: an org-scoped page can carry state in
its query string (the Statement of Applicability page carries the active standard
in `?standard=`), and a path-only return would silently reset that state on every
selection. `IsLocal` accepts a rooted path with a query string, so a
`/compliance/statement-of-applicability?standard=...` target round-trips. When the
supplied target is non-local, `LocalRedirect.Sanitize` is called with an explicit
sensible app-page fallback (the Statement of Applicability page), not the
`/account` default the string overload assumes, because `/account` is the wrong
landing for a selector action.

The endpoint requires an authenticated user through the named page-challenge
policy `PageChallengeScheme.PolicyName` (value `"PageAuthenticated"`) - the same
policy the `/compliance` folder uses via `AuthorizeFolder`. It is mapped with
`RequireAuthorization(PageChallengeScheme.PolicyName)`, not a bare
`RequireAuthorization()`. This matters: the process-wide default scheme is the
bearer scheme, so a bare `RequireAuthorization()` would answer an anonymous
browser with a 401, but the requirement (and the spec scenario) is a 302 redirect
to `/login`. Only the page-challenge scheme converts the authorization challenge
into that redirect. The endpoint is part of the authenticated app shell, and an
anonymous caller has no reason to set a view-state cookie; an unauthenticated
request is redirected to `/login` like the other authenticated page reads, so it
cannot set the cookie.

GET is chosen so the endpoint is served in GitOps read-only mode (the read-only
middleware blocks only unsafe methods, which is why the Statement of Applicability
page is GET-only too), and so the selector links need no antiforgery token per
node. A GET that sets a cookie is acceptable here because it changes only a
personal view preference, re-validated server-side, with no data mutation and no
data exposure; the worst a cross-site trigger could do is change the victim's own
view filter. This trade-off is recorded under Risks.

### Server-side enforcement via a pure subtree helper, applied after resolution

A pure helper `OrgScope.InScopeIds(organisations, accessibleIds, selectedId)`
computes the in-scope id set, always bounded by `accessibleIds`: for a null
selection, the accessible set; for a selected id, the id plus all descendants found
by walking children, intersected with the accessible set, guarded by a visited-set
so a cyclic `parent` link cannot loop forever. Passing the accessible set in keeps
"All Organisations" meaning "every accessible org" rather than "every persisted
org", so an out-of-accessible-set organisation never renders even under "All". It
is pure (no I/O), mirroring the existing pure `StatementOfApplicability.Resolve`,
so the subtree rule is unit tested directly.

The helper lives in the web app, not in `IComplianceStore` / `MySqlComplianceStore`
as a recursive CTE. The tree is already loaded in memory for the projection and is
small, so in-memory subtree computation adds no query and no persistence change,
and it keeps the reference graph and blast radius to the web app. Pushing a
pre-filtering CTE into the store was rejected: the only org-scoped read (Statement
of Applicability) must resolve inheritance over the full tree before filtering, so
the store would still have to return the full tree for that read; a CTE would help
only the JSON endpoints, which stay full-domain in v1. If store-level enforcement
is later needed for scale or a real access model, the filter can move into a CTE
then, keeping the inheritance read on the full tree.

Org-scoped views apply the filter server-side: the page model computes the in-scope
set and renders only those rows, so out-of-scope organisations are absent from the
HTML, not hidden by client code. For the Statement of Applicability page
specifically, `Resolve` is run over the FULL tree first and the resulting node list
is filtered to the in-scope set afterwards. This order is load-bearing: filtering to
the subtree before resolving would drop ancestors above the selected node and lose
inherited dispositions. Resolve-then-filter keeps inheritance correct while
displaying only the subtree.

What "server-side" does and does not mean here. It means the subtree filter is
computed in the page model and out-of-scope rows are absent from the returned
HTML, so the filter cannot be defeated by editing the DOM or replaying a request
from the client. It does NOT mean the user is forbidden from seeing other
organisations' data: in v1 every authenticated user may read the whole compliance
domain, the accessible set is all organisations, and the "All Organisations"
button shows everything by design. Scoping in v1 is therefore a rendered view
filter bounded by the accessibility seam, not an authorization boundary. The
"fail closed" guarantee is narrow and real: a forged or stale cookie naming an
organisation outside the accessible set cannot become the resolved selection.
That guarantee only starts withholding data once the seam narrows below
all-access; today it withholds nothing because nothing is restricted.

Org-scoped surfaces enumerated for v1:

- The `/compliance/statement-of-applicability` page (subtree filter on rendered
  nodes).
- The selector tree itself in the menu (bounded by the accessible set).

Not scoped in v1 (documented Non-Goal): the JSON read endpoints, Home, account, and
admin pages (not org data).

### JSON compliance endpoints stay full-domain in v1

The JSON read endpoints (`/organisations`, `/scopes`,
`/statement-of-applicability/{standardId}`) are not made org-aware in this change.
Because the accessible set is all organisations in v1, an org filter on these
endpoints would withhold nothing a caller is not already entitled to fetch, so it
would be pure speculative surface (three endpoints, a signature or parameter
change, more tests) for no authorization benefit. The endpoints stay full-domain
machine reads behind the same authentication. This is a deliberate consequence of
the "view filter, not authorization boundary" framing above: only when the
accessibility seam first restricts below all-access do these endpoints have
something to enforce, and at that point they MUST honour the same seam in the same
change (see Open Questions). If v1 needed shareable or stateless scoped reads
before then, the `?org=` override seam noted under the cookie decision would be the
place to add it; it is not built here.

### Authorization: a single accessibility seam, all-access in v1

Access is read through one seam, `IOrgAccess`, a pure function of the
already-loaded organisation list:
`IReadOnlySet<string> AccessibleOrgIds(ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)`.
It takes the organisation list its caller already loaded and returns the subset of
ids the user may access; it performs no store read of its own. Its v1 implementation
returns every id from the supplied list, matching today's model where any
authenticated user reads the whole compliance domain. The `user` parameter is unused
in v1 but kept so a future per-organisation model can narrow the returned subset by
membership without a signature change. Both the selector tree and every org-scoped
view are bounded by the set the seam returns, and selection resolution drops any
candidate outside it, so scoping and selection fail closed.

Keeping the seam a pure function of the supplied list, rather than a store-reading
method, means it adds no organisation read: the resolver computes the selector's
accessible set from the list it already loaded, and an org-scoped page computes its
accessible set from the list it read itself (see the store-failure decision), so an
organisation-load failure is always caught by the caller that owns the read rather
than hidden inside the seam.

This is the single point a future per-organisation access model narrows without
touching call sites. Building that model now would be speculative (no membership
data, no requirement in this change) and is a Non-Goal. Whether the seam should be
MIT or an EE carve-out is left open (see Open Questions); the seam interface and its
all-access default are MIT and live in the web app.

### Rendering: a view component invoked from the layout

The tree is rendered by an `OrgSelector` view component invoked from
`_Layout.cshtml`, because the layout has no injected services and a view component
is the framework-native way to run a small piece of server logic (resolve selection
+ build tree) inside a shared layout. It renders a nested list with an "All
Organisations" entry, marks the current selection, and uses Alpine only for
expand/collapse of branches. Each entry is a link to `/org/select`. Because the
component renders on every `_Layout` page - including the account, MFA, and admin
pages the `web-accessibility` spec already audits at zero axe-core violations
across WCAG A/AA/AAA - its markup must not regress that audit. Concretely: each
link SHALL have discernible text (the organisation title, and an accessible label
distinguishing the "All Organisations" entry); the tree SHALL use correct list
semantics (nested `ul`/`li`) rather than roles it does not fully implement; text
and selection-marker contrast SHALL meet WCAG AAA; each expand/collapse toggle
control SHALL have a discernible accessible name and expose its open/closed state
via `aria-expanded`; and the selector SHALL NOT introduce a second `nav` landmark
that duplicates the existing sidebar navigation landmark. The axe audit is extended
to cover the layout-with-selector - seeded with a multi-node tree and a current
selection so the tree markup, selection marker, and toggle controls are actually
present in the audited DOM - and the account/MFA/admin pages continue to pass.
Alternatives considered: `@inject` directly in the layout
(mixes data loading into the shared view and is harder to test) and a middleware
that stuffs the tree into `HttpContext.Items` (indirection with no gain). The view
component is the smallest testable unit.

### Selector degrades to "All Organisations" when the org store is unreachable

The `OrgSelector` view component renders on every `_Layout` page (Home, account,
MFA, admin, and the compliance pages), and it reads the organisation list from the
compliance store, which is lazy and may be unreachable. The Statement of
Applicability page already treats an unreachable store as a store-load failure and
renders an in-page notice (a 200 response with `StoreUnreachable` set, via the page
model's `IsStoreFailure` catch), not a 500; the JSON compliance endpoint is the
surface that returns HTTP 503 for the same outage. That page notice is page-local.
A store outage during layout render would otherwise throw and turn every
authenticated page into a 500, including pages that have nothing to do with
compliance.

The resolver and the view component MUST NOT propagate a store failure into the
layout. When the accessible-set or organisation-list load fails, the resolver
degrades to the "All Organisations" resolved selection and an empty organisation
list. The layout view component degrades silently: it renders only the "All
Organisations" entry with no tree whether the store was empty or unreachable, so an
unrelated page never faults. The resolver's degrade responsibility is limited to the
selector; it exposes no store-failure flag, because the view component renders the
same "All Organisations" entry for an empty store and an unreachable one, so it has
nothing to distinguish.

An org-scoped page (the Statement of Applicability page) does its OWN store-failure
handling and consumes the resolver for NOTHING. It reads its standards, scopes, AND
its organisation list directly from the compliance store inside its existing
try/catch that sets a page-local `StoreUnreachable` notice, so under a store outage
those direct reads throw and the notice renders regardless of the resolver. It then
derives its entire scope from those reads: it computes its accessible set by passing
the organisation list it read to `IOrgAccess`, reads the raw cookie candidate with no
I/O via `OrgSelection.ReadCandidate`, and calls the pure `OrgSelection.Resolve` to
get its own resolved selection, which it feeds to `OrgScope.InScopeIds`. It never
reads the resolver's organisation list, accessible set, or resolved selection, and no
resolver failure flag.

Deriving the whole scope from the page's own reads is load-bearing on two counts.
First, the resolver swallows an organisation-load failure into an empty list, so a
page that took its node list - or its accessible set - from the resolver would render
a healthy but empty table on an organisations-only outage (standards and scopes still
loading) instead of the store-unreachable notice; because the accessible set is
derived from the page's own read, an organisation-load failure fails that read and
raises the notice, never a silently empty accessible set. Second, the resolver
degrades an organisation-load failure to an "All Organisations" resolved selection,
so a page that took the resolved selection from the resolver would silently render
"All Organisations" - losing the cookie-selected subtree, the core capability of this
change - whenever the resolver's own read failed even though the page's own reads
succeeded and the cookie names a valid org. Reading the cookie candidate itself and
resolving it against the page's own accessible set removes that coupling: a transient
failure that hit only the resolver cannot affect the page's scope, and a failure that
hits the page's own read raises the page's notice.

So the degrade is silent only in the layout; the org-scoped pages surface an outage
through their own store reads. The degrade path is failure-only - a healthy store
always renders the full tree - so it does not mask a persistent outage from the pages
that surface it.

### "All Organisations" is the accessible set, not every persisted organisation

"All Organisations" means every organisation in the accessible set, not every
persisted organisation. The two coincide in v1 because the accessible set is all
persisted organisations, but the selector tree and every org-scoped view MUST
bound their rendered nodes by the accessible set even under a null (All) selection,
so the label stays correct the moment the accessibility seam narrows below
all-access. The in-scope computation therefore intersects with the accessible set:
`OrgScope.InScopeIds` takes the accessible ids and, for a null selection, returns
the accessible set (not the full persisted list); for a selected id it returns the
subtree intersected with the accessible set. The selector tree is built from the
accessible organisations only.

## Risks / Trade-offs

- Scoping could be mistaken for an authorization boundary -> it is not one in v1;
  it is a server-side-rendered view filter bounded by the accessibility seam, which
  currently grants all-access. Mitigation for the part that is real: the filter is
  applied in the page model and out-of-scope rows are absent from the HTML, and
  selection is re-validated server-side and intersected with the accessible set. A
  page test asserts out-of-scope ids do not appear in the response body. The seam is
  the single point a future access model turns this into a real boundary.
- No per-organisation authorization model exists, so this change must not imply one
  -> the accessibility seam is added with an explicit all-access v1 implementation
  and documented as the future enforcement point; no membership schema is invented
  speculatively.
- Full-domain JSON endpoints could leak once the seam restricts -> in v1 they leak
  nothing because the accessible set is all organisations. When the seam first
  narrows, the JSON endpoints MUST adopt it in that same change; recorded as an Open
  Question and a Non-Goal so it is not lost.
- Fail-open on a forged or inaccessible selection -> Mitigation: resolution only
  accepts a candidate that is in the accessible set; scoping is always bounded by
  the accessible set, so an inaccessible id can neither be selected nor render data.
- Inheritance broken by scoping the Statement of Applicability -> Mitigation:
  resolve over the full tree, filter the node list afterwards; a test asserts a
  department selected alone still shows the disposition inherited from a company
  above it.
- Stale cookie after an organisation is deleted -> resolves to "All Organisations"
  because the id is no longer in the accessible set; acceptable in v1 where "All"
  spans all organisations. Revisit when a real access model exists.
- GET selection endpoint can be triggered cross-site -> low risk: it changes only
  the user's own view preference, no data mutation or exposure; recorded here as an
  accepted trade-off in exchange for working in read-only mode without per-node
  antiforgery tokens.
- Cyclic `parent` links in stored data -> subtree walk uses a visited-set and
  terminates; a unit test feeds a cycle.
- Compliance-store outage during layout render 500s unrelated pages -> the selector
  renders on every `_Layout` page and reads the lazy compliance store. Mitigation:
  the resolver and view component catch a store-load failure and degrade to only the
  "All Organisations" entry without a tree, never throwing into the layout; a test
  asserts a layout page still renders (not 500) when the store is unreachable.
- New selector markup on every audited page regresses the axe audit -> the selector
  appears on the account, MFA, and admin pages the `web-accessibility` spec audits at
  zero violations. Mitigation: an acceptance criterion and E2E audit step assert the
  layout-with-selector passes at WCAG AAA (discernible link text, correct list
  semantics, AAA contrast, no duplicate nav landmark) and those pages still pass.
- Menu clutter with a large tree -> the tree collapses branches with Alpine. For
  v1 the tree renders collapsed by default and the user expands branches manually;
  the current selection is always rendered, linked, and marked in the DOM, and is
  revealed when its branch is expanded. A selected leaf under a collapsed parent is
  present and marked in the markup but visually hidden until the user expands its
  ancestor, since v1 does no ancestor pre-expansion. Computing and pre-expanding
  the selected node's ancestor chain on load is a deferred nicety, not built here
  (code-as-liability): it adds ancestor-walk logic and per-node open-state for a
  small tree the user can expand in one click. Not a correctness risk.

## Migration Plan

No data migration. Deploy is additive: new files plus DI registration and the menu
entry. No cookie exists before deploy, so every user starts at "All Organisations"
(current behaviour). Rollback is removing the menu entry and endpoint; a leftover
`freeboard-org` cookie is inert once the resolver is gone.

## Open Questions

- Is a real per-user, per-organisation access model in scope soon? If so, should
  `IOrgAccess` (or its non-default implementation) live in `Freeboard.Enterprise`
  as a paid feature, or stay MIT in the web app? This change keeps the seam and its
  all-access default MIT and defers the decision.
- When the accessibility seam first narrows below all-access, the JSON compliance
  read endpoints must adopt it in that same change (otherwise they would then leak).
  v1 leaves them full-domain because the seam grants all-access; the open part is
  only the timing of the future change that first restricts, not whether to do it.
- Placement: selector in the left sidebar (assumed here) versus the top-bar
  account dropdown. Sidebar is assumed as "the menu"; confirm with design.
