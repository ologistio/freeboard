## Why

The app shell (P3) ships a command-palette entry in the nav rail, but it is an inert
affordance: it carries the `Ctrl K` hint and holds a place in the tab order, yet opens
nothing. UX rule N7 requires exactly one working command palette as the single global
search-or-ask surface. This change delivers the palette behavior behind that entry - the
first slice of migration phase P4 - and, because the palette is the first accessibility-
critical overlay, it introduces the reusable focus-management primitive (focus restore,
Escape-to-close, background containment) that the later object-detail drawer will reuse.

## What Changes

- Wire the existing `fb-search-entry` rail button to open a working command palette; it
  gains `aria-haspopup="dialog"` and an Alpine open handler. **BREAKING** to the app-shell
  requirement that pins the entry as non-functional this phase (see Modified Capabilities).
- Add a document-level shortcut listener: `Ctrl-K`, `Cmd-K`, or `/` opens the palette from
  anywhere (the `/` shortcut is ignored while a text field is focused so it stays typeable).
- Render the palette as an ARIA combobox + listbox: a text input that keeps DOM focus at all
  times, with `aria-activedescendant` tracking the highlighted option. Arrow keys move the
  active option, Enter runs it, Escape closes and restores focus to the opener when it is
  visible (otherwise to a visible fallback control), typing filters, the first match becomes active.
- Populate the result list server-side from the resolved nav catalog (the one P3 source):
  each visible page is a **Page** result, respecting the same authorization and entitlement
  gating as the rail so no gated destination leaks. Add a **Command** result (Toggle dark
  mode) wired to the existing theme toggle. Do not ship **Agent** results yet - no assistant
  exists (see design.md for the rationale).
- Add a reusable focus-overlay Alpine primitive in `app.js` (opener capture, focus move-in,
  focus restore on close, Escape handling that closes only the topmost overlay by stopping the
  event's propagation, background inert) that the palette consumes now and the drawer will
  consume later.
- Add tokenized palette component classes to `app.css` (`@layer components`), no literal hex,
  both themes from the existing token set, reduced-motion honored by the global A3 rule.
- Add render/guard tests (Web.Tests) and an accessibility + keyboard E2E path (WebE2E).

## Capabilities

### New Capabilities

- `web-command-palette`: the command palette's observable contract - single tagged-result
  surface, the ARIA combobox/listbox model with `aria-activedescendant`, the open shortcuts,
  keyboard operation and focus restore, the nav-catalog-sourced page index with gating, the
  Command result, reduced-motion and token-only theming, and the reusable focus-overlay
  primitive it shares with future overlays.

### Modified Capabilities

- `web-app-shell`: the requirement that pinned the command-palette entry as a static affordance
  this phase is renamed and rewritten - the entry now opens the palette, advertises
  `aria-haspopup="dialog"`, and is operative. It remains the single search-or-ask surface and
  stays keyboard-focusable.
- `web-ux-conventions`: N7 is amended so its "ask the assistant" clause is explicitly staged as
  aspirational-not-built while no assistant backend exists, keeping the single-search-surface,
  page-jump, and command behavior as the built part. This is what lets the palette ship with no
  Agent result without contradicting the ratified rule (whose preamble makes every rule normative
  regardless of build state).

## Impact

- MIT change (default). All code lives in `src/Freeboard` (the web UI, MIT under the root
  LICENSE). Nothing goes in `src/Freeboard.Enterprise`; the reference graph is unchanged.
- Touched code: `src/Freeboard/assets/js/app.js` (focus-overlay primitive + palette component +
  a shared module-scope theme helper both the topbar toggle and the palette command call),
  `src/Freeboard/assets/css/app.css` (palette classes), a new `ShellPalette` view component under
  `Pages/Shared/Components/`, `Pages/Shared/_Layout.cshtml` (mount the palette as a third sibling of
  the existing rail and stage under `.fb-app`, and wire the opener - no new wrapper, no grid change),
  `Pages/Shared/Components/ShellRail/Default.cshtml` (wire the opener). Reuses
  `Navigation/ShellNavResolver` and `ShellNavCatalog` unchanged.
- Tests: new render/guard tests in `tests/Freeboard.Web.Tests`; the palette added to the axe
  audit and a new keyboard/focus E2E in `tests/Freeboard.WebE2E`.
- No new runtime dependencies. No database, API, or persistence changes.
