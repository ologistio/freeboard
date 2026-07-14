## Why

The token foundation (P1) and component library plus mark tag helpers (P2) have
landed behind the existing pages, but the running app still wears the old
256px-sidebar chrome: a hand-inlined nav, a user-menu-only topbar, and no
breadcrumb, command palette, theme control, or workspace switcher. The adopted
"audit ledger" direction is specified by `stories/AppShell.stories.js` and
`stories/_marks.js` (`railNav` / `shellTopbar` / `appFrame`) and is ratified as
`web-ux-conventions` (N1-N8, A2, A3, A6). P3 builds the shell every
authenticated page renders inside, so the rest of the migration (pages,
placeholders) has a frame to sit in.

## What Changes

- Add the authenticated app shell: a 236px grouped nav rail (brand mark, an edition
  tag that renders empty until a real edition signal exists, a command-palette entry
  that is a static, inert but keyboard-focusable affordance this phase, grouped nav
  with active state and actionable count badges, a workspace switcher pinned to the
  foot) and a breadcrumb topbar (breadcrumb with linked segments, audit countdown,
  theme toggle, notifications, account menu) over a scrolling main. Rewrites
  `Pages/Shared/_Layout.cshtml` to a frame that composes the shell view components
  and `@RenderBody()` - no inlined nav or authz logic.
- Apply the information-architecture re-home now: adopt the target nav groups
  (Comply, Risk, Trust, Resources, Platform, plus a group-less top set for Home / My
  work) and move the configuration and administration pages under `/settings`
  (evidence collectors, attestation templates, users, custom roles, role
  assignments), a clean break with no redirects from the old paths. This carries
  `MODIFIED` spec deltas for the four established specs that pin the old paths.
- Introduce one server-side nav catalog (`Navigation/ShellNavGroup`,
  `ShellNavItem`, `ShellNavCatalog`) as the single declarative source that drives
  the rail today and the command-palette index (P4) and breadcrumb next, plus a
  request-scoped `ShellNavResolver` that evaluates it per request (entitlement and
  authorization gating, active-item resolution, count attachment).
- Render the rail and topbar as view components (the existing `OrgSelector`
  pattern) so they can read request data via the resolver. Gating lives in the
  scoped resolver, not Razor markup; a hidden EE or admin item emits no label and
  no `href`. The workspace switcher reuses the existing `OrgSelector` view
  component (backed by `OrgSelectionResolver` and `/org/select`) in the rail foot.
- Add a breadcrumb and active-nav convention: pages declare `ViewData["NavGroup"]`
  and `ViewData["Title"]` (and optional `ViewData["NavItem"]` key,
  `ViewData["BreadcrumbDetail"]`, `ViewData["BreadcrumbDetailHref"]`); the shell
  reads them to render the breadcrumb (N8, each segment a link) and mark the
  active item, resolving active by explicit `NavItem` key first, then longest
  route match - not display title. Wire these values onto the existing
  authenticated pages, including Account/*.
- Add the theme-toggle setter: an Alpine component registered in `app.js` that
  writes the per-person `fb-theme` preference the P1 pre-paint reader already
  consumes; its icon reflects state and it honors `prefers-reduced-motion` (A3).
  It adds no new inline `<script>`, preserving the pre-paint reader's stable-hash
  CSP intent. Dark stays gated: no `@media (prefers-color-scheme: dark)`
  activation is added (staged to P5).
- Move the rail-to-drawer collapse and the account menu into Alpine components in
  `app.js`, adding an Escape-to-close and focus-restore keyboard contract; keep a
  full keyboard path with visible focus (A2), drawer slide honoring reduced motion.
- Add the shell chrome component classes (`fb-app`, `fb-rail`, `fb-navitem`,
  `fb-topbar`, `fb-crumb`, `fb-iconbtn`, `fb-avatar`, ...) to
  `app.css @layer components` from tokens only (no literal color, no framework
  built-in color utility), meeting AA in both themes (A6). Interactive controls
  use >=44px hit targets per A4, overriding the sub-44px sizes in the prototype
  story. Retire the legacy `nav-link` and `nav-section-title` classes once no caller
  remains (one class per job); `menu-item` is kept because the account menu carried into
  the topbar still uses it.
- Restyle `Pages/Shared/_AuthLayout.cshtml` (pre-auth) to the new tokens.

## Capabilities

### New Capabilities

- `web-app-shell`: the authenticated application chrome - the nav rail, the
  breadcrumb topbar, the one nav catalog and scoped resolver that drive them, the
  `ViewData` breadcrumb and active-nav convention, the theme-toggle setter, the
  mobile drawer, and the tokenized pre-auth layout. Composes `web-design-system`
  tokens, classes, and tag helpers, and implements `web-ux-conventions` N1-N8, A2,
  A3, A4, A6 for the chrome surface.

### Modified Capabilities

The route moves change observable paths that four established specs pin, so this
change carries a `MODIFIED` delta for each:

- `evidence-collector-register`: the register page moves to
  `/settings/evidence-collectors`.
- `attestation-template-register`: the register page moves to
  `/settings/attestation-templates`.
- `custom-role-designer`: the authoring page moves to `/settings/custom-roles`
  (the JSON API routes are unchanged).
- `admin-web-screens`: the user-management pages move to `/settings/users` and
  `/settings/usercredential` (files stay in the `/Admin` folder, so folder
  authorization and in-page enforcement are unchanged).

`user-admin` is not modified: it governs only the `/api/v1/freeboard/users` API
routes, which do not move. No other established spec pins a moved path.

## Non-goals

- The command palette and the object-detail drawer (N7 behavior, focus trap,
  Ctrl-K listener) are P4. The palette entry in the rail is a static
  non-functional affordance this phase (it carries the `Ctrl K` hint but does not
  open anything, and it sets no `aria-haspopup="dialog"` because no dialog exists
  until P4).
- Page-body migration to the compositions (Home dashboard, list pages, tabbed
  account pages) is P5. P3 only wraps the existing page bodies in the new shell
  and sets their nav `ViewData`. The one carve-out is the in-app link-target edits
  the route move forces: the literal `href` strings that pointed at a moved path are
  updated to the new `/settings` route (a necessary consequence of the clean-break move,
  not page-body composition), which stays out of scope until P5.
- Placeholder destination pages for unbuilt nav items are P6. P3 adds no new
  destination page (it moves existing pages under `/settings`, it does not create
  new ones); nav items that have no page are out of scope until P6, so the default
  nav map lists only destinations that exist today, each at its new route.
- Redirects from the old paths are NOT added. The IA re-home is applied as a clean
  break: the old URLs cease to exist. This is acceptable in pre-release software and
  keeps the routing surface small; if backward-compatible URLs are ever needed they
  are a later, separate change.
- Full N4 consolidation - merging the moved module-configuration page bodies into a
  single Settings page with per-module sections - is page-body work staged to a
  later phase. This change moves the routes under `/settings` and groups them, which
  meets N4 and N5 at the route/IA level; it does not merge the page bodies.
- System-default dark activation via `prefers-color-scheme` stays deferred
  (`web-design-system` staging); this change only adds the explicit
  light/dark setter.

## Impact

- MIT. All new code lives in `src/Freeboard` (the web app), the only component
  that already combines `Freeboard.Core` and `Freeboard.Enterprise`. EE-gated nav
  items call `IEnterpriseEntitlements` from within the web app; no MIT project
  gains an EE reference and the one-way EE dependency rule is unchanged.
- Rewrites `Pages/Shared/_Layout.cshtml` and restyles
  `Pages/Shared/_AuthLayout.cshtml`. Adds shell view components under
  `Pages/Shared/Components/` (`ShellRail`, `ShellTopbar`), the nav catalog and
  `ShellNavResolver` under `Navigation/`, the Alpine shell components in
  `assets/js/app.js`, and the shell chrome classes in `assets/css/app.css`.
  Restyles the `OrgSelector` view for the rail foot. Adds `NavGroup`/`Title` (and
  optional `NavItem`/detail) `ViewData` to existing authenticated pages, including
  Account/*.
- Moves the configuration and administration page routes under `/settings` by
  editing their `@page` directives (files stay in place, so folder authorization is
  unchanged) and updates the path-asserting web and E2E tests to the new paths. The
  preserved test markers (`temp-password`, `soa-nodes`, `data-node-id`,
  `btn-primary`, `badge`, `badge-danger`, `badge-success`) stay intact - only the
  asserted paths change. No redirects are added.
