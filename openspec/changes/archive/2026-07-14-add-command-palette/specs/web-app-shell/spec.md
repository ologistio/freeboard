## RENAMED Requirements

- FROM: `### Requirement: The command-palette entry is a static affordance this phase`
- TO: `### Requirement: The command-palette entry opens the palette`

## MODIFIED Requirements

### Requirement: The command-palette entry opens the palette

The nav rail SHALL show a single command-palette entry carrying the `Ctrl K` hint, and it
SHALL be the only global search or ask entry point in the chrome. The entry SHALL open the
command palette: it SHALL advertise the dialog it opens by setting `aria-haspopup="dialog"`,
and activating it (by click or keyboard) SHALL open the palette over a dim scrim. No second
search box SHALL appear elsewhere in the chrome. The entry SHALL remain keyboard-focusable - a
button in the tab order - and SHALL NOT be a `disabled` control, because a disabled control is
not focusable and would break the full keyboard path (A2). The palette behavior itself is
specified by the `web-command-palette` capability. This implements `web-ux-conventions` N7 for
the chrome's single search surface, keeping A2.

#### Scenario: Single palette entry with the shortcut hint

- **WHEN** the chrome renders
- **THEN** exactly one command-palette entry is present, it carries the `Ctrl K` hint, and no
  other global search or ask box appears

#### Scenario: Entry opens the palette

- **WHEN** the viewer activates the command-palette entry
- **THEN** the command palette opens, the entry advertises it with `aria-haspopup="dialog"`,
  and the entry stays keyboard-focusable (not a `disabled` control) so the tab order is
  unbroken
