# web-app-shell Specification

## Purpose
TBD - created by archiving change migrate-ui-app-shell. Update Purpose after archive.
## Requirements
### Requirement: Authenticated application pages render inside the app shell

The web UI (`src/Freeboard`) SHALL render every authenticated application page
inside one app shell composed of a nav rail on the left, a breadcrumb topbar, and a
scrolling main region that hosts the page body, matching the `appFrame` composition
in `stories/_marks.js`. The shell SHALL be the shared authenticated layout
(`Pages/Shared/_Layout.cshtml`), which is the default layout, so an application page
that does not override its layout renders inside the shell. The personal Account
pages (`Account/Index`, `Account/Mfa/*`, `Account/Password/*`, `Account/Sessions`)
are application pages and SHALL render inside the shell, reached from the account
menu and carrying a Platform then Account breadcrumb.

The pre-authentication and auth-funnel pages SHALL NOT use the shell: `Login`,
`ForgotPassword`, `ResetPassword`, `Account/Sudo`, and `Account/CompleteReset` keep
the pre-auth layout (`_AuthLayout.cshtml`), because they are steps in the
authentication funnel, not pages of the authenticated application. This implements
`web-ux-conventions` N1 and A2 for the chrome surface.

#### Scenario: Authenticated application page is wrapped by the shell

- **WHEN** an authenticated application page that uses the default layout is
  requested
- **THEN** its body renders inside the scrolling main of the shell, with the nav
  rail and the breadcrumb topbar present

#### Scenario: Personal account page renders in the shell

- **WHEN** a personal Account page (for example `Account/Index` or `Account/Mfa`) is
  requested
- **THEN** it renders inside the shell with a Platform then Account breadcrumb,
  reached from the account menu rather than a primary rail item

#### Scenario: Auth-funnel pages stay on the pre-auth layout

- **WHEN** `Login`, `ForgotPassword`, `ResetPassword`, `Account/Sudo`, or
  `Account/CompleteReset` is requested
- **THEN** it renders on the pre-auth layout (`_AuthLayout.cshtml`), not inside the
  shell

#### Scenario: Main scrolls independently of the chrome

- **WHEN** a page taller than the viewport renders in the shell
- **THEN** the main region scrolls while the nav rail and the topbar stay fixed

### Requirement: The nav rail is grouped, two-level, and driven by one nav map

The nav rail SHALL present a brand mark, a command-palette entry, grouped
navigation, and a workspace switcher pinned to the foot. An edition tag SHALL render
next to the brand mark only when a real edition signal exists; the current build has
no edition-name signal (the `EnterpriseEntitlement` enum names a single feature
entitlement, `CustomPolicies`, not an edition), so the edition tag SHALL render empty
or be omitted rather than inferring an edition from a feature entitlement.
Navigation SHALL be exactly two levels - group then page - with every destination
under exactly one group and no page appearing twice, and group labels SHALL be
shared jobs or nouns, not internal module or product-tier names. The rail, the
breadcrumb, and the future command-palette index SHALL be driven by one server-side
nav map data structure, so they cannot disagree. This implements
`web-ux-conventions` N1, N2, and N3.

#### Scenario: Rail structure matches the composition

- **WHEN** the nav rail renders
- **THEN** it shows the brand mark, a command-palette entry, the grouped nav items,
  and the workspace switcher at the foot, with the edition tag empty or omitted while
  no edition signal exists

#### Scenario: Every destination sits under one group at two levels

- **WHEN** the nav map is built
- **THEN** each destination is a page under exactly one group, no page appears
  twice, and no nav control descends below group then page

#### Scenario: One nav map is the shared source

- **WHEN** the rail and the breadcrumb are rendered for the same request
- **THEN** both derive from the one nav map, so an item's group, label, and route
  agree across surfaces

### Requirement: Shell surfaces are authorization-aware view components

The nav rail and the topbar SHALL be server-rendered view components that read
request data through a request-scoped nav resolver, following the existing
`OrgSelector` view component pattern; this covers the breadcrumb, theme toggle, and
the data-driven countdown and notification slots. The shared layout
(`_Layout.cshtml`) SHALL hold only the shell frame and the page body, with no
inlined navigation, authorization, or entitlement logic. Nav items that surface enterprise or admin
features SHALL be shown only when the viewer holds the required
`Freeboard.Enterprise` entitlement and passes the item's authorization predicate;
this gating SHALL run in the request-scoped resolver, not in view markup, and a
gated item SHALL be omitted entirely, emitting neither a label nor an `href`. The
entitlement and authorization checks SHALL run in the web app (`src/Freeboard`),
which is the only component that references `Freeboard.Enterprise`. The workspace
switcher SHALL reuse the existing `OrgSelector` view component in the rail foot.

#### Scenario: Enterprise nav item is gated by entitlement

- **WHEN** the rail renders for a viewer who lacks the entitlement an item
  requires
- **THEN** that item is omitted with no rendered label or `href`, and it is shown
  only when the entitlement and the item's authorization predicate both pass

#### Scenario: Workspace switcher reuses the org selector

- **WHEN** the rail foot renders
- **THEN** it renders the existing `OrgSelector` view component as the workspace
  switcher, not a re-implemented org tree

### Requirement: Active nav item and breadcrumb come from page-declared ViewData

Each page SHALL declare its navigation group and title through
`ViewData["NavGroup"]` and `ViewData["Title"]`, with an optional explicit nav-item
key through `ViewData["NavItem"]` and an optional detail through
`ViewData["BreadcrumbDetail"]` and `ViewData["BreadcrumbDetailHref"]`. The shell
SHALL mark exactly one matching rail item active (`aria-current="page"`), resolving
the active item by the explicit `NavItem` key when declared and otherwise by the
longest matching route against the current request path - not by the display
title. The shell SHALL render the breadcrumb as group then page then detail, each
segment a working link: the group segment links to the group's primary
destination, the page segment to the page's route, and the detail segment to
`BreadcrumbDetailHref` (or the current URL). A page in the group-less top set (Home,
My work) SHALL render a single page segment with no group segment, because the top
set is the app root rather than a navigable group; this still satisfies N8, whose
group-then-page-then-detail order names only the levels that exist for the page. A
page that declares neither group nor title SHALL still render, showing a breadcrumb
of just its title and no active nav item. This implements `web-ux-conventions` N8.

#### Scenario: Breadcrumb states group, page, detail as links

- **WHEN** a page that declares its group and title renders
- **THEN** the breadcrumb shows group then page (then detail when declared), and
  each segment is a working link

#### Scenario: Group-less top page shows a single page segment

- **WHEN** a group-less top page (Home or My work) renders
- **THEN** the breadcrumb shows a single page segment with no group segment, as a
  working link

#### Scenario: Active nav item reflects the current page

- **WHEN** a page declaring a group and title renders in the shell
- **THEN** the shell marks exactly one rail item active with `aria-current="page"`,
  chosen by the explicit `NavItem` key when declared and otherwise by longest route
  match, and no other item is active

#### Scenario: Undeclared page degrades safely

- **WHEN** a page declares neither group nor title
- **THEN** the shell renders with a breadcrumb of just the page title and no
  active nav item, rather than failing

### Requirement: Nav badges show only actionable counts and omit absent data

A nav item SHALL show a badge only when its count is actionable - failing, or
waiting on the viewer - and passing or informational totals SHALL NOT badge. A
badge SHALL render only from a real server-side count source; an item with no such
source SHALL render with no badge rather than a fabricated or zero one. This
implements `web-ux-conventions` N6.

#### Scenario: Passing or informational counts do not badge

- **WHEN** a nav item's count is passing or informational
- **THEN** no badge is shown

#### Scenario: Item without a count source shows no badge

- **WHEN** a nav item has no backing count source
- **THEN** the item renders with no badge

### Requirement: The command-palette entry is a static affordance this phase

The nav rail SHALL show a single command-palette entry carrying the `Ctrl K` hint,
and it SHALL be the only global search or ask entry point in the chrome. In this
change the entry SHALL be a non-functional affordance: it does not open a palette,
no second search box appears elsewhere in the chrome, and it SHALL NOT advertise a
dialog it cannot open - it SHALL NOT set `aria-haspopup="dialog"` and SHALL present
as visibly non-operative this phase. The entry SHALL remain keyboard-focusable - a
non-activating button or link that stays in the tab order - and SHALL NOT be a
`disabled` control, because a disabled control is not focusable and would break the
full keyboard path (A2). The palette behavior is delivered later. This implements
`web-ux-conventions` N7 for the chrome's single search surface, keeping A2.

#### Scenario: Single palette entry with the shortcut hint

- **WHEN** the chrome renders
- **THEN** exactly one command-palette entry is present, it carries the `Ctrl K`
  hint, and no other global search or ask box appears

#### Scenario: Entry is inert this phase

- **WHEN** the viewer activates the command-palette entry in this change
- **THEN** no palette opens, because palette behavior is out of scope here, the
  entry exposes no `aria-haspopup="dialog"` promise, and it stays keyboard-focusable
  (not a `disabled` control) so the tab order is unbroken

### Requirement: The topbar carries the theme toggle that writes the personal preference

The breadcrumb topbar SHALL include a theme toggle that switches the personal
theme by writing the `fb-theme` preference (`light` or `dark`) to `localStorage`
and setting `data-theme` on the `<html>` element - the same key and attribute the
pre-paint reader established in `web-design-system` consumes. The toggle's icon
SHALL reflect the current theme and its accessible state SHALL be exposed. Any
toggle transition SHALL be disabled under a reduced-motion preference. The toggle
SHALL write only an explicit `light` or `dark` value and SHALL NOT introduce
`prefers-color-scheme` activation, so system-default dark stays gated. This
implements `web-ux-conventions` A3 and the personal-setting part of A6, and wires
the setter side of the `web-design-system` theming mechanism.

#### Scenario: Toggle writes the preference the reader consumes

- **WHEN** the viewer activates the theme toggle
- **THEN** `fb-theme` is written as `light` or `dark` and `data-theme` is set on
  `<html>`, matching the key and values the pre-paint reader parses

#### Scenario: Icon reflects the current theme

- **WHEN** the theme is light or dark
- **THEN** the toggle icon shows the state and exposes its accessible state

#### Scenario: Reduced motion disables the toggle transition

- **WHEN** the viewer prefers reduced motion
- **THEN** any theme-toggle transition is disabled

#### Scenario: Toggle does not enable system-default dark

- **WHEN** the theme toggle is used
- **THEN** it writes only an explicit `light` or `dark` value and no
  `prefers-color-scheme` activation is added, so an unset preference still renders
  light

### Requirement: The rail collapses to a mobile drawer with a full keyboard path

Below the desktop breakpoint the nav rail SHALL collapse to a drawer opened from a
topbar control, reusing the existing Alpine drawer pattern. While the drawer is open
it SHALL trap focus (or hold the background inert), close on Escape, and restore
focus to the opening control on close, so keyboard focus cannot land on the
obscured page behind it. The full chrome - rail or drawer, palette entry,
breadcrumb, topbar controls, and account menu - SHALL be reachable by keyboard with
focus always visible, and the drawer slide SHALL honor a reduced-motion preference.
This implements `web-ux-conventions` A2, A3, and A5.

#### Scenario: Rail becomes a drawer on small screens

- **WHEN** the viewport is below the desktop breakpoint
- **THEN** the rail collapses to a drawer opened from a topbar control

#### Scenario: Open drawer traps focus and restores it on close

- **WHEN** the mobile nav drawer is open and the viewer presses Escape or closes it
- **THEN** focus was trapped inside the drawer (or the background was inert) while it
  was open, Escape closes it, and focus returns to the control that opened it

#### Scenario: Chrome is fully keyboard reachable with visible focus

- **WHEN** the viewer navigates the chrome by keyboard only
- **THEN** the rail or drawer, palette entry, breadcrumb, topbar controls, and
  account menu are all reachable with visible focus

### Requirement: Shell chrome classes are tokenized and meet AA in both themes

The shell chrome component classes SHALL be defined in `app.css @layer components`
from design tokens only. This covers the rail, brand, palette entry, nav group and
item, badge, workspace switcher, topbar, breadcrumb, countdown, icon buttons, pip,
and avatar classes. No literal color value and no framework built-in color utility
SHALL appear in any of their rule bodies, and each SHALL meet WCAG AA contrast in
both the light and the dark theme. Legacy chrome classes that lose their last caller when the shell is
rewritten SHALL be removed, so one class serves each visual job. This implements
`web-ux-conventions` A1 and A6 and conforms to `web-design-system`.

#### Scenario: No literal color in the shell chrome classes

- **WHEN** the shell chrome rules in `app.css @layer components` are scanned
- **THEN** they contain no literal color value and no framework built-in color
  utility; each color is a token reference

#### Scenario: Chrome renders AA in both themes

- **WHEN** the shell renders in light and in dark
- **THEN** its chrome takes its colors from the tokens and meets WCAG AA contrast
  in both

#### Scenario: Superseded legacy chrome classes are removed

- **WHEN** the shell rewrite removes the last caller of a legacy chrome class
- **THEN** that class is removed from the components layer, leaving one class per
  visual job

### Requirement: Shell controls meet the hit-target size

Every interactive control in the shell chrome outside a dense table SHALL present a
hit target of at least 44px in both dimensions, taking precedence over any smaller
dimension in the reference story. This covers the topbar icon buttons, the
avatar/account button, the command-palette entry, the theme toggle, the drawer
control, and the nav items. A smaller visual mark MAY sit inside the 44px target.
This implements `web-ux-conventions` A4.

#### Scenario: Topbar and nav controls are at least 44px

- **WHEN** the shell chrome renders its interactive controls outside a dense table
- **THEN** each control's hit target is at least 44px in both dimensions

### Requirement: The pre-auth layout is tokenized

The pre-authentication layout (`Pages/Shared/_AuthLayout.cshtml`) SHALL be
restyled to the design tokens, taking its colors from tokens rather than literal
values or framework built-in color utilities, so it themes consistently with the
rest of the UI. This implements `web-ux-conventions` A6 for the pre-auth surface.

#### Scenario: Pre-auth surface uses tokens

- **WHEN** a pre-authentication page (for example login) renders
- **THEN** its layout takes its colors from the design tokens

### Requirement: The shell applies the information-architecture route moves as a clean break

Landing the shell SHALL apply the information-architecture re-home that moves
configuration and administration pages under `/settings`, and the nav map SHALL
point at each destination's new route. The moves are: evidence collectors
`/compliance/evidence-collectors` to `/settings/evidence-collectors`; attestation
templates `/compliance/attestation-templates` to `/settings/attestation-templates`;
users `/admin/users` to `/settings/users` (and the one-time credential display
`/admin/usercredential` to `/settings/usercredential`); custom roles
`/admin/custom-roles` to `/settings/custom-roles` (and the role editor
`/admin/custom-roles/designer/{slug?}` to
`/settings/custom-roles/designer/{slug?}`); and role assignments
`/admin/role-assignments` to `/settings/role-assignments`. No module reporting page
exists today, so no page moves under `/reports`.

The move SHALL change only each page's route URL through its `@page` directive; the
page files SHALL stay in their current Razor Pages folders (`Pages/Compliance`,
`Pages/Admin`), so the existing folder authorization conventions
(`AuthorizeFolder("/Compliance")`, `AuthorizeFolder("/Admin")`) still gate them and
in-page enforcement is unchanged. No redirect from an old path SHALL be added: the
prior URLs cease to exist (a deliberate clean break, acceptable in pre-release
software). The path-asserting web and end-to-end tests SHALL be updated to the new
paths, and the preserved test markers (`temp-password`, `soa-nodes`,
`data-node-id`, `btn-primary`, `badge`, `badge-danger`, `badge-success`) SHALL
remain intact - the asserted path changes, the markers do not.

Moving these pages under `/settings` satisfies N4 and N5 at the route and
information-architecture level: module configuration and register pages no longer
hang off a feature's own nav entry, and no module report page exists. Full N4
consolidation into a single Settings page with per-module sections is page-body work
staged to a later phase; this change moves the routes and groups them, it does not
merge the page bodies into one Settings page.

#### Scenario: Moved pages answer at their new routes with no redirect

- **WHEN** the shell change is applied
- **THEN** each moved page answers at its new `/settings` route, the nav map links to
  the new route, and the old path returns no page and no redirect

#### Scenario: Path-asserting tests and markers stay green

- **WHEN** the web and end-to-end test suites run after the change
- **THEN** they pass with their path assertions updated to the new `/settings`
  routes, and the preserved markers are still present in the rendered markup

