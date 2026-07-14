## Why

The E2E accessibility audit (`tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`)
runs axe-core at a maximal bar that includes WCAG AAA (`wcag2aaa`), which enforces
`color-contrast-enhanced` (7:1 for normal text). On the authenticated app-shell
views the neutral secondary-text tokens (`muted`, `faint`) and the built-in
`text-neutral-600` body copy meet WCAG AA (4.5:1) but not AAA on the off-white
`field` ground the app renders content on. Twelve authenticated views fail exactly
one rule, `color-contrast-enhanced`, on three element kinds: the rail group labels
(`.fb-navgroup`, `faint`), the command-palette placeholder (`.fb-search-entry`,
`faint`), and page descriptions and empty states using `text-neutral-600`.

The design-system contract currently promises only AA for token text. The audit bar
is AAA and we are keeping it. The contract is therefore raised to match: the neutral
text tokens meet AAA so the audit passes on their own terms, not by lowering the bar.

## What Changes

- Re-calibrate the neutral secondary-text tokens in both themes so they clear WCAG
  AAA (7:1) against every panel ground they render on (worst case: `field` in light,
  `panel` in dark), keeping the `ink` > `muted` > `faint` luminance ordering:
  - Light `muted` `#4d534f` -> `#464b48`; light `faint` `#66706b` -> `#4a4f4c`.
  - Dark `muted` `#a2aba5` -> `#b5beb8`; dark `faint` `#828c86` -> `#a9b2ac`.
  Mirror the new values into `stories/Colors.stories.js` so the reference matches.
- Migrate the failing page body text off the built-in `text-neutral-600` utility onto
  the `text-muted` token utility, so it resolves to the AAA-calibrated `muted` token.
- Raise the neutral-text half of the source contrast guard
  (`tests/Freeboard.Web.Tests/ContrastGuardTests.cs`) from AA (4.5:1) to AAA (7:1),
  so this regression class is caught by the fast local suite, not only E2E. Semantic
  and brand word text keep the AA (4.5:1) bar; seals and fills keep 3:1.

## Capabilities

### Modified Capabilities

- `web-design-system`: the "Token text pairs meet AA contrast in both themes"
  requirement is raised so the neutral text tokens (`ink`, `muted`, `faint`) meet
  WCAG AAA (7:1) on their grounds. Semantic and brand word text stay at AA (4.5:1);
  seals and fills stay at 3:1. This aligns the token contract with the maximal
  (AAA-inclusive) accessibility audit the web UI is held to.

## Impact

- MIT web work only, in `src/Freeboard` (the web UI). No EE code, no change to the
  reference graph; `Freeboard.Agent` and `Freeboard.CLI` are untouched.
- Affected files: `src/Freeboard/assets/css/app.css` (the light `@theme`, the dark
  `[data-theme="dark"]`, and the re-asserted `[data-theme="light"]` token blocks),
  `src/Freeboard/stories/Colors.stories.js` (mirrored palette values), the audited
  `.cshtml` pages under `Pages/Account`, `Pages/Compliance`, and the Settings-hosted
  admin pages (`text-neutral-600` -> `text-muted`), and
  `tests/Freeboard.Web.Tests/ContrastGuardTests.cs` (neutral-text threshold 7:1).

## Non-goals

- No change to theme activation: dark stays gated behind an explicit
  `data-theme="dark"` override; no `@media (prefers-color-scheme: dark)` block ships.
- No change to the semantic or brand palette: those word tokens keep the AA bar; this
  change does not re-tune status colors.
- No layout, spacing, or component-structure change: only neutral text-color token
  values and the `text-neutral-600` -> `text-muted` class swaps.
- Public auth pages (login, forgot/reset password, setup, logout, MFA challenge) and
  non-audited pages keep their `text-neutral-600` where it already clears AAA on the
  white panel ground; they are out of scope.
