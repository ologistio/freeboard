## Why

The app shell (P3) and the command palette (P4a) shipped, but opening an object still
means a full-page navigation with the old markup: there is no drawer, and no page renders
the uniform object anatomy (O3). UX rules O3/O4/A5 require every object to open in a
right-anchored drawer from a list and as a full page from a direct link, both rendering
the same anatomy in the same order, with the drawer trapping and restoring focus. This
change delivers that drawer as the second slice of migration phase P4, reusing the
`overlayFocus` focus primitive that P4a built for exactly this purpose, and wires it into
one real list page so it is a live, end-to-end-testable capability rather than a prototype.

## What Changes

- Add one reusable `objectDrawer` Alpine component in `app.js` that composes the existing
  `overlayFocus` primitive (opener capture, focus move-in, background inert, Escape-with-
  stop-propagation, focus restore to a visible control). It does not re-author any focus-
  trap, Escape, or inert mechanics - it reuses the primitive exactly as `commandPalette`
  does.
- Render a right-anchored ARIA dialog (`role="dialog"`, `aria-modal="true"`,
  `aria-labelledby` the title) over a scrim, mounted once in `_Layout` as a sibling of
  `.fb-rail` and `.fb-stage` under `.fb-app` (so it stays reachable while it holds those
  two inert). `inert` when closed keeps its controls out of the tab order. Escape and
  scrim click close; focus moves in on open and returns to the opener on close.
- Add a shared, tokenized object-anatomy partial (`_ObjectDetail.cshtml`) driven by one
  view model, rendering the uniform anatomy (O3) in the fixed order: eyebrow and title,
  status, then assertion, relations, evidence, guidance, history, then actions. Empty
  facets render as explicitly empty (O2), never omitted.
- Add tokenized `.fb-drawer` / `.fb-dscrim` / `.fb-dhead` / `.fb-dbody` / `.fb-dfoot` /
  `.fb-dsec` / `.fb-dl` / `.fb-dlist` / `.fb-guidance` / `.fb-xbtn` / `.fb-sheet` classes to
  `app.css @layer components`, tokens only (no literal hex), both themes from the one token
  set, reduced-motion (A3) disabling the slide, dark gated to `data-theme` (no
  `prefers-color-scheme` rule). The drawer scrim uses a distinct `.fb-dscrim` class (reusing
  the ink-mix dim value) rather than the mobile nav's `.fb-scrim`, so the mobile rail drawer is
  not affected.
- Wire the drawer into the Statement of Applicability page
  (`Pages/Compliance/StatementOfApplicability.cshtml`): each control row becomes an anchor
  to a full-page control-detail URL and carries a server-rendered hidden `<template>` of
  its anatomy. With JavaScript the anchor opens the drawer from that template; without
  JavaScript it navigates to the full page (O4). A direct link to the same control renders
  the identical anatomy full-page through the same partial.
- Add a full-page control-detail Razor Page under Compliance that projects one control
  from the existing Statement of Applicability drill-down reads and renders the shared
  partial inside the shell (the direct-link and no-JS target for O4, and the breadcrumb
  detail segment for N8). The page is bounded by the caller's accessible organisations (the
  same boundary the list applies); a missing or out-of-boundary control renders not-found
  without leaking record names. The status facet is honest (S6): the drill-down projection carries
  no evaluated status at either level (`SoaControlNode.Evaluation` is the check-COMBINE rule, not a
  result), so the control-level status renders as an explicit O2 empty ("not evaluated"); evaluated
  per-check statuses come from the separate per-collector evidence-status read (not the drill-down
  projection) and render where that read supplies them - never a fabricated pass.
- Add render/guard tests (Web.Tests) and behavioral accessibility E2E (WebE2E, gated).

This change is MIT (default). All code lives in `src/Freeboard` (the web UI, MIT under the
root LICENSE). Nothing is placed in `src/Freeboard.Enterprise`; the reference graph is
unchanged and no EE dependency is introduced.

## Non-goals

- The full P5 restyle of the Statement of Applicability page. This wires the drawer and a
  control-detail view onto that page as a minimal, coherent slice; it does not convert the
  page to the list composition, re-sort exceptions-first (L1), add chip filters (L2), or
  restyle the drill-down tree. The blast radius on that page stays as small as coherent.
- Any other object type. Only the Statement of Applicability control object is wired.
  Vendors, users, and every other list stay on their current markup; P5 reuses this
  change's partial and drawer for them.
- New write actions on the Statement of Applicability page. It is read-only and GET-only;
  the drawer's actions region carries only honest, non-mutating affordances (a link to the
  full page) rather than fabricated Fix/Assign/Create buttons.
- Enriching the control-detail data model. Facets the current projection does not carry
  (a written assertion, evidence artifacts, guidance copy, history) render as explicit
  empties (O2); populating them is later product work.

## Capabilities

### New Capabilities

- `web-object-drawer`: the object-detail drawer's observable contract - the right-anchored
  ARIA dialog and its focus/Escape/scrim/inert behavior via the reused focus-overlay
  primitive, the uniform object anatomy in its fixed order rendered from one shared
  partial, drawer-and-full-page parity for one record (O4), reduced-motion and token-only
  theming in both themes, and the Statement of Applicability control as the wired surface.

### Modified Capabilities

<!-- None. The Statement of Applicability projection, drill-down, JSON endpoint, gating,
     read-only and store-unreachable behavior are all unchanged; the drawer and control-
     detail page are an additive presentation layer over the existing reads, so no
     statement-of-applicability, compliance-web-read, web-app-shell, or web-command-palette
     requirement changes. -->

## Impact

- MIT change (default). Touched code, all in `src/Freeboard`:
  - `assets/js/app.js`: add the `objectDrawer` Alpine component and a `drawer` Alpine
    store coupling the list openers to the drawer (mirrors the `palette` store). Reuses
    `overlayFocus` unchanged.
  - `assets/css/app.css` (`@layer components`): add the drawer and anatomy classes,
    tokens only.
  - `Pages/Shared/Components/ObjectDrawer/` (new view component) and its `Default.cshtml`:
    the drawer shell mounted by `_Layout`.
  - `Pages/Shared/_ObjectDetail.cshtml` (new partial) and a small view model: the shared
    anatomy, reused by the drawer template and the full-page detail.
  - `Pages/Shared/_Layout.cshtml`: mount the drawer as a sibling of `.fb-rail`/`.fb-stage`
    under `.fb-app`; no grid change.
  - `Pages/Compliance/StatementOfApplicability.cshtml`: control rows become drawer
    openers with an inline anatomy template; small, contained edit.
  - `Pages/Compliance/ControlDetail.cshtml` (+ `.cs`): the full-page control detail,
    reusing the Statement of Applicability drill-down projection.
- Tests: render/guard tests in `tests/Freeboard.Web.Tests`; drawer keyboard/focus/axe E2E
  in `tests/Freeboard.WebE2E`, gated on `FREEBOARD_TEST_E2E` + Chromium.
- No new runtime dependencies. No database, API, migration, or persistence changes.
