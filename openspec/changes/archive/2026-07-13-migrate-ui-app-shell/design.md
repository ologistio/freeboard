## Context

P1 shipped the token set, self-hosted fonts, the `data-theme` override, and the
pre-paint theme reader in `_Head.cshtml`; the reader applies a stored `fb-theme`
of `light` or `dark` to `document.documentElement.dataset.theme` before first
paint, but nothing writes that value yet. That script is deliberately static
(no server interpolation) to keep a stable hash for a future strict CSP, so a
new inline `<script>` is a cost to avoid. P2 shipped the tokenized `fb-*`
component classes and the invariant-carrying mark tag helpers (`fb-status`,
`fb-tag`, `fb-badge`, `fb-chip`, `fb-due`, `fb-owner`, `fb-stamp`) in
`src/Freeboard`. Both landed behind the existing pages.

The running chrome is `Pages/Shared/_Layout.cshtml`: a 256px sidebar with every
nav item hand-inlined, an admin section gated inline by
`AuthzViewHelpers.CanReachAdminAsync` / `CanAdministerSystemAsync` and
`IEnterpriseEntitlements.IsEntitled(CustomPolicies)`, the `OrgSelector` view
component dropped in mid-list, a topbar with only a mobile menu button and an
Alpine account menu, and a `max-w-6xl` main. Active state is already computed by
prefix-matching the request path (`IsActive`), not by title. The authoritative
target is `stories/AppShell.stories.js` with `stories/_marks.js` `railNav`,
`shellTopbar`, and `appFrame`: a 236px grouped rail, a breadcrumb topbar with
audit countdown, theme toggle, notifications, and account, over a scrolling
`fb-main`.

Constraints that bind this change:

- `web-ux-conventions` N1-N8 (nav depth, one group per page, shared-noun group
  names, no module settings or report pages, actionable-only badges, one command
  palette, linked breadcrumb), A2 (keyboard path, visible focus), A3 (reduced
  motion), **A4 (hit targets at least 44px outside dense tables)**, A6 (both
  themes, personal theme).
- `web-design-system`: use its tokens, `fb-*` classes, and tag helpers; the
  components layer carries no literal color and no framework built-in color
  utility; dark stays gated (no `prefers-color-scheme` activation block).
- The reference graph: shell code lives in `src/Freeboard`, the only project that
  combines Core and EE. EE gating uses `IEnterpriseEntitlements` from the web app.
- `code-as-liability`: reuse `OrgSelector`/`OrgSelectionResolver` and the P2 tag
  helpers; one nav catalog, not per-item markup; retire classes that lose their
  last caller; do not build infrastructure for data that does not exist yet.

## Synthesis note (this change merges two independent plans)

This design merges Plan A (the OpenSpec artifacts already drafted here) and
Plan B (Codex's independent P3 plan). Each decision below tags its provenance:
**[A]** from Plan A, **[B]** from Plan B, **[A+B]** where both agreed, and
**[merge]** where they diverged and this document resolves it on the merits.
The "Divergences resolved" section lists every point where the two disagreed and
why the chosen path wins. Facts were verified against the repo before adoption
(the components guard test, the org resolver, the entitlement calls, the
`/org/select` route, the pre-paint reader, the A4 threshold, and the story
dimensions), so the plan is grounded rather than speculative.

## Goals / Non-Goals

**Goals:**

- Every authenticated page renders inside the new shell in both themes, with the
  correct active nav item and breadcrumb, a working keyboard path with visible
  focus, and a working mobile drawer.
- One nav catalog data structure is the single source for the rail now and the
  palette index (P4) and breadcrumb next.
- The theme toggle writes `fb-theme`, so the P1 reader has a setter; the setter
  respects reduced motion and does not introduce `prefers-color-scheme`
  activation.
- The pre-auth layout is restyled to tokens.
- The N4/N5 IA re-home is applied now (see D11): the target nav groups are adopted
  and the configuration and administration pages move under `/settings` as a clean
  break, with the four established specs that pin the old paths carrying `MODIFIED`
  deltas in this change.

**Non-Goals:**

- Command palette and object-detail drawer behavior (P4). The rail's palette
  entry is a static, keyboard-focusable affordance.
- Page-body migration to compositions (P5) and placeholder destination pages
  (P6). Full N4 consolidation - merging the moved page bodies into one Settings page
  with per-module sections - is P5 page-body work; this change moves and groups the
  routes, meeting N4/N5 at the route/IA level.
- Redirects from the old paths. The route move is a clean break; old URLs cease to
  exist.
- `prefers-color-scheme` system-default dark activation (still staged in
  `web-design-system`).

## Decisions

### D1 Shell surfaces are view components; the layout owns only the frame [A+B]

The rail and the breadcrumb need request data and authorization: N6 actionable
counts, EE entitlement gating, the org resolver, and the current path. They are
built as server-rendered view components in the light DOM, following the existing
`OrgSelectorViewComponent` pattern (constructor-injected request-scoped service,
`InvokeAsync`, a `Default.cshtml` view emitting `fb-*` classes). `_Layout.cshtml`
keeps only the frame and `@RenderBody()`; it holds no inlined nav, authz, or
entitlement logic (that all moves into the resolver, D2/D8). Alternative: keep
inlining in `_Layout`. Rejected - the current file already mixes authz calls,
entitlement checks, and hand-written items; inlining the larger target would be
unreadable and untestable, and the palette index (P4) and breadcrumb need the
same data.

Components (both plans converged on these; Codex named the folders):

- `ShellRailViewComponent` (`Pages/Shared/Components/ShellRail/`) - renders the
  rail (brand, edition tag, palette entry, grouped nav with active state and
  badges) from the resolved catalog.
- `ShellTopbarViewComponent` (`Pages/Shared/Components/ShellTopbar/`) - renders
  the breadcrumb, audit-countdown slot, theme toggle, notifications slot, and
  account menu. **[merge]** Plan A rendered the breadcrumb as a standalone
  `BreadcrumbViewComponent` and left the rest inline in `_Layout`; Plan B made
  the whole topbar a view component. Resolved to one topbar view component so the
  request-aware slots (breadcrumb, countdown, pip) all sit behind the same
  testable seam and `_Layout` stays frame-only.
- Workspace switcher - reuse the existing `OrgSelector` view component in the
  rail foot, restyled to `fb-wspick`. It already reads `OrgSelectionResolver` and
  posts to `/org/select?return=...`; no new component and no re-implemented org
  tree. **[merge]** Plan B proposed a new `WorkspaceSwitcher` component "backed by
  current OrgSelector logic"; direct reuse of the existing component is lower
  liability and keeps the fail-closed store-outage degrade already built in.

### D2 One nav catalog drives rail, breadcrumb, and the future palette index [A+B, structure from B]

A single server-side catalog is the source of truth, expressed as a small typed
model under `src/Freeboard/Navigation/`:

- `ShellNavGroup` - a group with a shared-noun label and ordered items.
- `ShellNavItem` - a stable key, a label, a current route, its group, an optional
  EE entitlement requirement, and an optional authorization predicate.
- `ShellNavCatalog` - the static, declarative list of groups and items (plain
  data, no per-request state).
- `ShellNavResolver` - a request-scoped service that evaluates the catalog for
  the current request: it drops items whose entitlement or authorization predicate
  fails, resolves the active item, and (when a source exists) attaches counts.
  The rail and topbar view components render the resolver's output; P4's palette
  index will enumerate the same catalog.

**[merge]** Plan A carried the per-item authz/entitlement/count evaluation inside
the rail view component and embedded resolver delegates in the map; Plan B pulled
that into a scoped `ShellNavResolver` and kept the catalog pure data. Resolved to
Plan B's split: the catalog stays declarative and unit-testable as data, and the
per-request evaluation is one scoped service that both the rail and the palette
(P4) share, tested against fakes exactly as `OrgSelector` is. N2/N7/N8 consistency
holds by construction because one catalog feeds every surface.

The catalog lives in the web app (it references EE entitlement enums and web
routes); this does not touch the reference graph.

### D3 Active-nav and breadcrumb come from page-declared ViewData [A+B, resolution rule from B]

Pages declare `ViewData["NavGroup"]` and `ViewData["Title"]`, plus optional
`ViewData["NavItem"]` (an explicit catalog key), `ViewData["BreadcrumbDetail"]`,
and `ViewData["BreadcrumbDetailHref"]`. The topbar view component renders group
then page then detail (N8), each as a link: the group segment links to that
group's primary destination (its first routable item), the page segment to the
page's own route, the detail to `BreadcrumbDetailHref` (or the current URL).

**[merge]** Plan A resolved the active item by matching the page's declared group
and title against the catalog. Plan B resolved it by explicit `NavItem` key
first, then longest-route-match, and warned against relying on the display title.
Resolved to Plan B's rule: the resolver marks active by explicit `NavItem` key
when the page declares one, else by longest matching route against the current
path (the mechanism `_Layout.IsActive` already uses today). Title matching is
brittle - two pages can share a title, and a title is display copy that changes -
so it is not the key. Exactly one item is marked `aria-current="page"`.

P3 wires `NavGroup`/`Title` onto the existing authenticated pages. A page that
declares neither still renders: the breadcrumb shows just its `<title>` and no
item is active, so the shell degrades safely for anything not yet wired.

### D4 Theme toggle is an Alpine component registered in app.js [merge -> B]

The topbar theme toggle is an Alpine component registered in `app.js`
(`Alpine.data("themeToggle", ...)`), bound in the topbar markup with `x-data`. It
reads `document.documentElement.dataset.theme`, toggles between `light` and
`dark`, writes `localStorage` `fb-theme`, and sets `data-theme` on `<html>` - the
exact key, values, and attribute the P1 pre-paint reader in `_Head.cshtml`
consumes. The button icon reflects state (moon in light, sun in dark), and
`aria-pressed` / `aria-label` state the control. Any transition is guarded by
`prefers-reduced-motion` (A3) in CSS, not JS. It writes only explicit `light` or
`dark`; it never adds `prefers-color-scheme` activation, so the
`web-design-system` staging holds.

**[merge]** Plan A authored the toggle as inline Alpine in the topbar markup and
kept `app.js` a four-line bootstrap. Plan B put the behavior in `app.js` as an
Alpine component and stressed "no new inline `<script>`". Resolved to Plan B:
registering `Alpine.data` in `app.js` (an ES module the bundler ships, not an
inline `<script>`) keeps the `_Head.cshtml` pre-paint script the only inline
script, preserving its stable-hash CSP intent, and it is unit-testable and reused
by the drawer and account-menu components (D5). This is a small, justified growth
of `app.js`, not the four-line bootstrap - but it is the right home for the
shell's client behavior. A server round-trip to persist theme is rejected here:
theme is a personal client-side setting (A6) and the reader is already
client-side `localStorage`.

### D5 Mobile drawer and account menu are Alpine components with Escape and focus restore [merge -> B]

The rail collapses to a drawer below the desktop breakpoint using an Alpine
component in `app.js`, reusing the scrim-and-slide shape already in `_Layout` but
adding a keyboard contract: the open control lives in the topbar; while the drawer
is open it traps focus (or holds the background inert) so keyboard focus cannot
land on the obscured page behind it; Escape closes the drawer and the account menu
and restores focus to the opener; the drawer slide honors reduced motion (A3).
Visible focus (A2) is preserved throughout.

The focus trap is required by A5 ("Drawers and dialogs SHALL trap focus, restore it
on close, and close on Escape"). The mobile nav drawer is a drawer, so A5 applies -
it is not exempt. This is not the P4 object-detail drawer (a modal focus trap over
an ARIA dialog); it is the lighter nav-drawer contract - a focus trap or an inert
background plus Escape and focus restore - so the cost is small and stays inside the
existing pattern's shape. The account-menu popover closes on Escape and restores
focus but is a menu, not a drawer, so it does not need the full A5 focus trap.

**[merge]** Plan A reused the existing inline `x-data="{ nav: false }"` drawer
as-is. Plan B moved the drawer and account menu into `app.js` Alpine components
and added Escape / focus-restore. Resolved to Plan B plus the A5 focus trap: A2
requires a full keyboard path with visible focus and A5 requires the drawer to trap
and restore focus, and the current drawer has neither Escape, focus restoration, nor
a trap.

### D6 Nav badges show actionable counts only, and no provider is built until a source exists [merge]

Per N6, a nav item badges only when its count is actionable (failing, or waiting
on the viewer); passing and informational totals never badge, and a store failure
degrades to no badge, never a layout failure. Counts are never invented to match
the Storybook sample numbers.

**[merge]** Plan A embedded an optional count resolver delegate per catalog item;
Plan B proposed an `INavBadgeProvider` seam that degrades to no-badge on failure.
Neither ships a real count today - the repo has no server-side actionable-count
source (OQ2). Resolved to the lower-liability seam: `ShellNavResolver` is the
count seam. `ShellNavItem` carries an optional count that the resolver attaches;
this phase the resolver leaves it unset for every item, so nothing badges. A
standalone `INavBadgeProvider` interface is **not** created now, because building
an abstraction with zero implementations is the speculative infrastructure
`code-as-liability` forbids. When a real source lands, inject it into the resolver
(or introduce the provider then, when there is a caller). The rail view and the
`fb-navcount` class support rendering a badge, so wiring a real count later is a
resolver change, not markup churn.

### D7 Shell chrome classes are added to the components layer, tokenized, at A4 hit sizes [A+B, sizing from B]

The shell classes (`fb-app`, `fb-stage`, `fb-main`, `fb-rail`, `fb-brand`,
`fb-mark`, `fb-name`, `fb-rev`, `fb-search-entry`, `fb-kbd`, `fb-navwrap`,
`fb-navgroup`, `fb-navitem`, `fb-navcount`, `fb-rail-foot`, `fb-wspick`,
`fb-topbar`, `fb-crumb`, `fb-topbar-right`, `fb-countdown`, `fb-iconbtn`,
`fb-pip`, `fb-avatar`) are ported from `stories/_marks.js` into
`app.css @layer components` with every literal color replaced by a token, meeting
AA in both themes (A6). They carry no literal color and no framework built-in
color utility.

**[merge -> B, M1]** The story sizes several controls below the A4 threshold:
`.fb-iconbtn` and `.fb-avatar` are 30px and `.fb-navitem` is a 6px/10px-padded
row. A4 requires at least 44px outside dense tables. The ported classes therefore
use >=44px interactive hit sizes for the topbar icon buttons, the avatar button,
the palette entry, and the nav items (the visual mark may stay smaller inside a
44px target). The story dimensions are a prototype, not the spec; A4 wins.

The existing `ComponentLayerGuardTests` already scans the whole `@layer
components` block, so the new classes are covered by the no-literal-color and
no-built-in-utility guards without any new test. The interactive-control resting-
boundary guard has an explicit class list; the new bordered interactive controls
(`fb-iconbtn`, `fb-search-entry`) are added to it so their resting border uses a
>=3:1 token (A1).

The legacy `nav-link` and `nav-section-title` classes lose their only callers once
`_Layout` is rewritten and `OrgSelector` is restyled, so they are removed (one class
per job). `menu-item` is NOT retired this phase: the account menu is carried into the
topbar `fb-avatar` menu (task 4.2) and its dropdown links keep `menu-item`, so it still
has a live caller. It is already token-based (no literal color) and names a distinct job
(a dropdown menu item, not a rail nav row), so reusing it is lower liability than minting
a new `fb-*` account-menu-item class for one caller (`code-as-liability`). `acct-tab`
likewise keeps its caller (`_AccountNav`, migrated in P5) and stays.

### D8 EE and admin gating live in the scoped resolver; hidden items emit nothing [merge -> B]

Nav items that surface enterprise or admin features carry their requirement in the
catalog (an EE entitlement and/or an authorization predicate). `ShellNavResolver`
evaluates them with `IEnterpriseEntitlements` and the existing `AuthzViewHelpers`
calls - the same `CustomPolicies` entitlement plus `CanAdministerSystem` predicate
`_Layout` gates Custom Roles with today, and `CanReachAdmin` for the admin group.
A hidden item is dropped entirely: the rail emits no label and no `href` for it,
so a gated destination is not leaked in the markup.

**[merge]** Plan A resolved gating in the rail view component; Plan B put it in the
scoped resolver and required hidden items to emit no label or href. Resolved to
Plan B: gating in the resolver keeps it unit-testable independently of the view and
guarantees the "emit nothing" property in one place. This code lives only in
`src/Freeboard`; the one-way EE rule is unchanged and no MIT project is touched.

### D9 Data-driven chrome renders only from a provider; unset is empty, never fake [A+B, H2]

The audit countdown and the notifications pip render only from a real data source:
the countdown shows only when a real audit date exists, and the pip only when unread
notifications exist. The repo has **no** audit-deadline or notification model
(verified), so both render in their empty form (no countdown, bell without pip)
rather than the Storybook sample values. No "SOC 2 in 21d" string and no fabricated
pip ship.

The edition tag follows the same honest-empty rule. There is no edition-name signal
in the build: `EnterpriseEntitlement` (verified: `src/Freeboard.Core/Enterprise/
EnterpriseEntitlement.cs`) enumerates a single feature entitlement, `CustomPolicies
= 1`, not an edition. Inferring "Enterprise" from `CustomPolicies` would be
dishonest - it names one feature, not the product tier. So the edition tag renders
empty or is omitted until a real edition signal exists, exactly as the countdown and
pip do. This keeps the chrome honest (W3) rather than advertising a tier that is not
modelled.

**[A+B]** Both plans reached this independently; it is Plan B's H2 blocking concern
and Plan A's D9. The topbar view component exposes the countdown and pip as slots
fed by a provider; unset means the slot is hidden or neutral-empty.

### D10 The static palette entry is inert and claims no dialog [merge -> B]

The rail shows exactly one command-palette entry carrying the `Ctrl K` hint (N7:
one search surface, no second global search box in the chrome). In this change it
is a non-functional affordance: it opens nothing. **[merge -> B]** It does **not**
set `aria-haspopup="dialog"` and is not wired to a listener, because no dialog
exists until P4; advertising a dialog that cannot open would be a false ARIA
promise. It is rendered as a visibly non-operative control that stays
keyboard-focusable - a non-activating button or link kept in the tab order - so a
keyboard or screen-reader user is not told it opens a palette yet can still tab past
it. It is **not** `disabled`: a disabled control drops out of the tab order and
breaks the full keyboard path (A2). P4 replaces it with the real combobox.

### D11 The IA re-home is APPLIED now: groups adopted, routes moved, no redirects [mediator decision]

The mediator resolved the former open question. Both layers are applied in this
change, not deferred:

- **Layer A (grouping):** adopt the target nav groups now - Comply, Risk, Trust,
  Resources, Platform, plus a group-less top set for Home / My work. Real
  destinations map into these groups.
- **Layer B (route moves):** move the configuration and administration pages under
  `/settings` now, as a **clean break with no redirects**. The old URLs cease to
  exist. This is acceptable because the software is pre-release; carrying redirect
  routes for URLs no user depends on yet is needless routing surface
  (`code-as-liability`).

**How the move is done (verified against the repo).** These routes are declared by
each page's `@page` directive, not by route conventions in `Program.cs` (`Program.cs`
holds only `AuthorizeFolder`/`AuthorizePage` conventions and `MapRazorPages()`, no
`AddPageRoute`). So the move is an edit to each `@page` string. The page files stay
in their current Razor Pages folders; the folder authorization conventions
(`AuthorizeFolder("/Compliance")`, `AuthorizeFolder("/Admin")`) are keyed on the page
file path, not the URL, so moving the URL leaves authorization and in-page
enforcement untouched. This corrects the mediator's note about "Program.cs route
conventions": there are none to change; the `@page` directives are the routes.

**The applied route moves (current -> new), verified:**

| Current route | New route |
| --- | --- |
| `/compliance/evidence-collectors` | `/settings/evidence-collectors` |
| `/compliance/attestation-templates` | `/settings/attestation-templates` |
| `/admin/users` | `/settings/users` |
| `/admin/custom-roles` | `/settings/custom-roles` |
| `/admin/role-assignments` | `/settings/role-assignments` |

Two coupled child routes verified in the repo move with their parents for coherence
(the mediator's list named only the parents): the custom-role editor
`/admin/custom-roles/designer/{slug?}` -> `/settings/custom-roles/designer/{slug?}`,
and the one-time temporary-password display `/admin/usercredential` ->
`/settings/usercredential` (the create/reset flow on the users page redirects to it).
Leaving a child under `/admin` while its parent sits under `/settings` would be
incoherent.

**No `/reports` move.** The mediator asked to move "any module reporting page" under
`/reports`. Verified: no module reporting page exists today (no `/reports*` route),
so nothing moves under `/reports`. N5 is satisfied because there is no module report
page to eliminate.

**Spec deltas carried by this change** (an undocumented path change is a spec
violation). The established specs that pin a moved path each get a `MODIFIED` delta
under this change's `specs/`: `evidence-collector-register`,
`attestation-template-register`, `custom-role-designer` (page URL only; its
`/api/v1/freeboard/custom-roles` API is unchanged), and `admin-web-screens` (the
user-management page URL). `user-admin` is **not** modified - it pins only the
`/api/v1/freeboard/users` API routes, which do not move. No other established spec
(`compliance-web-read`, `statement-of-applicability`, `vendor-register`,
`auth-web-screens`) pins a moved path.

**The pinned default nav catalog.** These are the only real destinations that render
as live rail links in P3. Each row is a `ShellNavItem`: a stable key, a label
(sentence case, W1), a group, its new route, and any gating. Labels and routes are
final; the group set is adopted per the mediator, so the item-to-group mapping is not
provisional (placeholder siblings from the roadmap table - Frameworks, Controls,
Risks, Trust Center, People, Reports, Integrations, and so on - are out of scope
until P6 and do not render).

| Key | Label | Group | New route | Gating | Provisional grouping? |
| --- | --- | --- | --- | --- | --- |
| `home` | Home | (top, group-less) | `/home` | authenticated | no |
| `soa` | Statement of applicability | Comply | `/compliance/statement-of-applicability` | authenticated | no |
| `vendors` | Vendors | Risk | `/compliance/vendors` | authenticated | no |
| `evidence-collectors` | Evidence collectors | Platform | `/settings/evidence-collectors` | authenticated | no |
| `attestation-templates` | Attestation templates | Platform | `/settings/attestation-templates` | authenticated | no |
| `users` | Users | Platform | `/settings/users` | admin (`CanReachAdmin`) | no |
| `custom-roles` | Custom roles | Platform | `/settings/custom-roles` | EE `CustomPolicies` + `CanAdministerSystem` | no |

Role Assignments (`/settings/role-assignments`) is **omitted from the P3 rail** (see
D12); its route still moves and its test still updates, but it does not render as a
live rail item this phase. The Statement of Applicability and Vendors routes do not
move (they are already correctly placed under Comply and Risk), only their group
assignment is set.

**Account pages.** The personal Account pages (`Account/Index`, `Account/Mfa/*`,
`Account/Password/*`, `Account/Sessions`) already use the default `_Layout` (verified:
no layout override), so they render inside the shell. They are reached from the
account (avatar) menu, not a primary rail item, and carry a Platform then Account
breadcrumb. The auth-funnel pages stay on `_AuthLayout` (verified: `Account/Sudo` and
`Account/CompleteReset` set `Layout = "_AuthLayout"`, as do `Login`,
`ForgotPassword`, `ResetPassword`); they are not "in the shell".

**Group-less breadcrumb (F-11).** Home and My work sit in the group-less top set, so
their breadcrumb is a single page segment with no group segment (a working link).
This satisfies N8: its group-then-page-then-detail order names the levels that exist
for the page, and a top-level page has no parent group to name.

**N-conformance, stated honestly (F-5).** With the routes moved and grouped, the
shell satisfies, at the chrome/route level: N1 (two levels, group then page), N2 (one
group per page, enforced by the catalog), N3 (Comply/Risk/Trust/Resources/Platform
are shared jobs or nouns, not module or SKU names), N7 (one inert palette entry, no
second search box), and N8 (linked breadcrumb, including the group-less form above).
**N4** is met at the route/IA level: module configuration pages (evidence collectors,
attestation templates) and the admin pages no longer hang off a feature's own nav
entry - they live under `/settings`. The exact wording of N4 is "Module-specific
settings pages SHALL NOT exist. All configuration SHALL live in Settings, sectioned by
module"; the "sectioned within a single Settings page" part is page-body consolidation
staged to P5, so N4 is met at the route/IA level now with body-consolidation to
follow. **N5** ("Module-specific report pages SHALL NOT exist") is satisfied because
no module report page exists to eliminate. **N6** is met as "no fabricated badges":
the resolver leaves counts unset (no actionable-count source exists, D6), so nothing
badges; full actionable-count wiring waits for a real source. This is the claim
mirrored in `proposal.md` and the spec delta - no stronger claim is made.

### D12 Role Assignments is omitted from the P3 rail until its access model is settled [grounded]

The Role Assignments page enforces per-organisation `authz.assignment.write` through
`AuthzPageGuard` (verified: `RoleAssignments.cshtml.cs` calls
`pageGuard.CheckAsync(User, AuthzActions.AuthzAssignmentWrite, ...)` for a target
`orgId`). The admin nav predicate `CanReachAdmin` (verified: `AuthzViewHelpers.cs`)
passes on `system.admin` **or** `user.manage`, which does not imply
`authz.assignment.write` on any org. So gating a Role Assignments rail item on
`CanReachAdmin` is the wrong predicate: it would show the item to a `user.manage`
admin who can assign roles nowhere, and hide it from an org manager who holds
`authz.assignment.write` but not `user.manage`.

A verified nuance: the bare page (`/settings/role-assignments` with no `orgId`)
returns 200 for any authenticated user - the guard runs only once an `orgId` is
supplied - so the mismatch does not literally 403 the nav target, but the affordance
is still wrong on both sides. There is no view-side helper for
`authz.assignment.write`, and the permission is per-target-org while the rail is
global chrome, so a correct global predicate is not obvious.

Decision: omit Role Assignments from the rail in P3 (lower liability than inventing a
per-org predicate for a global item, `code-as-liability`). Its route still moves to
`/settings/role-assignments` and its path-asserting test still updates, so the page
and its spec stay coherent; it simply is not a live rail link until the access model
is resolved (a correct `AuthzViewHelpers` predicate for assignment-write, or a
decision that the item is org-scoped). This keeps the "a link never leads to a place
the viewer cannot use" property true for the rail.

## Risks / Trade-offs

- [The IA re-home moves routes and breaks path-asserting web and E2E tests] -> the
  moves are edits to `@page` directives (files stay put, so folder authorization is
  unchanged); the path-asserting tests are updated to the new `/settings` paths, not
  deleted, and the preserved markers stay. No redirects are added (clean break), so
  a test that expects the old path to answer must be updated to expect it gone. Each
  moved path's established spec carries a `MODIFIED` delta in this change.
- [Theme toggle and the P1 reader disagree on key or values, causing a flash or a
  stuck theme] -> the toggle writes exactly the `fb-theme` key and `light`/`dark`
  values the `_Head.cshtml` reader parses; a test asserts the written key and the
  `data-theme` attribute match the reader's contract and that no
  `prefers-color-scheme` activation is present in the served CSS.
- [Growing `app.js` beyond the bootstrap adds client complexity] -> the additions
  are three small Alpine components (theme toggle, drawer, account menu) that the
  shell genuinely needs for the A2 keyboard path; they replace inline markup logic
  rather than adding a new concern, and keeping them out of a new inline `<script>`
  preserves the pre-paint script's stable-hash CSP intent.
- [Nav badges fabricate counts to match the Storybook sample] -> badges render only
  from a real backing store via the resolver; items without a source ship unbadged.
  A test asserts no badge appears for an item lacking a count source.
- [Building a badge provider with no data source is speculative] -> no provider
  interface is built; the resolver is the seam and leaves counts unset this phase.
- [Literal color, a built-in color utility, or a sub-44px control creeps into the
  new shell classes] -> the existing `ComponentLayerGuardTests` scans the whole
  components layer including these classes; the interactive controls are added to
  its resting-boundary guard; A4 sizes are set from tokens, not the story literals.
- [Rewriting `_Layout` regresses a preserved test marker or the account menu] ->
  the account menu, antiforgery logout form, and initials logic are carried over;
  the preserved markers live in page bodies, which P3 does not touch. Existing page
  and E2E tests run unchanged.
- [EE/admin gating regressed while moving items into the catalog] -> the catalog
  carries the same `CustomPolicies` entitlement plus `CanAdministerSystem` predicate
  (Custom Roles) and `CanReachAdmin` (Users) the current `_Layout` uses; Role
  Assignments is omitted from the rail (D12) rather than gated on a mismatched
  predicate; the resolver drops hidden items entirely (no leaked label/href), covered
  by `AdminPageAuthzTests` / `CustomRolesPageTests` plus a resolver render test.
- [Static palette entry falsely advertises a dialog] -> it sets no
  `aria-haspopup="dialog"` and opens nothing until P4.
- [Per-request evaluation of entitlements, authz, and counts in the resolver adds
  cost] -> the same calls `_Layout` already makes per request; counts resolve
  lazily and only for items that carry a source (none this phase).

## Migration Plan

1. Land the shell chrome classes in `app.css` (tokenized, A4 hit sizes) with no
   page consuming them yet - no visual change.
2. Add the `Navigation/` catalog (`ShellNavGroup`/`ShellNavItem`/`ShellNavCatalog`)
   and the `ShellNavResolver` scoped service with unit tests, built to the D11
   pinned catalog (new `/settings` routes; Role Assignments omitted per D12).
3. Add the rail and topbar view components with render tests.
4. Rewrite `_Layout` to a frame that composes `<vc:shell-rail />`,
   `<vc:shell-topbar />`, and `<main class="fb-main">@RenderBody()</main>`;
   restyle `OrgSelector` for the rail foot.
5. Add the Alpine components in `app.js` (theme toggle, drawer with A5 focus trap /
   Escape / focus restore, account menu) with Escape and focus restore.
6. Move the routes: edit the `@page` directives to the new `/settings` paths (D11
   table plus the two coupled child routes), files staying in place. Fix the literal
   in-app `href` targets that pointed at a moved path (Home card, Custom Roles
   create/Edit, Custom Role Designer back/cancel, credential back-to-users) and the
   `TempPasswordDisplayStore.DisplayPath` const (redirect target and case-sensitive
   cookie path). Update the path-asserting web and E2E tests to the new paths,
   preserving the markers.
7. Wire `NavGroup`/`Title` (and optional `NavItem`/detail) `ViewData` onto the
   authenticated application pages, including Account/* (Platform / Account).
8. Restyle `_AuthLayout` to tokens.
9. Run `dotnet build`, the web test suite, and `openspec validate --strict`; confirm
   moved pages answer at their new routes, old paths are gone, and all preserved
   markers still pass.

Rollback: the change is confined to the shared layouts, the new view components,
the nav catalog and resolver, the `app.js` components, and the components-layer
additions; reverting the commit restores the prior `_Layout`. No route, data
model, or service contract changes, so rollback is a straight revert.

## Open Questions

1. IA re-home (see D11) - RESOLVED by the mediator. Both layers are applied now:
   the target nav groups are adopted and the configuration and administration pages
   move under `/settings` as a clean break with no redirects; the four established
   specs that pin the old paths carry `MODIFIED` deltas; Account personal pages
   render in the shell under Platform / Account; Role Assignments is omitted from the
   rail (D12) though its route still moves. No module reporting page exists, so
   nothing moves under `/reports`.
2. Which nav items have a real, server-side actionable-count source today (N6)?
   Items without one ship unbadged and no provider is built until a source exists.
3. Are the audit countdown and notifications pip backed by any data yet? They are
   not today, so both ship in their empty form (D9); confirm that is acceptable for
   the first cut.
