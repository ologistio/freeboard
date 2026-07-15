## Context

Phase P3 shipped the app shell (nav rail, breadcrumb topbar, scrolling main, one server-side
nav source). Phase P4a shipped the command palette (N7) and, with it, the reusable
`overlayFocus` Alpine primitive in `src/Freeboard/assets/js/app.js`. That primitive owns the
overlay focus mechanics and only those: it captures the opener, moves focus in on open, holds
caller-named background nodes `inert` while open, closes on Escape while stopping that Escape
from propagating (so one keypress closes only the topmost overlay), and restores focus on close
to the opener when it is visible, otherwise to a caller-named visible fallback, otherwise to the
`main.fb-main` landmark. `commandPalette` composes it by spreading `...overlayFocus({ inert,
focus, fallback })` into its Alpine data and calling `enterOverlay(opener)` / `exitOverlay()`
around its own open/close. The palette mounts in `_Layout.cshtml` as a third sibling of
`.fb-rail` and `.fb-stage` under `.fb-app`, holds those two siblings inert while open, and stays
reachable because it is outside its own inert list. The rail opener (a different Alpine scope) is
coupled to the palette through an Alpine `palette` store. `app.css` already carries the token
foundation (both themes from one `@theme` set, dark only via `data-theme`, no
`prefers-color-scheme`), a global reduced-motion rule (A3), the `.fb-scrim` ink-mix dim, and the
`fb-eyebrow`/`fb-status`/`fb-seal`/`fb-tag`/`fb-stamp`/`fb-guidance` marks (P2). A
`ComponentLayerGuardTests` guard rejects literal color in `@layer components`; a
`ContrastGuardTests` guard checks token text pairs at the AA/AAA bar in both themes.

This change delivers the object-detail drawer (O4/A5) - the second and final slice of P4. The
reference design is `src/Freeboard/stories/Drawer.stories.js` (reference-only prototype with
literal hex and inline handlers; not copied verbatim). The wired surface is the Statement of
Applicability page, whose control objects match the story's control-detail anatomy.

## Goals / Non-Goals

**Goals:**

- One right-anchored ARIA dialog drawer (`role="dialog"`, `aria-modal="true"`, `aria-labelledby`
  the title) over a scrim, `inert` when closed, focus moving in on open and restored to the
  opener on close, Escape and scrim click closing - all focus mechanics reused from
  `overlayFocus`, not re-authored.
- One reusable `objectDrawer` Alpine component that composes `overlayFocus` the same way
  `commandPalette` does.
- One shared object-anatomy partial rendering the uniform O3 anatomy in the fixed order, reused
  by the drawer and by the full-page detail so they cannot diverge (O4).
- The Statement of Applicability control rows wired as drawer openers, with a direct link
  rendering the same anatomy full-page and a no-JavaScript navigation fallback (O4).
- Token-only styling, both themes, reduced-motion honored (A3), AA/AAA contrast both themes
  (A1/A6), dark gated to `data-theme` (no `prefers-color-scheme`).

**Non-Goals:**

- The full P5 restyle of the Statement of Applicability page (list composition, exceptions-first
  sort, chip filters, tree restyle). This is minimal drawer wiring only.
- Any other object type. Only the Statement of Applicability control is wired; P5 reuses this
  partial and drawer for the rest.
- New write actions on the read-only page; the drawer's actions region carries no mutating
  affordance.
- Enriching the control-detail data model. Facets the current projection does not carry render
  as explicit empties (O2).

## Decisions

### D1. Reuse `overlayFocus`, add `objectDrawer` and a `drawer` store

`objectDrawer` is a new Alpine data factory that spreads `...overlayFocus(...)` and layers only
drawer-specific state (which content is shown, open flag) on top, exactly as `commandPalette`
does. It supplies `overlayFocus` with `inert: [".fb-rail", ".fb-stage"]` (the same two shell
siblings the palette inerts, making rail, topbar, and main all unreachable), `focus` pointing at
the dialog panel (`tabindex="-1"`) so focus lands on the labelled dialog on open, and a `fallback`
of `main.fb-main` (the always-present landmark) for the case where the opener is no longer
visible. Open calls `enterOverlay(opener)` with the activating row anchor; close calls
`exitOverlay()`. Escape is bound to `overlayEscape($event)` and scrim click to `close()`, matching
the palette. The Escape-stop-propagation, inert, and restore behavior are inherited from the
primitive - no focus-trap, Escape, or inert code is written in `objectDrawer`. This satisfies the
`web-command-palette` "primitive is reused, not duplicated" scenario, which named the drawer as
the intended second consumer.

A `drawer` Alpine store couples the list openers (which live in the page's own Alpine scope) to
the single drawer instance (a `_Layout` sibling), mirroring the `palette` store. The store exposes
the open request and the currently-selected content source; the row opener calls it, and
`objectDrawer` registers its open handler on `init`. This keeps the two markup regions decoupled
(a monolithic `x-data` spanning the page rows and the shell-level drawer would be brittle), the
same trade-off resolved for the palette.

Alternative - a bespoke drawer focus implementation - rejected: it duplicates proven,
already-tested mechanics and violates code-as-liability; the primitive exists precisely for this.

### D2. Mount the drawer once in `_Layout`, as a `.fb-app` sibling

The drawer is a `<vc:object-drawer />` view component mounted by `_Layout.cshtml` as a sibling of
`.fb-rail` and `.fb-stage` under `.fb-app` - the same position the palette uses. This is required,
not incidental: the drawer holds `.fb-rail` and `.fb-stage` inert while open, so it must sit
outside both. Mounting inside `.fb-stage` (for example inside `.fb-main`) would make the drawer
inert itself when the stage is inerted. One shared instance is reused by every future list (P5),
so it belongs in the shell, not per page. `_Layout` gains one mount line and no grid change; this
is the minimal coherent shell touch and mirrors the palette exactly. The mount is empty until a
page's list wires an opener to the `drawer` store, so every other page is unaffected.

Alternative - render the drawer on the Statement of Applicability page only - rejected: a
page-level drawer inside `.fb-main` is inside the inerted stage; hoisting it out would reintroduce
the same sibling-of-`.fb-app` placement, i.e. `_Layout`. Rendering it globally now also avoids P5
having to move it later.

The focus "trap" is achieved by inerting every non-drawer region (`.fb-rail` and `.fb-stage`),
not by a JavaScript Tab-cycle handler. `overlayFocus` does not implement tab-cycling and does not
need to: `inert` removes the entire background subtree from the tab order and the accessibility
tree, so Tab from the last drawer control has nowhere outside the drawer to go and wraps within it.
Because the drawer is a sibling of the two inerted regions (not a descendant of `.fb-stage`), it is
never inerted itself - which is exactly the placement that resolves the "inerting the stage inerts
the drawer" hazard raised against a stage-descendant mount. The panel's `focus` selector points at
the `role="dialog"` panel (`tabindex="-1"`) so focus lands on the labelled dialog on open, and the
`fallback` is `main.fb-main`, the always-present shell landmark, for the case where the opener is no
longer visible on close - both correct for a `_Layout`-mounted drawer.

### D3. Shared anatomy is a Razor partial driven by a view model, not a tag helper

The uniform anatomy is `Pages/Shared/_ObjectDetail.cshtml`, a partial driven by one view model
(eyebrow, title, status, assertion, relations, evidence, guidance, history, actions). It is not a
tag helper. The repo's tag helpers (`StatusTagHelper`, `StampTagHelper`, `TagTagHelper`, etc.)
exist for atomic marks that carry an invariant or ARIA - a status seal encoding S2/S3, a
provenance stamp encoding P1/P2 - where a typed enum makes the invariant unbreakable at author
time. The anatomy is the opposite altitude: a multi-section layout that composes those marks. A
partial expresses the section structure and the O2 empty-facet rendering naturally, and it can
call `<fb-status>`, `<fb-stamp>`, and `<fb-tag>` inside for the marks. `web-design-system`
reserves tag helpers for invariant-carrying marks and CSS/partials for layout, so a partial is the
correct choice here. One partial, reused by the drawer template and the full-page detail,
guarantees O3/O4 parity by construction (there is no second markup to drift).

The one exception to byte-identical output is the actions slot, which is context-dependent. The
only honest action on this read-only surface is an "Open full page" link, which is meaningful in
the drawer but a circular self-link on the full page. The partial therefore takes the actions
content as a small parameter: the drawer passes the "Open full page" link; the full page passes no
self-link (optionally a "Back to Statement of Applicability" link, never a fabricated mutating
action). The O2 actions region is still present in both contexts. Accordingly the O3/O4 parity
guarantee - and the parity tests - cover the facet content from the eyebrow through history, not
the actions slot, which is expected to differ by context.

The view model is a general `ObjectDetailView` (not control-specific), so P5 can project other
object types into the same partial. It lives in `src/Freeboard` (MIT). Relations model O6's
two-way links; the Statement of Applicability projection fills them from the control's mapped
requirements ("Satisfies") and its configured checks ("Proving checks"), both kept inside the
single relations section to preserve O3's fixed five-body-section order.

### D4. The row carries the record to the drawer via a server-rendered inline template, no fetch

Each control row renders, adjacent to it, a hidden `<template>` containing the control's anatomy
(the same `_ObjectDetail` partial). On open, `objectDrawer` clones that template's content into the
drawer's content slot, then calls `enterOverlay(anchor)`. This is chosen over fetching the detail
on click for three reasons: (1) the `statement-of-applicability` capability requires the page's
disclosure to be server-rendered and forbids client script fetching additional data - an inline
template keeps the drawer content in the initial GET and consistent with that rule; (2) it needs
no new fetch endpoint and no CSP `connect-src` surface, and works under a strict CSP; (3) parity
with the full page is guaranteed because both render the one partial. `<template>` content is inert
and not laid out, so parking it is cheap; the SoA page already server-renders all four disclosure
levels hidden, so this matches the page's established grain.

The row's visible affordance is an anchor whose `href` is the control's full-page detail URL and
which carries `aria-haspopup="dialog"`. With JavaScript, `@click.prevent` opens the drawer from the
adjacent template. Without JavaScript, the anchor navigates to the full page (O4 reachability). The
existing disclosure toggle button and all existing row markers (`data-control-id`,
`data-check-id`, `data-node-id`, dispositions, resolutions) are left intact; the anchor is an
added affordance, not a replacement.

Alternative - fetch the detail fragment on click (progressive enhancement) - rejected here: it
adds a fetch path and CSP surface and sits awkwardly against the SoA "no client fetch of
disclosure data" rule, for no parity benefit over the shared partial.

### D5. Full-page control detail reuses the existing projection

`Pages/Compliance/ControlDetail.cshtml` (+ `.cs`) is the O4 direct-link and no-JS target. Its N8
breadcrumb is "Comply / Statement of Applicability / <control>": it sets `NavGroup = "Comply"`,
`NavItem = "soa"` (so the rail marks Statement of Applicability active on the detail page),
`BreadcrumbParent = "Statement of Applicability"` with `BreadcrumbParentHref` set to the SoA page
URL, and `Title` set to the control title. It does NOT set `BreadcrumbDetail`: the topbar appends
`Title` as its own leaf crumb, so `Title` supplies the leaf and a separate `BreadcrumbDetail` for
the same control would duplicate it. It identifies one control by the tuple already known at the row
(standard, organisation node, requirement, control id), re-runs the existing
`StatementOfApplicability.ResolveDrilldown` reads in the same store-unreachable try/catch, selects
the one control, and - inside that same try/catch - performs the same per-collector evidence-status
sidecar read (`GetCollectorEvidenceStatusesAsync`) for the requested org that the list page runs, so
the full page has the evaluated per-check statuses the list page has. It projects the control into
`ObjectDetailView` through the shared mapping helper, passing a
`CollectorStatus(org, requirement, collector)` resolver over that sidecar read, and renders
`_ObjectDetail` inside the shell. It reads the same data through the same store as the list page and
introduces no new store contract - so no `statement-of-applicability` or `compliance-web-read`
requirement changes. It is GET-only
(read-only middleware safe) and authenticated: it lives under `Pages/Compliance`, which is already
gated by `AuthorizeFolder("/Compliance", ...)` in `Program.cs`, so the new page inherits the same
authentication as the list page with no extra registration. To avoid duplicating the projection,
the control-to-`ObjectDetailView` mapping is one small helper both the list page (for the inline
templates) and the detail page call. The helper takes the `CollectorStatus(org, requirement,
collector)` resolver as an input parameter, so both callers feed the shared partial the same
per-check statuses and the drawer and full page cannot diverge on the proving-checks rows.

Authorization binds to the caller's full accessible organisation set, not the active list scope.
The detail page authorizes the requested control against `orgAccess.AccessibleOrgIdsAsync` - every
org the caller may see - NOT the active-scope-narrowed `OrgScope.InScopeIds` or the org-selection
cookie. A direct URL for any accessible org must render regardless of which org the active-scope
cookie currently selects; the cookie chooses what the list shows, not what the caller is entitled
to open by direct link. A control that is missing, or whose org lies outside the caller's
accessible set, yields a not-found response that discloses no record name or facet - a direct URL
cannot be used to probe for records the caller may not see. This closes the gap where a
valid-looking tuple for an unauthorized org would otherwise render its detail, without coupling
reachability to the transient scope selection.

URL shape (divergence, resolved). Plan A adopts a dedicated full-page detail route (a separate
Razor Page under `Pages/Compliance` reusing the shared partial), because O4 asks for a clean direct
link to the record full-page and a dedicated page reads better than a list-plus-detail mode toggle;
it also gives N8 a natural breadcrumb detail segment. The control's status resolves in the
org+requirement context, so the URL carries that tuple (standard, org node, requirement, control);
exact parameter names are an implementation detail that does not affect the observable contract.
The considered alternative (Plan B / Codex L1) was a query-string detail mode on the same SoA page
(`?...&detail=control&...`), which avoids adding a route but conflates list and detail state on one
URL and one page model. It is a reasonable P5 option and is recorded here as the rejected
alternative; P5 may revisit cleaner routes for all object types at once.

### D6. Styling: tokenized component classes, no hex, both themes, reduced motion

Port the prototype's `.fb-drawer` / `.fb-dhead` / `.fb-dbody` / `.fb-dfoot` / `.fb-dsec` /
`.fb-dl` / `.fb-dlist` / `.fb-guidance` / `.fb-xbtn` / `.fb-sheet` into `app.css @layer
components` against existing tokens (panel, line, line-strong, ink, muted, faint, brand-soft, the
`--shadow-lg` elevation, `--r`/`--r-lg` radii, `--font-sans`/`--font-mono`). Reuse the existing
`fb-eyebrow`, `fb-status`, `fb-seal`, `fb-tag`, `fb-stamp`, `fb-guidance` marks inside the anatomy.
No literal hex - the `ComponentLayerGuardTests` whole-block scan already covers the new classes.

Scrim (divergence, resolved - Codex M1). The drawer gets its own scrim under a distinct selector
(`.fb-dscrim`), not the existing `.fb-scrim`. `.fb-scrim` is owned by the mobile nav drawer
(railDrawer): it is `display:none` at the desktop breakpoint and only `display:block` inside the
`@media (max-width:1023px)` query, toggled by `railDrawer.hide()`. Reusing that class for the
object drawer would make the object drawer's scrim invisible above 1023px and entangle it with the
mobile nav's open state. The drawer instead reuses the ink-mix dim *value*
(`color-mix(in srgb, var(--color-ink) 28%, transparent)`) under its own class, so the visual is
identical but the mobile nav is untouched. Likewise the panel class is `.fb-drawer`, distinct from
the existing `.fb-drawer-toggle` (the mobile rail hamburger); the names are adjacent but the
selectors do not collide. A verification task asserts the mobile nav (railDrawer) still opens,
closes, and dims correctly after these additions.

The open/close is a real transform-plus-opacity slide with a non-zero duration, so the global
reduced-motion rule (A3) has something to zero - a bare display toggle would make the A3 guarantee
vacuous and untestable. The transition sits under the one global reduced-motion rule; no
per-component motion guard is added. Both themes render from the one token set with no new
`prefers-color-scheme` rule (dark stays `data-theme`-gated, matching P1-P4a). The anatomy text,
muted labels, and any brand-soft-tinted rows must clear the AA/AAA bar in both themes; the
`ContrastGuardTests` token-pair check covers the pairs used.

### D7. The status facet is honest: no fabricated pass (S6)

The anatomy's status facet must never render `Passing` for a control whose status is unknown. This
is a firm constraint, not a styling preference: S6 requires that nothing passes because its source
went quiet, and O2 requires an explicit empty over a fabricated value.

The data makes this simple, not speculative. `SoaControlNode.Evaluation` is the control's
check-COMBINE rule (all / any / manual) - metadata about how the control combines its checks, NOT
a pass/fail result. The drill-down projection carries no evaluated status at all - not at the
control level and not at the check level. The evaluated per-collector-check statuses (Unknown /
Stale / Passing / SoftFailure / HardFailure) come from a separate per-collector evidence-status read
(`GetCollectorEvidenceStatusesAsync`, surfaced through the `CollectorStatus(org, requirement,
collector)` resolver), which the Statement of Applicability page already runs and renders at the
check level. There is thus no control-level evaluated status anywhere, and synthesizing one
(aggregating per-check statuses under the combine rule) would be new logic that contradicts this
change's additive-over-existing-reads framing.

So for P4b the control-to-`ObjectDetailView` mapping does not derive a control-level `StatusKind`
at all: the anatomy's control-level status facet renders as an explicit O2 empty ("not evaluated").
The evaluated per-check statuses continue to render where the per-collector evidence-status read
provides them (the evidence / proving-checks relations rows), including the degraded-on-stale signal
at the per-check level - never synthesized up to the control. Both the list page and the full page
pass the same `CollectorStatus` resolver into the shared mapping helper, so the proving-checks rows
carry the same statuses in the drawer and the full page. This keeps the never-fabricate-a-pass honesty
guarantee (S6) while adding no aggregation logic, and keeps the drawer and full page honest by
construction since both read the one mapping. Enriching the control-level status with a real
roll-up is later product work, gated on the projection actually carrying such a result.

### D8. One top-level overlay at a time: the drawer suppresses the palette

The command palette (`ShellPalette`) is already mounted in `_Layout` as a sibling of `.fb-rail` /
`.fb-stage`, and its document-level Ctrl-K / `/` listener in `app.js` guards only whether the
palette itself is open. It does not know about the drawer. Left as-is, Ctrl-K could open the
palette on top of an open drawer, and the palette inerts only `.fb-rail` + `.fb-stage` - not the
drawer, the fourth `.fb-app` sibling - so the drawer would stay focusable behind the palette. (The
passive case is already sound: a closed palette is `visibility:hidden`, so the drawer's inert of
rail + stage traps focus correctly.)

Policy: only one top-level overlay is active at a time. While the object drawer is open, the
palette's open path is suppressed - both its Ctrl-K / `/` shortcut and its store `open()` - so the
drawer stays the single top-level overlay until it closes. The smallest coupling mirrors the
existing store pattern: the palette `open()` also checks the `drawer` store's open flag and returns
early when the drawer is open, the same way the drawer opener and palette opener already read each
other's stores. No new coordinator abstraction is introduced.

Alternative - a general overlay-stack manager - rejected: two overlays that must not co-exist do
not justify a stack; a one-line guard reading the sibling store is the lower-liability fit.

### Provenance and divergence resolution (Plan A / Plan B)

Two independent plans fed this design. Plan A is this change's original artifacts; Plan B is
Codex's independent plan. Where they agreed the design keeps the shared decision; where they
diverged the resolution is recorded on the decision it touches.

- Shared by both: compose `overlayFocus` rather than re-author focus mechanics (Plan B H1, Plan A
  D1); a shared Razor anatomy partial driven by a view model, not a tag helper (Plan B, Plan A D3);
  server-rendered `<template>` cloned on open with no fetch and no client-built HTML from JSON, for
  XSS safety and O4 parity (Plan B H3, Plan A D4); token-only styling with `color-mix`, dark gated
  to `data-theme` only with no `prefers-color-scheme` rule (Plan B M4, Plan A D6).
- Divergence - drawer mount location: Plan B proposed mounting the drawer inside the SoA page and
  wrapping the list in a `#soa-drawer-background` so the page background (not `.fb-stage`) is
  inerted, to avoid inerting a stage-descendant drawer (Plan B H2). Resolved in favor of Plan A:
  mount one generic drawer once in `_Layout` as a sibling of `.fb-rail`/`.fb-stage`, driven by a
  `drawer` store, mirroring the shipped palette. Because the drawer is a sibling of `.fb-stage`
  (not a descendant), inerting `.fb-rail` + `.fb-stage` inerts all background chrome without
  inerting the dialog - so H2's hazard does not arise and no `#soa-drawer-background` wrapper is
  needed. This is more reusable for P5 and matches the palette pattern. See D2.
- Divergence - full-page detail URL: Plan A's dedicated route vs Plan B/Codex L1's query-string
  detail mode on the SoA page. Resolved in favor of Plan A's dedicated route; the query-string
  alternative is recorded in D5 as the considered option for P5.
- Folded in from Plan B: honest status mapping (Codex M2 / S6, D7 above); accessible-org
  authorization bound on the direct link, not just active list scope (Codex M3, D5); a
  drawer-scoped scrim distinct from the mobile nav's `.fb-scrim` so railDrawer is not broken
  (Codex M1, D6); and the fuller behavioral verification list (Tab-stays-inside via inert,
  background inert releases on close, direct-URL section-order parity, axe in both themes,
  reduced-motion disabling both the slide and any close delay, CSS literal-color and built-in
  utility guards).

## ARIA dialog and focus contract (exact behavior)

- The drawer panel is `role="dialog"`, `aria-modal="true"`, `aria-labelledby="fb-detail-title"`
  (the anatomy title element's stable id), `tabindex="-1"`.
- Closed: the panel carries `inert` (bound `:inert="!open || null"`) and is translated off-screen
  by CSS; its (cloned) controls are out of the tab order and off the accessibility tree.
- Open: `objectDrawer.open(anchor)` clones the row's template into the content slot, sets the
  open flag (removing `inert`, running the slide), and calls `enterOverlay(anchor)`. The primitive
  holds `.fb-rail` and `.fb-stage` inert and moves focus to the `focus` target (the dialog panel).
- Escape: bound to `overlayEscape($event)` on the drawer root; the primitive stops propagation and
  preventDefault, then calls `close()`. So one Escape closes only the drawer.
- Scrim click: the scrim (or the dialog root outside the panel) `@click.self="close()"`.
- Close: `objectDrawer.close()` clears the open flag (re-applying `inert`, running the reverse
  slide) and calls `exitOverlay()`, which releases the inert it applied and restores focus to the
  captured anchor when it is visible, otherwise to `main.fb-main`.
- Reduced motion: the slide has a real duration; the global `@media (prefers-reduced-motion:
  reduce)` rule zeroes transition/animation durations, and the close visibility delay (if any) is
  dropped so the drawer hides at once - the pattern the palette and mobile rail already use.
- Dark: no `prefers-color-scheme` rule is emitted; dark is reached only through the persisted
  `data-theme` override.

## File changes (respecting the reference graph)

All changes are in `src/Freeboard` (MIT web UI) and its test projects. Nothing touches
`Freeboard.Enterprise`, `Freeboard.Core`, `Freeboard.Agent`, or `Freeboard.CLI`; the reference
graph is unchanged and no EE dependency is introduced. Agent and CLI stay cross-platform and
EE-free.

- `assets/js/app.js`: add `objectDrawer` (composing `overlayFocus`) and the `drawer` store.
- `assets/css/app.css` (`@layer components`): add the drawer and anatomy classes, tokens only.
- `Pages/Shared/Components/ObjectDrawer/ObjectDrawerViewComponent.cs` + `Default.cshtml`: the
  drawer shell (scrim, `role="dialog"` panel, close button, content slot).
- `Pages/Shared/_ObjectDetail.cshtml` + `ObjectDetailView` view model: the shared anatomy.
- `Pages/Shared/_Layout.cshtml`: mount `<vc:object-drawer />` as a `.fb-app` sibling.
- `Pages/Compliance/StatementOfApplicability.cshtml`: control rows become drawer openers with an
  adjacent hidden anatomy template; existing markers preserved.
- `Pages/Compliance/ControlDetail.cshtml` + `.cs`: the full-page control detail, reusing the
  drill-down projection and the shared partial.

## Risks / Trade-offs

- [Accessibility-critical focus/keyboard code] -> Reuse the proven, tested `overlayFocus`
  primitive rather than new trap code; cover with an axe audit in both themes plus a
  keyboard/focus-restore/Escape E2E through Playwright/CDP, as the palette does.
- [Inline anatomy templates add DOM weight per control] -> Use `<template>` (inert, not laid out)
  and render only for control-level rows; consistent with the page already server-rendering all
  disclosure levels hidden. If weight becomes a problem later, the fetch-fragment path is the
  documented fallback.
- [Drawer content and full page could drift] -> Mitigated by construction: one shared partial
  renders both; there is no second markup to drift.
- [Shared Alpine scope across the page rows and the shell-level drawer] -> A `drawer` store keeps
  the regions decoupled, the same resolution the palette used.
- [Name confusion between `.fb-drawer` and `.fb-drawer-toggle`] -> Documented; distinct selectors,
  no collision.
- [Wiring could regress Statement of Applicability behavior] -> The drill-down, scoping,
  authorization, read-only, and store-unreachable paths are untouched; the added anchor and
  template are additive, and the existing markers and E2E assertions are kept.

## Migration Plan

Additive and reversible. The drawer is new behavior; the control rows gain an anchor that, with JS
off, navigates to a new full-page detail (also new). No data migration, no API change, no route
removal. Rollback is reverting the change - the rows return to plain text and the drawer mount is
gone. Ship after P4a (which provides `overlayFocus`).

## Archiver note

The new `web-object-drawer` requirement "The Statement of Applicability list opens its control
objects in the drawer" co-describes SoA control rows that the `statement-of-applicability`
capability also covers. Its drill-down scenarios stay satisfied - the disclosure toggle and the
existing row markers are preserved - so this adds a presentation contract over those rows without
modifying the SoA capability.

## Open Questions / unresolved tensions for review

Resolved by this synthesis (see the cited decision):

- Status derivation: the drill-down projection carries no control-level evaluated status
  (`SoaControlNode.Evaluation` is the check-COMBINE rule, not a result), so the control-level status
  facet renders as an explicit O2 empty ("not evaluated"); evaluated per-check statuses come from the
  separate per-collector evidence-status read (`GetCollectorEvidenceStatusesAsync` / `CollectorStatus`)
  and render in the proving-checks rows, degraded-on-stale at the per-check level. No control-level
  aggregation is synthesized, never a fabricated pass (D7).
- Full-page URL shape: dedicated route, with the query-string mode recorded as the P5 alternative
  (D5).
- Authorization: bounded by accessible orgs, not just active scope; missing/inaccessible renders
  not-found without leaking names (D5).
- Scrim collision: drawer-scoped `.fb-dscrim`, not the mobile nav's `.fb-scrim` (D6).
- Actions region on a read-only page: the actions slot is context-dependent (a partial parameter).
  The drawer renders the "Open full page" link; the full page omits that self-link and keeps the
  O2 actions region present without a fabricated mutating action. Parity covers the facet content,
  not the actions slot (D3, D4).

Left for reviewers to examine:

- URL shape durability: the dedicated route carries the full org+requirement+control tuple as
  parameters. Reviewers should confirm the chosen parameter names and route template read cleanly
  and that P5's eventual multi-object route scheme can absorb this page without a breaking URL
  change.
