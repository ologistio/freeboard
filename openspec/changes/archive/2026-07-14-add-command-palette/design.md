## Context

Phase P3 shipped the app shell: a nav rail, breadcrumb topbar, and scrolling main, all
driven by one server-side nav source (`Navigation/ShellNavCatalog` evaluated per request by
`Navigation/ShellNavResolver`). The rail already renders a command-palette entry
(`.fb-search-entry` in `Pages/Shared/Components/ShellRail/Default.cshtml`) with the `Ctrl K`
hint, but it is deliberately inert: no `aria-haspopup`, no open handler, kept in the tab
order so the keyboard path (A2) stays whole. The current `app.css` already carries the token
foundation (both themes from one `@theme` set, `data-theme` override only - no
`prefers-color-scheme` activation), a global reduced-motion rule (A3), and the
`.fb-search-entry`/`.fb-kbd` styles. `app.js` holds three small Alpine components
(`themeToggle`, `railDrawer`, `accountMenu`); the drawer already demonstrates the house
pattern for an overlay: capture the opener, move focus in, hold the background `:inert` while
open, and restore focus to the opener on Escape/close.

This change delivers the palette behavior (N7) - the first slice of P4. The reference design
is `src/Freeboard/stories/CommandPalette.stories.js` (reference-only prototype with literal
hex and inline handlers). The object-detail drawer is the other half of P4 and is a separate
later change; it is out of scope here.

## Goals / Non-Goals

**Goals:**

- One working command palette, the single global search-or-ask surface (N7).
- ARIA combobox + listbox: the input keeps DOM focus; `aria-activedescendant` tracks the
  highlight; DOM focus never moves onto option elements.
- Open from anywhere via `Ctrl-K` / `Cmd-K` / `/`; type to filter; ArrowUp/ArrowDown move the
  active option; Enter runs it; Escape closes and restores focus to the rail opener when it is
  visible, otherwise to a visible fallback control.
- The page index is the P3 nav catalog, gated exactly as the rail is (no leaked destination).
- A small, reusable focus-overlay Alpine primitive in `app.js`, shaped for reuse by the
  future drawer, not palette-specific.
- Token-only styling, both themes, reduced-motion honored, AA/AAA contrast in both themes.

**Non-Goals:**

- The object-detail drawer (O3/O4/A5) - separate later P4 change. This change only builds the
  focus-overlay primitive the drawer will reuse; it does not build the drawer.
- An assistant / Agent-tagged results (no assistant backend exists; see the Agent decision).
- Recent/frequent history, fuzzy ranking, or scoped search - filtering is a plain
  case-insensitive substring match, matching the prototype.
- Any new global search box elsewhere - N7 forbids a second one.
- Server-side full-text search or a search API - the index is the small nav list, rendered
  into the page.

## Decisions

### D1. ARIA model: combobox input + listbox, `aria-activedescendant` only

The dialog contains a `role="combobox"` text input (`aria-expanded="true"`,
`aria-controls` -> the listbox id, `aria-autocomplete="list"`, `aria-activedescendant` -> the
active option id) over a `role="listbox"` `<ul>` of `role="option"` `<li>` rows. The input
holds DOM focus for the palette's whole lifetime. Arrow keys and filtering change which
option carries `aria-selected="true"` and update `aria-activedescendant`; they never call
`.focus()` on an option. This is the WAI-ARIA editable-combobox-with-listbox pattern and
matches the prototype's markup exactly. Alternative - a roving `tabindex` over the options
(moving DOM focus) - was rejected: it fights the "type to filter while arrowing" interaction
and is not what the reference specifies.

### D2. Page index sourced from the resolved nav view (reuse, no second list)

The palette's Page results are the same items the rail shows. A new `ShellPalette` view
component calls the existing `ShellNavResolver.ResolveAsync` for the current request and
renders one option per visible item (label + `Page` tag + the route as the option's target).
Because it resolves through the same path as the rail, entitlement and authorization gating
are identical - a page the viewer cannot reach never appears in the palette. This reuses
`ShellNavCatalog`/`ShellNavResolver` with zero duplication (code-as-liability decision order:
reuse before add). Alternative - a client-side JSON index emitted once - was rejected: it
would duplicate the gating logic on the client and risk leaking gated routes; server-render
keeps one source of truth and one gate.

The active-item marking that the rail needs is irrelevant to the palette, so `ShellPalette`
reads the resolved `ShellNavView` for label/route only. Selecting a Page result navigates by
setting `location.href` to the route (a real navigation, so N9 list-state and normal history
behavior are unaffected).

Both the rail and the palette render in the same request and each calls
`ShellNavResolver.ResolveAsync`, which runs async authorization and entitlement checks. That is
two identical resolves per request. Correctness is unaffected (both see the same gated view), but
to avoid the duplicate work the resolved `ShellNavView` should be memoized for the request - e.g.
cached in `HttpContext.Items` on first resolve and reused by the second component. This is a
documented performance consideration, not a correctness requirement; implement it if the second
resolve shows up, but the gating is right either way.

### D3. Command results: one real command now (Toggle dark mode)

The palette ships one `Command`-tagged result, "Toggle dark mode", that flips and persists the
theme. Today the theme write lives privately inside `Alpine.data("themeToggle")`, so it is not
actually reusable; "call the existing behavior" would mean copying the `data-theme` plus
`fb-theme` write. To keep a single theming path, extract a module-scope helper in `app.js` (a
`toggleTheme`/`setTheme` closure over the same key, values, and attribute the pre-paint reader in
`_Head.cshtml` consumes) and have BOTH the topbar `themeToggle` and the palette Command call it.
One write of the theme contract, two callers - no duplication. Commands are a tiny static client-side list in the palette
component (label + tag + an action key the component dispatches). One command is enough to
establish the Command kind and its tag; more commands are additive later. A command runs in
place and closes the palette, restoring focus per the focus-restore rule (the opener when it is
still visible, otherwise a visible fallback control).

### D4. Agent results: cut this phase, not stubbed

No assistant backend exists. The two options are to **cut** Agent results (reserve the tag, show
no Agent row) or to **stub** them - an honest local `aria-live` "Assistant is coming soon" row
that keeps focus in the input and routes nowhere (no fake page, no fake answer). Decision:
**cut** for this phase.

Rationale for cut over stub: even an honest coming-soon stub is standing UI, string copy, and a
result-kind branch built for a feature that does not exist - dead scaffolding under code-as-
liability ("no TODO scaffolding for imagined requirements"), and the tag vocabulary keeps
re-adding it purely additively, so nothing is lost by waiting. The stub's honesty aim - never
ship a fake route or answer - is real and is honored a different way: by shipping no Agent row at
all, there is nothing dishonest to announce.

Cutting Agent rows must not silently contradict ratified `web-ux-conventions` N7, whose text
still says the palette "asks the assistant" and whose spec preamble makes every rule normative
regardless of build state. So this change carries a `web-ux-conventions` spec delta that modifies
N7 itself: it stages the "ask the assistant" clause as aspirational-not-built while no assistant
backend exists, keeping the single-search-surface and page-jump and command behavior as the built
part. That delta - not an internal note - is what makes the cut conform; with N7 amended, shipping
no Agent row is exactly what the rule now requires. This change implements the buildable part -
the single search surface that jumps to pages and runs commands, with Page and Command as the two
live kinds. The result-kind vocabulary (Page/Command/Agent as a tag) is
designed so an Agent kind is a purely additive change when an assistant lands: a new tagged
result source, no rework of the ARIA model, keyboard handling, or focus primitive.
`web-command-palette` states the tag vocabulary includes Agent as reserved-but-unused and marks
the assistant clause aspirational-not-built this phase, so the later addition does not
contradict the spec. If the honest-stub path is later preferred, it re-enters as that additive
Agent source plus the `aria-live` "coming soon" announcement described above.

### D5. Reusable focus-overlay primitive in `app.js`

Add one Alpine data factory, `overlayFocus` (working name), that both the palette (now) and
the drawer (later) compose. Responsibilities, and only these:

- capture the opener element on open (from the triggering event or an explicit ref);
- move focus to a caller-named element on open (the palette's input; the drawer's panel);
- hold a caller-named background node inert while open so neither focus nor pointer can reach the
  obscured page, reusing the `:inert`-the-background technique `railDrawer` already uses. Reuse
  means the technique, not the same node: the inert target(s) are a parameter of the primitive, and
  it accepts more than one so a caller can inert several sibling nodes at once. The mobile drawer
  inerts only `.fb-stage`, because there the rail IS the overlay and must stay reachable. The
  palette is the opposite case - its background includes the rail, which holds the nav links and the
  palette opener, and all of it must be unreachable while the palette is open - so the palette inerts
  both existing background siblings directly: `.fb-rail` and `.fb-stage`. In `_Layout` the topbar
  (`.fb-topbar`) and main (`.fb-main`) live inside `.fb-stage`, so inerting `.fb-stage` covers them;
  `.fb-rail` is the only other background sibling. That two-node list (`.fb-rail`, `.fb-stage`) makes
  the rail, topbar, and main all unreachable with no new DOM element and no change to the shell grid,
  which lays out `.fb-rail` and `.fb-stage` as direct children of `.fb-app`. The palette mounts as a
  third sibling under `.fb-app` (for the shared Alpine scope) and is never in its own inert list, so
  it stays reachable. The drawer passes `[.fb-stage]`; the palette passes `[.fb-rail, .fb-stage]`.
  Alternative - wrap `.fb-rail` and `.fb-stage` in one container and move the grid, height,
  background, and mobile-breakpoint rules onto that container so the palette inerts a single node -
  rejected: it restructures the shell grid (the container would have to become the grid and inherit
  every rule that currently targets `.fb-app` and its direct children) for no functional gain, since
  the multi-target inert achieves the same containment with less change;
- close on Escape, **stop propagation of that Escape event**, and restore focus to the captured
  opener when that opener is still visible, otherwise to a caller-named visible fallback control
  (for the palette, the topbar nav toggle) - never a hidden or detached element.

The Escape-stop-propagation rule lives inside the primitive so every overlay that composes it
inherits the behavior. Without it, one Escape both
closes the palette and bubbles to the mobile `railDrawer`'s own Escape handler: the drawer then
closes too and tries to restore focus to its hamburger opener, which may be `display:none` at
the current breakpoint - focus lands on a hidden or wrong element. The primitive calls
`event.stopPropagation()` (and `preventDefault()`) on the Escape it handles so exactly one
overlay - the topmost - closes per keypress. This requirement is independent of the containment
mechanism: it holds whether the background is `:inert` (chosen here) or a Tab-trap (the
considered alternative).

It does not own arrow-key navigation, filtering, `aria-activedescendant`, or result rendering
- those are palette-specific and live in the palette component that composes the primitive.
Shape: an Alpine data object the component merges via `Alpine.data("commandPalette", () => ({
...overlayFocus(), /* palette-specific state and methods */ }))`, or an equivalent small
factory the drawer will call the same way. This keeps the primitive framework-idiomatic
(Alpine data, like the existing three components) and genuinely shared - the justification for
adding it now is the two consumers (palette + drawer), per code-as-liability.

Containment choice: reuse `:inert` on the background rather than a hand-rolled Tab-cycling focus
trap. The background-inert approach is already proven in this codebase (`railDrawer`), is less
code (code-as-liability), and cannot desync from the DOM the way a manual Tab-wrap trap can - a
trap must track the live set of focusable descendants, which the filtered listbox mutates on every
keystroke. The Tab-trap is the considered alternative and stays the documented fallback only if a
future overlay cannot inert its container; the palette and drawer both can. The primitive's contract is
"trap or hold background inert" so either implementation satisfies it (matching the app-shell A5
wording). Either way, `overlayFocus` is the single shared unit the later drawer reuses, and the
Escape-stop-propagation rule above lives inside it, so the containment decision does not change
the primitive's shared surface.

### D6. Open shortcuts and the `/` typeable-field guard

A single document-level `keydown` listener (registered by the palette component's `init`, torn
down in `destroy`) opens the palette on `Ctrl-K`, `Cmd-K` (metaKey), or a bare `/`. The `/`
shortcut is ignored when focus is in a text input, textarea, or contenteditable so it stays a
literal character where the user is typing; `Ctrl/Cmd-K` open regardless. Opening from the
shortcut captures the rail `.fb-search-entry` button as the opener (so Escape restores to it when
it is visible, and otherwise to the visible topbar nav toggle fallback), matching opening by click. `preventDefault` on the matched shortcut stops the browser default
(Cmd-K focus-bar, `/` quick-find).

### D7. Markup home and mount point

The palette component is one `ShellPalette` Razor view component rendered by `_Layout.cshtml`
as a third sibling of the existing rail (`.fb-rail`) and stage (`.fb-stage`) inside `.fb-app`, so
the palette holds those two siblings inert while its own overlay and input stay reachable. `_Layout`
gains no wrapper element and no grid change - it only mounts the palette sibling and wires the
opener; the drawer keeps inerting only `.fb-stage`. The rail's `.fb-search-entry` gains
`aria-haspopup="dialog"` and an Alpine
handler that opens the palette. Because Alpine state is
shared through a single root `x-data` on the shell, the opener (in the rail) and the palette
(sibling) must sit under a common Alpine scope; the palette component owns that scope and the
rail opener references it (an `x-data` on a shell-root wrapper, or an Alpine store - decide at
implementation time; the store keeps the two markup regions decoupled). The listbox options,
their ids, roles, tags, and the keyboard-hint foot are server-rendered so the palette is
correct with JS disabled for the static parts and axe can audit real option markup.

### D8. Styling: tokenized component classes, no hex, both themes

Port the prototype's `.fb-pal*` classes into `app.css @layer components` against existing
tokens. The token list: brand, panel, line, ink, muted; `--color-ink` on
`--color-brand-soft` for the active row (its label and tag), because the suite's AAA gate
(7:1 `color-contrast-enhanced`) requires it - `--color-brand-ink` on `--color-brand-soft` is only
5.73:1 in dark and would fail the enhanced bar; the elevation/shadow token for the box; and the
scrim dim reuses the existing `.fb-scrim`
ink-mix `color-mix(in srgb, var(--color-ink) 28%, transparent)` for `.fb-pal`'s backdrop, so no
literal rgba is reintroduced (the whole-block literal-color guard would catch one). No literal hex.
Both themes render from the one token set with no new `prefers-color-scheme` rule (dark stays gated
to `data-theme`, matching P1-P3).

The open and close use a real transition - an opacity plus a small transform on the box, matching
the prototype - with a non-zero `transition-duration`. This is deliberate over a bare display
toggle: the global reduced-motion rule (A3) only zeroes `transition-duration`/`animation-duration`,
so a plain display swap has nothing for it to neutralize and the reduced-motion guarantee would be
vacuous. A genuine duration makes A3 actually do work (and makes the reduced-motion scenario
testable). The transition sits under the one global rule, so no per-component motion guard is
needed. Contrast of the input text, the muted tag/foot text, and the active-row combination must
clear the AA/AAA bar in both themes (A1/A6); the contrast guard test covers it via the existing
token pairs.

## File changes (respecting the reference graph)

All changes are in `src/Freeboard` (MIT web UI) and its test projects. Nothing touches
`Freeboard.Enterprise`, `Freeboard.Core`, `Freeboard.Agent`, or `Freeboard.CLI`; the
reference graph is unchanged and no EE dependency is introduced.

- `src/Freeboard/assets/js/app.js`: add the `overlayFocus` primitive and the `commandPalette`
  Alpine component (open shortcuts, filter, arrow nav, `aria-activedescendant`, run Page/
  Command, Escape close via the primitive).
- `src/Freeboard/assets/css/app.css`: add `.fb-pal`, `.fb-palbox`, `.fb-palinput`,
  `.fb-pallist`, option row, `.fb-hint`, `.fb-palfoot` in the components layer, token-only.
- `src/Freeboard/Pages/Shared/Components/ShellPalette/ShellPaletteViewComponent.cs` +
  `Default.cshtml`: resolve the nav view, render combobox/listbox with Page options + the
  Command option.
- `src/Freeboard/Pages/Shared/_Layout.cshtml`: mount `<vc:shell-palette />` as a third sibling of
  `.fb-rail` and `.fb-stage` under `.fb-app` (not inside either inerted node); establish the shared
  Alpine scope for opener + palette. No new wrapper element, no grid change.
- `src/Freeboard/Pages/Shared/Components/ShellRail/Default.cshtml`: give `.fb-search-entry`
  `aria-haspopup="dialog"` and the open handler; update the file's comment (it currently
  documents the entry as inert).

## Risks / Trade-offs

- [Accessibility-critical JS; the palette is net-new focus/keyboard code] -> Reuse the proven
  `:inert`-background pattern and share one primitive; cover it with an axe audit in both
  themes plus a keyboard/focus-restore E2E driven through Playwright/CDP, not just a happy-
  path render assertion.
- [Shared Alpine scope across two markup regions (rail opener + sibling palette)] -> Prefer an
  Alpine store or a shell-root `x-data` so the regions stay decoupled; a single monolithic
  `x-data` spanning rail and palette would be brittle.
- [The `/` shortcut hijacking a keystroke users mean literally] -> Guard: `/` is ignored while
  a text field / contenteditable is focused; `Ctrl/Cmd-K` are unambiguous and open always.
- [Palette page index drifting from the rail] -> Mitigated by construction: both resolve the
  one `ShellNavCatalog` through `ShellNavResolver`; no second list exists to drift.
- [Cutting Agent results could read as dropping N7 scope] -> `web-command-palette` records the
  Agent tag as reserved and the assistant clause as aspirational, so re-adding is additive and
  the spec stays honest about what ships.

## Migration Plan

Additive and reversible. The palette is new behavior behind an entry that was already present
(now made operative); no data migration, no route change, no API change. Rollback is reverting
the change - the entry returns to its inert state. Ship after the P3 shell; no ordering
dependency on the drawer (this change builds the shared primitive the drawer later consumes).

## Verification strategy

- **Render/guard tests (`tests/Freeboard.Web.Tests`)**, following `ShellChromeRenderTests` and
  the served-bundle pattern of `ShellThemeToggleTests`:
  - Palette markup renders in the shell: one `role="combobox"` input with `aria-expanded`,
    `aria-controls`, `aria-autocomplete="list"`, `aria-activedescendant`; one `role="listbox"`
    with `role="option"` rows; the opener carries `aria-haspopup="dialog"`.
  - Page options come from the nav catalog and are gated: an EE/authz-gated destination absent
    from the rail is also absent from the palette (assert with an unentitled/non-admin session,
    mirroring `RailGatesEnterpriseItemByEntitlement`).
  - The Command option (Toggle dark mode) is present; no Agent option is rendered.
  - The served `/js/app.js` bundle contains the palette + primitive markers (open-shortcut
    handling, `aria-activedescendant`, focus restore) - a string assertion like the theme
    test, since there is no JS test runner in this repo.
  - The served `/css/app.css` carries the `.fb-pal*` classes and no `prefers-color-scheme`
    activation. The `@layer components` literal-color guard (`ComponentLayerGuardTests`) already
    scans the whole block, so the new palette classes are covered with no list edit - confirm it
    still passes with them present. Do NOT add `.fb-palinput` to the resting-boundary guard
    (`InteractiveControlsUseAThreeToOneBoundaryToken`): that guard only recognizes a full
    `border-<token>` or `border-color: var(--color-...)`, and the palette input is borderless (a
    `border-bottom` divider, no outer border), so listing it would fail the guard or force a wrong
    full outer border. If a boundary assertion is wanted, assert the enclosing `.fb-palbox` box
    instead. The active row uses `--color-ink` on `--color-brand-soft`, covered by `ContrastGuardTests`
    at the AAA bar (7:1 `color-contrast-enhanced`) that the suite enforces - `--color-brand-ink` on
    `--color-brand-soft` is only 5.73:1 in dark and would fail it - so the palette's text, tag, foot, and
    active-row contrast all clear both themes (A1/A6).
- **Accessibility E2E (`tests/Freeboard.WebE2E`, axe)**: add the open palette to
  `AccessibilityAuditE2ETests` in both themes, asserting zero violations across the maximal
  standard set the suite runs. The palette is `display:none` until opened and axe skips hidden
  nodes, so open it (click or `Ctrl-K`) and wait for it visible before `RunAxe`. The suite seeds no
  theme today; seed dark for the dark pass via Playwright `AddInitScript` writing
  `localStorage['fb-theme']='dark'` before load, so the pre-paint reader applies `data-theme="dark"`.
  There is no existing data-theme seeding path to reuse.
- **Keyboard / focus E2E (`tests/Freeboard.WebE2E`, Playwright + CDP)** covers every keyboard
  and focus requirement as an explicit assertion:
  - open with `Ctrl-K` and with a click on the rail entry;
  - `/` opens the palette from the page body, but pressing `/` while a text field is focused does
    **not** open it and enters the `/` as text (the editable-target guard);
  - type to filter and assert the first match becomes `aria-activedescendant`;
  - ArrowDown/ArrowUp move the active option while DOM focus stays on the input - assert
    `document.activeElement` is the combobox input throughout and no option element ever holds
    DOM focus;
  - Enter on a Page option navigates to its route;
  - while the palette is open the background is unreachable: Tab cycles stay inside the palette and
    neither Tab nor a pointer click reaches the rail, topbar, or main controls (`.fb-rail` and
    `.fb-stage` are both inert), covering the background-not-reachable requirement and A2;
  - Escape closes the palette and, on the drawer-open path where the rail entry is visible, focus
    returns to the `.fb-search-entry` opener; and Escape closes **only** the palette, proven on that
    path: at a sub-1024px viewport open the mobile rail drawer, open the palette over it with
    `Ctrl-K`, press Escape once, and assert only the palette closed, the drawer stayed open, and
    focus landed on the visible opener (never a hidden control). Also assert the drawer-closed
    narrow-viewport path: with the drawer closed at a sub-1024px viewport the rail entry is hidden,
    so open the palette with `Ctrl-K`, press Escape, and assert focus lands on the visible fallback
    control (the topbar nav toggle), never a hidden control - covering focus restore on both the
    opener-visible and opener-hidden paths.
  This is the CDP-driven browser path the suite already uses (no hardware needed); it skips
  cleanly without `FREEBOARD_TEST_E2E` + Chromium, like the rest of the suite. These behavioral
  checks - combobox focus retention, arrow nav, Escape restore, the `/` guard, stop-propagation,
  and background inert - are the real accessibility coverage; the served-bundle string markers only
  prove the strings shipped, not behavior, so the WebE2E suite must run in whatever pipeline gates
  this change rather than be left to its default skip.
- **Build/lint**: `dotnet build` (runs the bun asset build), `dotnet test`, and
  `npx markdownlint-cli2` on the change docs.

## Open Questions

- Shared-scope mechanism: Alpine store vs a shell-root `x-data` for coupling the rail opener
  to the sibling palette. Both work; the store decouples the two regions better. Resolve at
  implementation; it does not affect the observable contract.
- Should "Toggle dark mode" reflect current state in its label (e.g. "Switch to light mode")?
  The prototype uses a static "Toggle dark mode". Keeping the static label is simpler and
  matches the reference; a stateful label is a later polish, not a spec requirement.
