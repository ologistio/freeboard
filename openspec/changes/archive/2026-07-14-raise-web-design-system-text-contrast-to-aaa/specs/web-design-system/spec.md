## MODIFIED Requirements

### Requirement: Token text pairs meet AA contrast in both themes

The neutral text tokens (`ink`, `muted`, `faint`) SHALL meet WCAG AAA contrast (at
least 7:1 for normal text) against every panel ground they render on (`panel`,
`field`, `panel-dim`) in both the light and the dark theme, because they carry the
UI's body and label copy and the accessibility audit the web UI is held to includes
WCAG AAA. Every other text-bearing token pairing used by the component layer (the
semantic `-ink` status words and the `brand-ink` word) SHALL meet WCAG AA (at least
4.5:1 for normal text) in both themes, and every non-text seal or fill SHALL meet at
least 3:1 against its ground. Where a raw palette value does not clear its required
bar as text, the design system SHALL use an adjusted token rather than the raw value:
status word text SHALL use the semantic on-soft `-ink` token (not the semantic base,
which remains the non-text seal fill), and any neutral text weight that does not clear
7:1 SHALL be corrected while keeping the `ink` darker/lighter than `muted` darker/
lighter than `faint` luminance ordering. Corrections SHALL be mirrored into
`stories/Colors.stories.js` so the reference matches the app. This implements
`web-ux-conventions` A1 and the both-themes part of A6.

#### Scenario: Neutral text tokens clear AAA in both themes

- **WHEN** the contrast ratio is computed from the token values for each neutral text
  weight (`ink`, `muted`, `faint`) on each panel ground (`panel`, `field`,
  `panel-dim`) in light and in dark
- **THEN** every such pair is at least 7:1

#### Scenario: Load-bearing status and brand text pairs clear AA in both themes

- **WHEN** the contrast ratio is computed from the token values for the semantic and
  brand word pairs (each status word on its soft ground and on `panel`, and the brand
  word on its soft ground and on `panel`) in light and in dark
- **THEN** every such pair is at least 4.5:1, and every seal or fill is at least 3:1
  against its ground

#### Scenario: Status word uses the accessible on-soft token

- **WHEN** a status mark renders its word on a soft ground
- **THEN** the word text takes the semantic `-ink` token that clears AA, while the
  seal fill takes the semantic base
