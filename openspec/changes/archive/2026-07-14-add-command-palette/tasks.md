## 1. Focus-overlay primitive (feat(web): reusable focus-overlay primitive)

- [x] 1.1 Add the `overlayFocus` Alpine data factory to `src/Freeboard/assets/js/app.js`:
      capture the opener element, move focus to a caller-named target on open, and hold one or more
      caller-named background nodes inert while open (reusing the `railDrawer` `:inert` technique,
      not the same node - the inert target(s) are a parameter and may be a list). The palette passes
      both existing background siblings, `[.fb-rail, .fb-stage]`, so the rail's nav links and opener
      (and the topbar and main, which live inside `.fb-stage`) all become unreachable; the future
      drawer passes `[.fb-stage]` only. Close on Escape while calling
      `stopPropagation()` (and `preventDefault()`) on that Escape so it closes only this overlay
      and does not also close the mobile rail drawer behind it, and restore focus to the opener
      when it is still visible, otherwise to a visible fallback, on close. No result/filter/arrow
      logic in it.
- [x] 1.2 Keep it composable via Alpine data object spread so the future drawer reuses it
      unchanged; document its contract in a short comment (why it exists, what it owns and does
      not own) per comment-etiquette.

## 2. Palette styles (feat(web): tokenized command-palette component classes)

- [x] 2.1 Add `.fb-pal`, `.fb-palbox`, `.fb-palinput`, `.fb-pallist`, the option row, `.fb-hint`,
      and `.fb-palfoot` to `src/Freeboard/assets/css/app.css` `@layer components`, ported from
      the prototype against existing tokens (brand, panel, line, ink, muted, ink on brand-soft for
      the active row, elevation token for the box). No literal hex.
- [x] 2.2 Give the palette a real open/close transition (opacity plus a small transform on the
      box, matching the prototype) with a non-zero `transition-duration`, so the global
      reduced-motion rule (A3) genuinely neutralizes it rather than being vacuously satisfied by a
      bare display toggle. Confirm no `prefers-color-scheme` rule is added (dark stays gated to
      `data-theme`).

## 3. Server-rendered palette markup (feat(web): ShellPalette view component)

- [x] 3.1 Add `Pages/Shared/Components/ShellPalette/ShellPaletteViewComponent.cs` that resolves
      the nav view for the request via the existing `ShellNavResolver` (reuse, no second page
      list).
- [x] 3.2 Add `Pages/Shared/Components/ShellPalette/Default.cshtml` rendering the dialog: a
      `role="combobox"` input (`aria-expanded`, `aria-controls`, `aria-autocomplete="list"`,
      `aria-activedescendant`) over a `role="listbox"` of `role="option"` rows - one Page option
      per gated nav item (label + Page tag + route) plus the Toggle dark mode Command option and
      the keyboard-hint foot. No Agent option.
- [x] 3.3 In `Pages/Shared/_Layout.cshtml`, mount `<vc:shell-palette />` as a third sibling of the
      existing rail (`.fb-rail`) and stage (`.fb-stage`) under `.fb-app` - no new wrapper element and
      no change to the shell grid. The palette holds `.fb-rail` and `.fb-stage` inert while open and
      is itself never in that list, so it stays reachable; the drawer keeps inerting only `.fb-stage`.
      Establish the shared Alpine scope (store or shell-root x-data) that couples the rail opener to
      the palette.

## 4. Wire the opener (feat(web): open the palette from the rail entry)

- [x] 4.1 In `Pages/Shared/Components/ShellRail/Default.cshtml`, give `.fb-search-entry`
      `aria-haspopup="dialog"` and the open handler; update the file comment that currently
      documents the entry as inert.

## 5. Palette interaction (feat(web): command-palette keyboard and filtering)

- [x] 5.1 Add the `commandPalette` Alpine component to `app.js` composing `overlayFocus`, passing
      `[.fb-rail, .fb-stage]` as the inert targets and the palette input as the focus-in target:
      document-level `Ctrl-K` / `Cmd-K` / `/` open listener (registered in `init`, removed in
      `destroy`; `/` ignored while a text field/contenteditable is focused; `preventDefault` on a
      match), capturing the rail entry as the opener.
- [x] 5.2 Implement filter (case-insensitive substring, first match becomes active), ArrowUp/
      ArrowDown highlight movement updating `aria-activedescendant` and `aria-selected` without
      moving DOM focus, Enter to run, and Escape/scrim-click to close via the primitive.
- [x] 5.3 Extract a module-scope theme helper in `app.js` (e.g. `toggleTheme`/`setTheme` over the
      same `fb-theme` key, `light`/`dark` values, and `data-theme` attribute the pre-paint reader
      consumes) and call it from BOTH the topbar `themeToggle` and the palette Command, so there
      is one theming path and no duplicated `data-theme` plus `fb-theme` write. Then implement
      running a result: Page navigates to its route; Toggle dark mode calls that shared helper and
      closes the palette; both restore focus per the focus-restore rule (the opener when visible,
      else a visible fallback).

## 6. Tests (test(web): palette render, guard, and accessibility)

- [x] 6.1 Render/guard tests in `tests/Freeboard.Web.Tests`: combobox/listbox roles and the
      required ARIA attributes render; the opener carries `aria-haspopup="dialog"`; Page options
      come from the catalog and a gated (EE/authz) destination is absent from the palette as it
      is from the rail; the Toggle dark mode Command is present and no Agent option renders.
- [x] 6.2 Served-bundle/CSS guards: `/js/app.js` carries the palette + primitive markers;
      `/css/app.css` carries the `.fb-pal*` classes, no `prefers-color-scheme` activation, and
      no literal hex in the palette block (the `ComponentLayerGuardTests` literal-color scan
      covers the whole `@layer components` block, so confirm it still passes with the palette
      classes present - no list edit needed). Do NOT add `.fb-palinput` to the resting-boundary
      guard list in `ComponentLayerGuardTests.InteractiveControlsUseAThreeToOneBoundaryToken`: that
      guard only recognizes a full `border-<token>` / `border-color: var(--color-...)` and the
      palette input is borderless (a `border-bottom` divider, no outer border), so listing it would
      either fail the guard or force a wrong full outer border. If a boundary assertion is wanted,
      assert the enclosing `.fb-palbox` (the box on the scrim) instead. Make the active row use
      `--color-ink` on `--color-brand-soft`, because the suite's AAA gate (7:1 `color-contrast-enhanced`)
      requires it - `--color-brand-ink` on `--color-brand-soft` is only 5.73:1 in dark and would fail;
      confirm `ContrastGuardTests` covers that ink-on-brand-soft pair at the AAA bar in both themes.
- [x] 6.3 Add the open palette to `AccessibilityAuditE2ETests` in both themes and assert zero axe
      violations across the suite's standard set. Axe skips hidden nodes and the palette is
      `display:none` until opened, so explicitly open it (click the rail entry or press Ctrl-K) and
      wait for it visible BEFORE `RunAxe`. The suite does no theme seeding today, so seed dark for
      the dark pass via Playwright `AddInitScript` setting `localStorage['fb-theme']='dark'` before
      the page loads (the pre-paint reader then applies `data-theme="dark"`); do not claim to reuse
      an existing data-theme seeding path, there is none.
- [x] 6.4 Add a keyboard/focus E2E (Playwright + CDP): open with Ctrl-K and by click; `/` opens
      from the page body but is ignored (typed as text) while a text field is focused; typing
      filters and the first match becomes `aria-activedescendant`; arrows move the highlight
      while `document.activeElement` stays the combobox input and no option holds DOM focus;
      Enter on a Page navigates; Escape closes and focus returns to the rail entry when it is visible,
      otherwise to a visible fallback control (never a hidden control). Assert the
      background is unreachable while open: with the palette open, Tab cycles stay inside the
      palette and neither Tab nor a pointer click reaches the rail, topbar, or main controls
      (`.fb-rail` and `.fb-stage` are both inert) - covering the background-not-reachable scenario
      and A2. Prove
      stop-propagation on the drawer-open path (not a drawer-absent stand-in): set a sub-1024px
      viewport, open the mobile rail drawer, open the palette over it with Ctrl-K, press Escape
      once, and assert only the palette closed, the drawer stayed open, and focus landed on the
      visible opener (never a hidden control). Skips cleanly without `FREEBOARD_TEST_E2E` +
      Chromium.

## 7. Verification

- [x] 7.1 Run `dotnet build` (runs the bun asset build) and `dotnet test`; run the E2E suite
      with `FREEBOARD_TEST_E2E=1` and Chromium installed. Run `npx markdownlint-cli2` on the
      change docs. Fix any failures.
- [x] 7.2 Ensure the pipeline that gates this change actually runs the WebE2E suite
      (`FREEBOARD_TEST_E2E=1` + Chromium). All behavioral accessibility coverage (combobox focus
      retention, arrow nav, Escape restore, the `/` guard, stop-propagation, background inert)
      lives there; the served-bundle string markers are a smoke check that strings shipped, not
      behavioral proof. The WebE2E suite must not be left to its default skip for this change.
