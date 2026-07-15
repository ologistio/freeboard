## 1. Tokenized drawer and anatomy styles (feat(web): drawer component classes)

- [x] 1.1 Add `.fb-drawer` (right-anchored panel, `--color-panel`, left border `--color-line`,
      `--shadow-lg`, off-screen transform when closed, slide-in when open) to `app.css @layer
      components`, tokens only. Confirm no collision with the existing `.fb-drawer-toggle`.
- [x] 1.2 Add the anatomy classes `.fb-dhead`, `.fb-dbody`, `.fb-dfoot`, `.fb-dsec`, `.fb-dl`,
      `.fb-dlist`, `.fb-xbtn`, and `.fb-sheet` (full-page container), tokens only; reuse the
      `fb-eyebrow`, `fb-guidance`, `fb-status`, `fb-seal`, `fb-tag`, `fb-stamp` marks rather than
      re-authoring them. Add a drawer-scoped scrim under a distinct class (`.fb-dscrim`) reusing
      the ink-mix dim value - do NOT reuse the mobile nav's `.fb-scrim` (it is `display:none` at
      desktop and owned by railDrawer).
- [x] 1.3 Give the open/close a real transform+opacity transition with a non-zero duration so the
      global reduced-motion rule (A3) has something to zero; drop any close visibility delay under
      `prefers-reduced-motion`. Emit no `prefers-color-scheme` rule (dark stays `data-theme`-gated).
- [x] 1.4 Verify: `dotnet build` runs the bun asset build with 0 warnings and the served
      `/css/app.css` carries the new classes; `ComponentLayerGuardTests` (no literal color) and
      `ContrastGuardTests` (token pairs, both themes) pass.

## 2. Reusable drawer component in app.js (feat(web): objectDrawer Alpine component)

- [x] 2.1 Add an `objectDrawer` Alpine component that spreads `...overlayFocus({ inert:
      [".fb-rail", ".fb-stage"], focus: <dialog panel selector>, fallback: "main.fb-main" })` and
      layers only open flag plus selected-content state on top. Do not re-author focus-trap,
      Escape, or inert mechanics.
- [x] 2.2 Implement `open(opener)` (clone the opener's anatomy template into the content slot, set
      the open flag, call `enterOverlay(opener)`) and `close()` (clear the flag, call
      `exitOverlay()`); bind Escape to `overlayEscape($event)` and scrim to `close()`.
- [x] 2.3 Add a `drawer` Alpine store coupling the list openers to the single drawer instance,
      mirroring the `palette` store; register the open handler in `objectDrawer.init`.
- [x] 2.4 Enforce one top-level overlay at a time: while the drawer is open, suppress the command
      palette's open path. In the palette `open()` (and its Ctrl-K / `/` shortcut path), read the
      `drawer` store's open flag and return early when the drawer is open, mirroring the existing
      cross-store pattern - so Ctrl-K cannot stack the palette over an open drawer.
- [x] 2.5 Verify: served `/js/app.js` contains the drawer + store markers (string assertion, as the
      palette bundle test does).

## 3. Shared anatomy partial and drawer shell (feat(web): object-detail anatomy and drawer shell)

- [x] 3.1 Add an `ObjectDetailView` view model (eyebrow, title, status, assertion, relations,
      evidence, guidance, history, actions) in `src/Freeboard`, general enough for P5 reuse.
- [x] 3.2 Add `Pages/Shared/_ObjectDetail.cshtml` rendering the uniform anatomy in the fixed O3
      order with the title carrying a stable id (`fb-detail-title`); render absent facets as
      explicit empties (O2); compose the `<fb-status>`/`<fb-stamp>`/`<fb-tag>` marks.
- [x] 3.3 Add `Pages/Shared/Components/ObjectDrawer/ObjectDrawerViewComponent.cs` + `Default.cshtml`
      (scrim, `role="dialog"` `aria-modal="true"` `aria-labelledby="fb-detail-title"` panel with
      `tabindex="-1"` and `:inert`, close button, content slot, `x-data="objectDrawer"`).
- [x] 3.4 Mount `<vc:object-drawer />` in `Pages/Shared/_Layout.cshtml` as a sibling of `.fb-rail`
      and `.fb-stage` under `.fb-app`; no grid change.
- [x] 3.5 Verify: `dotnet build` 0 warnings; a render test asserts the drawer dialog ARIA and that
      the closed drawer is `inert`.

## 4. Full-page control detail (feat(web): full-page control detail for O4 parity)

- [x] 4.1 Add a control-to-`ObjectDetailView` mapping helper reused by the list page and the detail
      page (relations from mapped requirements plus configured checks, kept in the relations
      section; empties for facets the projection does not carry). The helper takes a
      `CollectorStatus(org, requirement, collector)` status resolver as an input parameter so the
      inline templates and the full page feed it the same per-check statuses and cannot diverge. Map
      the status honestly (S6): the drill-down projection carries no control-level evaluated status
      (`SoaControlNode.Evaluation` is the check-COMBINE rule, not a result), so render the
      control-level status facet as an explicit O2 empty ("not evaluated") - do NOT synthesize a
      control-level roll-up. The evaluated per-check statuses (including degraded-on-stale) are not
      in the drill-down projection either; they come from the separate per-collector evidence-status
      read (`GetCollectorEvidenceStatusesAsync` surfaced via the `CollectorStatus` resolver). Render
      them only in the evidence / proving-checks relations rows - never a fabricated pass.
- [x] 4.2 Add `Pages/Compliance/ControlDetail.cshtml` + `.cs`: GET-only, authenticated (inherits
      `AuthorizeFolder("/Compliance")`), projecting one control from the existing `ResolveDrilldown`
      reads inside the same store-unreachable try/catch, rendering `_ObjectDetail` inside the shell.
      Inside that same try/catch, perform the same per-collector evidence-status sidecar read
      (`GetCollectorEvidenceStatusesAsync`) for the requested org and pass a
      `CollectorStatus(org, requirement, collector)` resolver into the shared mapping helper, so the
      full page feeds the shared partial the same per-check statuses the SoA-page inline templates do.
      Authorize the requested control against the caller's FULL accessible org set
      (`orgAccess.AccessibleOrgIdsAsync`), NOT the active-scope-narrowed `OrgScope.InScopeIds` or
      the org-selection cookie: a direct URL for any accessible org renders regardless of which org
      the active-scope cookie selects. A missing or outside-the-accessible-set control returns
      not-found and discloses no record name or facet. Set the N8 breadcrumb ViewData that
      `ShellTopbarViewComponent` reads - `NavGroup = "Comply"`, `NavItem = "soa"` (so the rail marks
      Statement of Applicability active on the detail page), `BreadcrumbParent = "Statement of
      Applicability"` with `BreadcrumbParentHref` set to the SoA page URL
      (`/compliance/statement-of-applicability`), and `Title` set to the control title. Do NOT set
      `BreadcrumbDetail`: the topbar appends `Title` as its own leaf crumb, so `Title` already
      supplies the leaf and setting `BreadcrumbDetail` to the control would duplicate it. The crumb
      reads "Comply / Statement of Applicability / <control>".
- [x] 4.3 Verify: a render test asserts the detail page renders the same facet anatomy the drawer
      shows for the same control (eyebrow through history parity, same section order; the actions
      slot is context-dependent and excluded from the parity assertion). Use a fixture where at
      least one collector check has a NON-unknown evidence status (for example Passing or
      HardFailure) and assert the drawer template and the full page render that same per-check
      status - so the parity covers the sidecar-fed proving-checks rows, not just projection facets.
      Also assert that the store-unreachable path renders the notice, that a missing/inaccessible
      control returns not-found without leaking names, and that with the active-org cookie selecting
      org A a direct control URL for a different but accessible org B still renders while an org
      outside the accessible set returns not-found.

## 5. Wire the Statement of Applicability list (feat(web): open SoA controls in the drawer)

- [x] 5.1 Make each control object an anchor to its full-page detail URL, carrying
      `aria-haspopup="dialog"`, that opens the drawer via `@click.prevent` and the `drawer` store;
      without JavaScript the anchor navigates to the full page (O4).
- [x] 5.2 Render each control's anatomy into an adjacent hidden `<template>` via `_ObjectDetail`
      (server-rendered, no client fetch), keyed so the drawer clones the matching one.
- [x] 5.3 Preserve all existing row markers and behavior (`data-control-id`, `data-check-id`,
      `data-node-id`, dispositions, resolutions, the drill-down toggle, scope, read-only,
      store-unreachable); keep the actions region non-mutating.
- [x] 5.4 Verify: `dotnet build` 0 warnings; existing `StatementOfApplicability` render/E2E
      assertions still pass with the markers intact; the mobile nav (railDrawer) still opens,
      closes, and dims via `.fb-scrim` - the new drawer's `.fb-dscrim` does not regress it.

## 6. Render and guard tests (test(web): drawer render and guard coverage)

- [x] 6.1 Add render tests (following `ShellPaletteRenderTests`): the drawer dialog ARIA and closed
      `inert`; the SoA control anchor carries `aria-haspopup="dialog"` and an adjacent anatomy
      template; the drawer and full page render the identical facet anatomy (eyebrow through
      history) for one control whose fixture gives at least one collector check a NON-unknown
      evidence status, asserting both render that same per-check status in the proving-checks rows -
      the actions slot is context-dependent (drawer "Open full page" link, full page without the
      self-link) and is excluded from the parity assertion.
- [x] 6.2 Assert the served `/css/app.css` carries the drawer classes and no `prefers-color-scheme`
      activation, and `/js/app.js` carries the drawer + store markers.
- [x] 6.3 Verify: `dotnet test tests/Freeboard.Web.Tests` is green.

## 7. Accessibility and keyboard E2E (test(web): drawer behavioral a11y E2E)

- [x] 7.1 Add a `DrawerE2ETests` (following `CommandPaletteE2ETests`), gated on
      `FREEBOARD_TEST_E2E` + Chromium: open the drawer from a SoA control, assert focus moves in,
      the background (`.fb-rail`, `.fb-stage`) is inert while open and Tab stays inside (via inert,
      not a JS tab-cycle), the inert releases on close, Escape and scrim click close, focus returns
      to the opener, and Escape does not dismiss an overlay behind it. Assert that pressing Ctrl-K
      while the drawer is open does NOT open the palette - the drawer stays the single top-level
      overlay.
- [x] 7.2 Assert reduced motion disables the slide AND any close visibility delay, the no-JS anchor
      path navigates to the full page, and the drawer and full page render the same record in the
      same facet section order (eyebrow through history; the actions slot may differ by context).
- [x] 7.3 Add the open drawer to the axe audit in both themes (seed dark via the `data-theme` /
      `localStorage['fb-theme']='dark'` path the palette audit uses); assert zero violations.
- [x] 7.4 Verify: `dotnet test tests/Freeboard.WebE2E` is green with the gate set and skips cleanly
      without it.

## 8. Final verification (chore(web): drawer verification pass)

- [x] 8.1 `dotnet build` reports 0 warnings; `dotnet test tests/Freeboard.Web.Tests` green; WebE2E
      green with `FREEBOARD_TEST_E2E` + Chromium.
- [x] 8.2 `npx markdownlint-cli2` clean on the change docs; `openspec validate
      add-object-detail-drawer` passes.
