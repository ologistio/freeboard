# web-object-drawer Specification

## Purpose

Open a list object's full detail in a right-anchored ARIA dialog layered over the list, so the
viewer inspects a record without losing the list scroll position or filter state behind it. The
drawer renders the same server-authored anatomy as the full-page detail view and degrades to a
direct navigation to that page when JavaScript is unavailable, giving one accessible, keyboard-
and screen-reader-safe detail surface across both paths. This delivers `web-ux-conventions` O4
and A5.

## Requirements
### Requirement: The object-detail drawer is a right-anchored ARIA dialog

Opening an object from a list SHALL show a right-anchored panel that is an ARIA dialog:
`role="dialog"`, `aria-modal="true"`, and `aria-labelledby` referencing the object title in
its header. The drawer SHALL open over a dim scrim. When closed the drawer SHALL be `inert`
so its controls stay out of the tab order and off the accessibility tree, and it SHALL become
reachable only while open. This delivers `web-ux-conventions` O4 and A5.

#### Scenario: Opening a list object shows the labelled dialog over a scrim

- **WHEN** the viewer activates a list object that opens in the drawer
- **THEN** a right-anchored `role="dialog"` panel with `aria-modal="true"` and
  `aria-labelledby` pointing at its title appears over a dim scrim

#### Scenario: The closed drawer is inert

- **WHEN** the drawer is closed
- **THEN** it carries `inert`, so neither keyboard focus nor a pointer can reach its
  controls and an accessibility audit skips it

### Requirement: The drawer traps and restores focus and closes on Escape and scrim click

The drawer SHALL trap focus while open and hold the entire background chrome inert - the nav
rail, the topbar, and the main content - so neither keyboard focus nor pointer interaction can
reach the obscured page. Focus SHALL move into the drawer on open. The drawer SHALL close on Escape and
on a click of the scrim outside the panel. On close, focus SHALL return to the control that
opened it when that control is still visible, and otherwise to a visible fallback control, so
focus never lands on a hidden or detached element. The Escape that closes the drawer SHALL be
consumed - its propagation stopped - so it closes only the drawer and does not also dismiss any
overlay behind it. This focus behavior SHALL be provided by the shared focus-overlay primitive,
composed - not reimplemented. This delivers `web-ux-conventions` A5 and A2.

#### Scenario: Focus moves in on open and the background is inert

- **WHEN** the drawer opens
- **THEN** focus moves into the drawer, and the nav rail, topbar, and main content are inert
  so Tab cycles stay inside the drawer and no background control is reachable

#### Scenario: Escape and scrim click close and restore focus to the opener

- **WHEN** the drawer is open and the viewer presses Escape or clicks the scrim outside the
  panel
- **THEN** the drawer closes and focus returns to the opener when it is visible, otherwise to
  a visible fallback control, never to a hidden or detached element

#### Scenario: Escape closes only the drawer

- **WHEN** the drawer is open over another overlay and the viewer presses Escape once
- **THEN** only the drawer closes; the Escape does not propagate to the overlay behind it

#### Scenario: The focus primitive is reused, not duplicated

- **WHEN** the drawer's open and close focus behavior is implemented
- **THEN** it composes the same focus-overlay primitive the command palette uses rather than
  reimplementing opener capture, background inerting, Escape handling, or focus restore

### Requirement: The drawer renders the uniform object anatomy in the fixed order

The drawer SHALL render the uniform object anatomy in the fixed order: an eyebrow and title,
the status, then the assertion or description, relations, evidence, guidance, and history,
closing with actions. Every object opened in the drawer SHALL use this same order. A facet the
object lacks SHALL be shown as explicitly empty, not omitted. This delivers
`web-ux-conventions` O3 and O2.

#### Scenario: Anatomy order is uniform

- **WHEN** any object is opened in the drawer
- **THEN** its sections appear in the fixed order eyebrow and title, status, assertion,
  relations, evidence, guidance, history, then actions

#### Scenario: A missing facet renders as empty

- **WHEN** an object lacks a value for a standard facet
- **THEN** that facet is shown as explicitly empty rather than omitted from the anatomy

#### Scenario: The control-level status facet is never shown as passing

- **WHEN** a control is opened in the drawer or as a full page and no control-level evaluated
  status exists for it
- **THEN** the anatomy's control-level status facet renders as an explicit empty ("not
  evaluated") rather than a fabricated passing status; any evaluated per-check status (including a
  degraded-on-stale signal) comes from the per-collector evidence-status read, not the drill-down
  projection, and appears only in the evidence or proving-checks relations rows, and is not
  aggregated into a control-level pass. This delivers `web-ux-conventions` S6.

### Requirement: An object opens in the drawer from a list and as a full page from a direct link

The same object SHALL open in the drawer from a list and render as a full page from a direct
link, and both SHALL present the same record through the same facet anatomy - the eyebrow and
title through history. That facet content SHALL be produced by one shared anatomy partial so it
cannot diverge. The actions region is context-dependent: the drawer offers an "Open full page"
link, while the full page omits that self-link; both keep the actions region present (O2) with no
fabricated mutating action. Without JavaScript the list affordance SHALL be a link that navigates
to the full page, so the object is always reachable. This delivers `web-ux-conventions` O4.

#### Scenario: Drawer and full page render one record

- **WHEN** an object is opened from its list drawer and from its direct URL
- **THEN** both present the same record with the same facet anatomy (eyebrow through history),
  produced by the one shared partial; only the actions region differs by context - the drawer's
  "Open full page" link versus the full page without that self-link - and each keeps a present
  actions region with no fabricated mutating action

#### Scenario: The list affordance works without JavaScript

- **WHEN** JavaScript is disabled and the viewer activates the list affordance
- **THEN** the browser navigates to the object's full-page detail rather than doing nothing

### Requirement: The Statement of Applicability list opens its control objects in the drawer

On the Statement of Applicability page each control object SHALL be a drawer opener: activating
it SHALL open the drawer showing that control's anatomy, and a direct link to the same control
SHALL render the identical anatomy full-page. The control anatomy SHALL be server-rendered and
present in the page response (no client fetch of additional data), consistent with the page's
server-rendered drill-down; the drawer SHALL reveal that content rather than requesting it. All
existing Statement of Applicability behavior - the drill-down disclosure, scoping,
authorization boundary, read-only mode, and store-unreachable notice - SHALL continue to hold,
and the page's existing row markers SHALL be preserved. Because the page is read-only, the
drawer's actions region SHALL carry no mutating action. The full-page detail SHALL authorize the
requested control against the caller's full accessible organisation set - not the active list
scope or org-selection cookie - so a direct link to any accessible control renders regardless of
which org the active scope currently selects; a control that is missing or outside the accessible
set SHALL render a not-found response that does not leak the names of records the caller cannot
see. Only one top-level overlay SHALL be active at a time: while the drawer is open the command
palette shortcut SHALL be suppressed, so the drawer remains the single top-level overlay until it
closes.

#### Scenario: A control row opens the drawer with its record

- **WHEN** the viewer activates a control on the Statement of Applicability page
- **THEN** the drawer opens showing that control's uniform anatomy

#### Scenario: The control's direct link renders the same anatomy full-page

- **WHEN** the viewer opens the control's direct URL
- **THEN** the page renders that control's identical anatomy full-page inside the shell

#### Scenario: Drawer content is server-rendered, not fetched

- **WHEN** the initial page response is inspected before any client script runs
- **THEN** the control anatomy the drawer shows is already present in the HTML, and opening
  the drawer issues no new server request

#### Scenario: Existing page behavior is preserved

- **WHEN** the drawer wiring is present
- **THEN** the drill-down disclosure, scope selection, authorization boundary, read-only mode,
  store-unreachable notice, and the existing row markers all continue to work unchanged

#### Scenario: An inaccessible or missing control does not leak

- **WHEN** the caller opens a direct control URL that is missing or outside the caller's
  accessible organisations
- **THEN** the page renders a not-found response and does not disclose the name or any facet of
  a record the caller is not authorized to see

#### Scenario: A direct link authorizes against the accessible set, not the active scope

- **WHEN** the caller's active-org selection is org A and the caller opens a direct control URL
  for a different organisation B that is within the caller's accessible set
- **THEN** the control detail for B renders, while a direct URL for an organisation outside the
  accessible set returns not-found - authorization follows the accessible organisation set, not
  the active scope selection

#### Scenario: Opening the drawer suppresses the command palette

- **WHEN** the drawer is open and the viewer presses the command-palette shortcut
- **THEN** the palette does not open; the drawer remains the single active top-level overlay
  until it is closed

### Requirement: The drawer is styled from tokens only in both themes with reduced motion honored

The drawer, scrim, and anatomy SHALL be styled from design tokens only, with no literal color
value, so they render correctly in both the light and dark themes from the one token set. Dark
SHALL activate only via the `data-theme` override, with no `prefers-color-scheme` activation.
The drawer's open and close slide transition SHALL be disabled under a reduced-motion
preference (A3). Text and interactive elements in the drawer SHALL meet the AA/AAA contrast bar
in both themes (A1/A6). This delivers `web-ux-conventions` A1, A3, and A6.

#### Scenario: Both themes from one token set, no literal color

- **WHEN** the drawer and anatomy styles are inspected
- **THEN** every color is a design token, no `prefers-color-scheme` rule activates dark, and
  the drawer meets the contrast bar in both light and dark

#### Scenario: Reduced motion disables the slide

- **WHEN** the viewer has requested reduced motion and opens or closes the drawer
- **THEN** the drawer appears and disappears without an animated slide

