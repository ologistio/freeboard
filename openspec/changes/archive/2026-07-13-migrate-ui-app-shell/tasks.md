## 1. Shell chrome styles (feat(web): shell chrome classes)

- [x] 1.1 Port the shell chrome classes from `stories/_marks.js` (`fb-app`,
  `fb-stage`, `fb-main`, `fb-rail`, `fb-brand`, `fb-mark`, `fb-name`, `fb-rev`,
  `fb-search-entry`, `fb-kbd`, `fb-navwrap`, `fb-navgroup`, `fb-navitem`,
  `fb-navcount`, `fb-rail-foot`, `fb-wspick`, `fb-topbar`, `fb-crumb`,
  `fb-topbar-right`, `fb-countdown`, `fb-iconbtn`, `fb-pip`, `fb-avatar`) into
  `assets/css/app.css @layer components`, replacing every literal color with a
  token and using no framework built-in color utility.
- [x] 1.2 Set interactive hit targets to at least 44px (A4) for the topbar icon
  buttons (`fb-iconbtn`), the avatar button (`fb-avatar`), the palette entry
  (`fb-search-entry`), and the nav items (`fb-navitem`), overriding the sub-44px
  sizes in the prototype story. The visual mark may stay smaller inside the 44px
  target.
- [x] 1.3 Add the mobile-drawer and reduced-motion rules for the rail (collapse
  below the desktop breakpoint; disable slide under `prefers-reduced-motion`).
- [x] 1.4 Add the active-item and focus-visible states (`aria-current` styling,
  visible focus ring) for `fb-navitem` and the topbar controls.
- [x] 1.5 Add the new bordered interactive controls (`fb-iconbtn`,
  `fb-search-entry`) to the resting-boundary class list in
  `ComponentLayerGuardTests.InteractiveControlsUseAThreeToOneBoundaryToken` so
  their resting border uses a >=3:1 token (A1). The existing no-literal-color and
  no-built-in-utility guards already scan the whole components layer, so the new
  classes need no new guard.
- [x] 1.6 Build assets (`bun run build`) and confirm the guard tests pass (no
  literal color, no built-in color utility, no built-in shadow in the new rules).

## 2. Nav catalog and resolver (feat(web): nav catalog)

- [x] 2.1 Add the nav catalog under `src/Freeboard/Navigation/`: `ShellNavGroup`
  (shared-noun label, ordered items) and `ShellNavItem` (stable key, label,
  current route, group, optional EE entitlement, optional authorization
  predicate), composed into a static `ShellNavCatalog`. The catalog is pure,
  declarative data - no per-request state.
- [x] 2.2 Populate the catalog with exactly the D11 pinned rows and no others, each
  at its NEW route: `home` (Home, top group-less, `/home`); `soa` (Statement of
  applicability, Comply, `/compliance/statement-of-applicability`); `vendors`
  (Vendors, Risk, `/compliance/vendors`); `evidence-collectors` (Evidence collectors,
  Platform, `/settings/evidence-collectors`); `attestation-templates` (Attestation
  templates, Platform, `/settings/attestation-templates`); `users` (Users, Platform,
  `/settings/users`, `CanReachAdmin`); `custom-roles` (Custom roles, Platform,
  `/settings/custom-roles`, `CustomPolicies` entitlement + `CanAdministerSystem`).
  Do NOT add a Role Assignments rail item (D12): its route moves and its test updates
  but it is not a live rail link this phase. Labels are sentence case (W1). No invented
  groupings and no placeholder destinations.
- [x] 2.3 Add `ShellNavResolver`, a request-scoped service that evaluates the
  catalog for the current request: drop items whose entitlement or authorization
  predicate fails (a dropped item is omitted entirely - no label, no href),
  resolve the active item (explicit `ViewData["NavItem"]` key first, else longest
  route match against the current path), and attach counts where a source exists
  (none this phase, so counts stay unset). Register it in DI.
- [x] 2.4 Add unit tests: N2 (each destination under exactly one group, no
  duplicate; every route is an existing route at its NEW `/settings` path where
  moved); the resolver gates the EE item by entitlement and omits its label/href when
  gated; the resolver marks exactly one active item and prefers the explicit key over
  route match; no item gets a badge because no count source exists; Role Assignments
  is absent from the catalog's live rail items.

## 3. Rail and topbar view components (feat(web): shell view components)

- [x] 3.1 Add `ShellRailViewComponent` (`Pages/Shared/Components/ShellRail/` +
  `Default.cshtml`) rendering the brand (edition tag empty/omitted until a real
  edition signal exists), the static command-palette entry with the `Ctrl K` hint
  (inert, no `aria-haspopup="dialog"`, keyboard-focusable and NOT `disabled` so it
  stays in the tab order per A2), the grouped nav with active state and actionable
  badges from the resolver, and the `OrgSelector` in the foot.
- [x] 3.2 Add `ShellTopbarViewComponent` (`Pages/Shared/Components/ShellTopbar/` +
  `Default.cshtml`) rendering the breadcrumb (group, page, optional detail from
  `ViewData`, each segment a link, degrading to title-only when a page declares
  nothing), the audit-countdown slot, the theme toggle, the notifications slot,
  and the account menu.
- [x] 3.3 Restyle the `OrgSelector` view (`Default.cshtml`, `_Node.cshtml`) to the
  `fb-wspick` shell classes for the rail foot; keep its `OrgSelectionResolver`
  read and `/org/select?return=...` behavior.
- [x] 3.4 Render the edition tag empty or omitted: there is no edition-name signal
  in the build (`EnterpriseEntitlement` names only the `CustomPolicies` feature, not
  an edition), so do NOT infer an edition from `CustomPolicies`. Render the audit
  countdown and notifications pip only when a real backing source exists (omit
  otherwise - no fabricated countdown text, no fake pip).
- [x] 3.5 Add render tests: rail marks the active item, gates the EE item by
  entitlement, and omits a badge for an item with no count source; breadcrumb
  renders linked segments and degrades safely; countdown/pip render empty with no
  backing data.

## 4. Rewrite the authenticated layout (feat(web): app shell layout)

- [x] 4.1 Rewrite `Pages/Shared/_Layout.cshtml` to the `fb-app` frame: the rail
  (`<vc:shell-rail />`), the topbar (`<vc:shell-topbar />`), and the scrolling
  `<main class="fb-main">@RenderBody()</main>`. The layout holds no inlined nav,
  authz, or entitlement logic - all of that lives in the resolver.
- [x] 4.2 Carry over the account menu (initials, display name, role badge,
  antiforgery logout form) into the topbar `fb-avatar` menu. Its dropdown links keep
  the existing tokenized `menu-item` class (a dropdown-item, a different job from the
  rail nav classes), so `menu-item` retains a live caller.
- [x] 4.3 Remove the legacy `nav-link` and `nav-section-title` classes from `app.css`
  once no caller remains (the `_Layout` rewrite and the `OrgSelector` restyle drop their
  last callers). Do NOT retire `menu-item`: the account menu carried into the topbar
  (4.2) still uses it, and it is already token-based.

## 5. Shell client behavior (feat(web): shell alpine components)

- [x] 5.1 Register Alpine components in `assets/js/app.js`: `themeToggle` (read
  `document.documentElement.dataset.theme`, toggle `light`/`dark`, write
  `localStorage` `fb-theme`, set `data-theme` on `<html>`; icon and `aria-pressed`
  reflect state), the rail drawer (open/close; while open trap focus or hold the
  background inert per A5; Escape closes and restores focus to the opener; slide
  honors reduced motion), and the account menu (Escape closes and restores focus).
  Add no new inline `<script>`.
- [x] 5.2 Add a test asserting the theme toggle writes the `fb-theme` key and
  `light`/`dark` values the pre-paint reader in `_Head.cshtml` parses, sets
  `data-theme`, and that no `prefers-color-scheme` activation block is present in
  the served CSS.

## 6. Apply the IA route moves (feat(web): re-home config and admin routes)

- [x] 6.1 Edit the `@page` directives to the new routes (files stay in their current
  folders, so `AuthorizeFolder` stays valid): `/compliance/evidence-collectors` ->
  `/settings/evidence-collectors`; `/compliance/attestation-templates` ->
  `/settings/attestation-templates`; `/admin/users` -> `/settings/users`;
  `/admin/usercredential` -> `/settings/usercredential`; `/admin/custom-roles` ->
  `/settings/custom-roles`; `/admin/custom-roles/designer/{slug?}` ->
  `/settings/custom-roles/designer/{slug?}`; `/admin/role-assignments` ->
  `/settings/role-assignments`. Add NO redirect from any old path.
- [x] 6.1a Update the literal link targets in page bodies that point at a moved route
  (the route move breaks these; there are no `asp-page` link-tag usages to update, and
  the one `RedirectToPage("/Admin/CustomRoles")` in `CustomRoleDesigner.cshtml.cs` is
  page-name-based and auto-resolves to the page's new route, so it needs no edit).
  These literal `href` strings DO need editing:
    - `src/Freeboard/Pages/Home.cshtml` - `href="/admin/users"` (dashboard card) ->
      `/settings/users`.
    - `src/Freeboard/Pages/Admin/CustomRoles.cshtml` - `href="/admin/custom-roles/designer"`
      (create button) and `href="/admin/custom-roles/designer/@slug"` (Edit link) ->
      `/settings/custom-roles/designer` and `/settings/custom-roles/designer/@slug`.
    - `src/Freeboard/Pages/Admin/CustomRoleDesigner.cshtml` - two
      `href="/admin/custom-roles"` (back link and Cancel) -> `/settings/custom-roles`.
    - `src/Freeboard/Pages/Admin/UserCredential.cshtml` - `href="/admin/users"`
      (back-to-users) -> `/settings/users`.
  The four `_Layout.cshtml` rail links to moved paths are NOT edited here: the rail is
  rewritten in 4.1 and its links come from the nav catalog (2.2) at the new routes.
- [x] 6.1b Update `TempPasswordDisplayStore.DisplayPath` from `/admin/usercredential`
  to `/settings/usercredential` in lockstep with the `UserCredential.cshtml` `@page`
  route. This one const is BOTH the create/reset redirect target AND the case-sensitive
  cookie `Path` scoping the one-time nonce cookie. If it does not match the new lowercase
  route the credential handoff 404s, or the browser withholds the path-scoped cookie so
  the display page reads no nonce and the temp password renders blank.
- [x] 6.2 Update the path-asserting web tests (`EvidenceCollectorsPageTests`,
  `AttestationTemplatesPageTests`, `AdminUserPagesTests`, `AdminPageAuthzTests`,
  `CustomRolesPageTests`, `CustomRoleDesignerPageTests`, `RoleAssignmentEndpointTests`)
  and E2E tests (`AdminUserPagesE2ETests`, `AccessibilityAuditE2ETests`) to the new
  paths. Preserve every marker (`temp-password`, `soa-nodes`, `data-node-id`,
  `btn-primary`, `badge`, `badge-danger`, `badge-success`) - change only the asserted
  path.
- [x] 6.3 Verify at least one moved page is reachable by following an in-app link
  (not only a direct `GotoAsync` to the new URL), so a stale `href` to an old path
  cannot ship silently. For example, drive the Custom Roles create/Edit link (6.1a) or
  the credential handoff redirect (6.1b) and assert the landing page loads at its new
  `/settings` route.

## 7. Page nav declarations (feat(web): page nav metadata)

- [x] 7.1 Set `ViewData["NavGroup"]` and `ViewData["Title"]` (and, where needed,
  `ViewData["NavItem"]`, `ViewData["BreadcrumbDetail"]`,
  `ViewData["BreadcrumbDetailHref"]`) on the authenticated application pages so the
  breadcrumb and active nav item resolve: Home (group-less top set, single page
  breadcrumb segment); Compliance/* (SOA -> Comply, Vendors -> Risk); the moved
  Settings pages -> Platform; Account/* -> Platform group with an Account breadcrumb,
  reached from the account menu. Do not change page-body composition (that is P5). The
  necessary in-app link-target updates forced by the route move (task 6.1a) are the one
  exception and are done there, not here.

## 8. Pre-auth layout (feat(web): tokenize auth layout)

- [x] 8.1 Restyle `Pages/Shared/_AuthLayout.cshtml` to the design tokens (no
  literal color, no built-in color utility; remove the hex-filled brand SVG). Leave
  the auth-funnel pages (`Login`, `ForgotPassword`, `ResetPassword`, `Account/Sudo`,
  `Account/CompleteReset`) on `_AuthLayout`; they do not enter the shell.

## 9. Verification

- [x] 9.1 `dotnet build` succeeds.
- [x] 9.2 `dotnet test` for the web tests passes: the moved pages answer at their new
  `/settings` routes, the old paths return no page, and the path-asserting tests plus
  the preserved markers (`temp-password`, `soa-nodes`, `data-node-id`, `btn-primary`,
  `badge`, `badge-danger`, `badge-success`) pass.
- [x] 9.3 Manual check: every authenticated application page renders in the shell in
  both themes with the correct active item and breadcrumb; the mobile drawer traps
  focus and restores it (A5); keyboard path, Escape, and focus restore work for the
  drawer and account menu; theme toggle persists across reload with no flash; all
  interactive controls are >=44px.
- [x] 9.4 `openspec validate "migrate-ui-app-shell" --strict` passes, including the
  four `MODIFIED` spec deltas (`evidence-collector-register`,
  `attestation-template-register`, `custom-role-designer`, `admin-web-screens`).
