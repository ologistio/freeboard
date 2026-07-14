# web-command-palette Specification

## Purpose
The command palette is the app chrome's single global search-or-ask surface (web-ux-conventions
N7): one keyboard- and pointer-reachable overlay, built as an ARIA combobox over a listbox, that
filters and runs tagged results - jumping to a gated nav destination (Page) or running an in-app
Command such as toggling dark mode. It also defines the reusable focus-overlay primitive (opener
capture, background inert, Escape handling, and focus restore) that later overlays share.
## Requirements
### Requirement: One command palette is the single search-or-ask surface

There SHALL be exactly one command palette, and it SHALL be the only global search or ask
entry point in the app chrome. It SHALL open over a dim scrim from the nav rail's single
command-palette entry and from a document-level keyboard shortcut. There SHALL NOT be a second
global search or ask box anywhere else in the chrome. This delivers `web-ux-conventions` N7.

#### Scenario: Only one global search surface exists

- **WHEN** the authenticated app chrome is rendered
- **THEN** exactly one command-palette entry is present and it is the only global search or
  ask affordance; no second search box appears elsewhere in the chrome

#### Scenario: The entry opens the palette

- **WHEN** the viewer activates the command-palette entry
- **THEN** the command palette opens over a dim scrim with its search input focused

### Requirement: Results are tagged by kind

Each palette result SHALL carry a visible tag naming its kind. Page (jump to a nav destination)
and Command (run an in-app action such as toggling dark mode) are the two live kinds this phase.
The kind vocabulary SHALL reserve an Agent kind (ask the assistant) for when an assistant
exists; until then no Agent result SHALL be shown. The "ask the assistant" clause of
`web-ux-conventions` N7 is aspirational this phase - not built - because no assistant backend
exists; the palette SHALL NOT show a result that leads to a feature that does not exist, and
SHALL NOT show a placeholder or coming-soon Agent row.

#### Scenario: Page and Command results are tagged

- **WHEN** the palette renders its results
- **THEN** each result shows a tag identifying it as Page or Command

#### Scenario: No Agent result until an assistant exists

- **WHEN** the palette renders its results and no assistant backend exists
- **THEN** no Agent-tagged result is shown - not even a placeholder or coming-soon row - and no
  result routes to a non-existent assistant

### Requirement: The page index is the gated nav catalog

The palette's Page results SHALL be sourced from the same server-side nav catalog that drives
the rail, resolved for the current request, so the palette and the rail cannot disagree. A
destination the viewer cannot reach - dropped by an authorization gate or an enterprise
entitlement - SHALL NOT appear in the palette, exactly as it does not appear in the rail. The
palette SHALL NOT maintain a second, independent list of pages.

#### Scenario: Palette pages match the rail's visible pages

- **WHEN** the palette renders for a viewer
- **THEN** its Page results are exactly the nav destinations that viewer can reach, with no
  page the rail hides for that viewer

#### Scenario: A gated destination never leaks into the palette

- **WHEN** a viewer lacks the entitlement or authorization for a destination
- **THEN** that destination appears in neither the rail nor the palette

### Requirement: ARIA combobox and listbox with active-descendant tracking

The palette SHALL be built as an ARIA combobox over a listbox. The dialog SHALL contain a
`role="combobox"` text input with `aria-expanded="true"`, `aria-controls` referencing the
listbox, `aria-autocomplete="list"`, and `aria-activedescendant` referencing the highlighted
option. The results SHALL be a `role="listbox"` whose rows are `role="option"` with
`aria-selected` reflecting the highlight. DOM focus SHALL remain on the input for the entire
time the palette is open; DOM focus SHALL NOT move onto an option element. Moving the
highlight SHALL update `aria-activedescendant` and `aria-selected`, not DOM focus.

#### Scenario: Input keeps focus while active-descendant tracks the highlight

- **WHEN** the palette is open and the viewer moves the highlight or filters
- **THEN** DOM focus stays on the input, `aria-activedescendant` points at the highlighted
  option, and that option carries `aria-selected="true"` while no option holds DOM focus

### Requirement: Keyboard operation

The palette SHALL open from a document-level shortcut - `Ctrl-K`, `Cmd-K`, or `/` - from
anywhere in the app. The `/` shortcut SHALL be ignored while focus is in a text input,
textarea, or contenteditable element, so it stays typeable there; `Ctrl-K` and `Cmd-K` SHALL
open regardless. While open, typing SHALL filter the results by a case-insensitive substring
match and the first match SHALL become the highlighted option; ArrowDown and ArrowUp SHALL
move the highlight; Enter SHALL run the highlighted result; Escape SHALL close the palette.

#### Scenario: Shortcut opens the palette

- **WHEN** the viewer presses Ctrl-K, Cmd-K, or / outside a text field
- **THEN** the palette opens with its input focused and the browser default for that key is
  suppressed

#### Scenario: Slash stays typeable in a text field

- **WHEN** focus is in a text input, textarea, or contenteditable and the viewer presses /
- **THEN** the palette does not open and the / is entered as text

#### Scenario: Type, arrow, and run

- **WHEN** the viewer types to filter, then presses ArrowDown or ArrowUp, then Enter
- **THEN** the list filters with the first match highlighted, the highlight moves with the
  arrows, and Enter runs the highlighted result

### Requirement: Escape and close restore focus to a visible control

Closing the palette SHALL restore DOM focus to the control that opened it when that control is
still visible, and otherwise to a visible fallback control - so focus never lands on a hidden or
detached element. This holds however the palette closes: by Escape, by clicking the scrim outside
the box, or by running a result. While the palette is open the entire background chrome SHALL be held inert
or focus SHALL be trapped, so neither keyboard focus nor pointer interaction can reach the
obscured page behind it. The background chrome here includes the nav rail - its nav links and the
command-palette opener - as well as the topbar and main content, because when the palette is the
overlay the rail is background, not the overlay; all of it SHALL be unreachable while the palette
is open. The Escape that closes the palette SHALL be consumed - its propagation
stopped - so it closes only the palette and does not also close or otherwise affect any other
overlay behind it (for example the mobile nav drawer), which would restore focus to a hidden or
wrong element.

#### Scenario: Escape closes and restores focus

- **WHEN** the palette is open and the viewer presses Escape
- **THEN** the palette closes and focus returns to the control that opened it when that control is
  still visible, and otherwise to a visible fallback control - never a hidden control

#### Scenario: Escape closes only the palette

- **WHEN** the palette is open over other chrome that also closes on Escape (for example the
  mobile nav drawer) and the viewer presses Escape
- **THEN** only the palette closes, the chrome behind it is unaffected, and focus returns to the
  still-visible opener (or a visible fallback control) - never a hidden control

#### Scenario: Background is not reachable while open

- **WHEN** the palette is open
- **THEN** the whole chrome behind it - the nav rail and its opener, the topbar, and the main
  content - is inert or focus is trapped, so Tab and pointer cannot reach any control on the
  obscured page

### Requirement: Running a result

Selecting a Page result SHALL navigate to that destination's route. Selecting a Command result
SHALL run its in-app action and close the palette. Running any result SHALL restore focus per
the focus-restore requirement. The palette SHALL ship at least one Command result, "Toggle
dark mode", wired to the existing theme toggle so it flips and persists the personal theme
without introducing a second theming path.

#### Scenario: Page result navigates

- **WHEN** the viewer runs a Page result
- **THEN** the app navigates to that destination's route

#### Scenario: Toggle dark mode command runs in place

- **WHEN** the viewer runs the Toggle dark mode command
- **THEN** the theme flips and persists as the personal preference, and the palette closes

### Requirement: Reusable focus-overlay primitive

The focus management the palette needs SHALL be implemented as one reusable client-side
primitive, not palette-specific code: capturing the opener, moving focus in on open, holding
the background inert (or trapping focus), closing on Escape while stopping that Escape from
propagating to any overlay behind it, and restoring focus to the opener when it is still visible,
otherwise to a visible fallback control (never a hidden or detached element), on close. The primitive
SHALL be shaped so a later overlay (the object-detail drawer) reuses it without change; the
Escape-stop-propagation behavior SHALL live in the primitive so every overlay that composes it
inherits it. The primitive SHALL NOT own result rendering, filtering, arrow navigation, or
active-descendant tracking; those are the palette's own concern layered on top.

#### Scenario: The primitive is reused, not duplicated

- **WHEN** a second overlay needs open/close focus behavior
- **THEN** it composes the same focus-overlay primitive the palette uses, rather than
  reimplementing opener capture, background inerting, Escape handling, or focus restore

### Requirement: Reduced motion and token-only theming

Any palette open or close transition SHALL be disabled under a reduced-motion preference (A3).
The palette SHALL render from design tokens only, with no literal color values, so it renders
correctly in both the light and dark themes from the one token set. Dark SHALL activate only
via the `data-theme` override (the persisted personal preference), with no
`prefers-color-scheme` activation. The palette's text, tags, keyboard-hint foot, and
highlighted-row combinations SHALL meet the AA/AAA contrast bar in both themes (A1/A6).

#### Scenario: Reduced motion disables the open transition

- **WHEN** the viewer has requested reduced motion and opens or closes the palette
- **THEN** the palette appears and disappears without an animated transition

#### Scenario: Both themes from one token set, no literal color

- **WHEN** the palette styles are inspected
- **THEN** every color is a design token, no `prefers-color-scheme` rule activates dark, and
  the palette meets the contrast bar in both light and dark

