## Why

The adopted "audit ledger" design system lives only in Storybook
(`src/Freeboard/stories/`) as reference-only, story-scoped CSS with literal hex.
The running web UI still uses a three-color theme (blue on white) and a handful of
ad-hoc component classes. Nothing in the app implements the ratified
`web-ux-conventions` capability. Before any page can be restyled (P5) or the app
shell rebuilt (P3), the app needs a real token foundation, both themes, self-hosted
type, and a tokenized component library. This change lands that foundation
(migration phases P1 and P2) behind the existing pages, so later phases build on
tokens instead of a Storybook page that can drift or be deleted.

## What Changes

- Replace the three-color `@theme` block in `src/Freeboard/assets/css/app.css`
  with the full token set sourced from `stories/Colors.stories.js`: brand triad,
  8 neutrals, 5 semantic colors (base + soft ground + on-soft `-ink` text token),
  radii, and elevation. Every token is authored for both light and dark from one set
  (A6). Where the raw palette misses WCAG AA as text (`faint` in both themes; light
  `warn`/`neutral` and dark `brand`/`fail` words on their soft grounds), use adjusted
  accessible tokens and mirror the correction into the Colors story (A1, A6).
- Add the theming mechanism, staged: author BOTH themes in the token set (one set,
  A6), but activate dark ONLY through an explicit `fb-theme=dark` override this change.
  The per-user preference is stored in `localStorage` `fb-theme` (`light|dark|system`)
  and applied via `data-theme` on `<html>`; dark token values override under
  `[data-theme="dark"]` only. This change does NOT emit the
  `@media (prefers-color-scheme: dark)` activation block, so with `fb-theme=system` or
  absent the app renders light - no half-themed dark state reaches un-migrated pages.
  A tiny pre-paint script reads the preference before first paint to avoid a flash of
  the wrong theme. Light token values live in `:root` with a re-asserting
  `[data-theme="light"]` block. The toggle that writes the preference is P3;
  system-default dark activation (`prefers-color-scheme`) is staged to P5 with page
  migration. This change ships only the read/apply half.
- Self-host Schibsted Grotesk (400/500/600/700) and IBM Plex Mono (400/500/600)
  via `@font-face` with `font-display:swap`, vendored under the web project, wired
  to `--font-sans` / `--font-mono`. No runtime Google Fonts.
- Keep the old `--color-brand` / `--color-brand-hover` / `--color-danger` names as
  aliases through this change so existing pages keep building. (They are removed in
  P5, out of scope here.)
- Rebuild the `fb-*` component set from `stories/_marks.js` in `app.css`
  `@layer components`, every literal hex replaced by a token so both themes work
  for free.
- Reconcile the class systems: keep the established bare names that pages and tests
  key on (`btn-primary`, `badge`, `badge-*`, `card`) and redefine them against the
  new tokens; introduce `fb-*` only for genuinely new marks (seals, stamps, tags,
  chips, owners, due dates, panels, feed, sparklines). Retokenize EVERY live component
  class (buttons, badges, card, `form-*`, `notice*`, `link`, `nav-link*`, `menu-item`,
  `app-card`, `acct-tab*`) so it themes in both modes - today they use Tailwind
  built-in colors that do not switch on `data-theme`. Keep the names; do not restyle
  markup. One class per job, one naming convention, with a short rule note at the top
  of the components layer.
- Add an ASP.NET tag-helper mechanism for the high-frequency marks that carry an
  invariant or ARIA (status seal, provenance stamp, badge, tag, due date, chip,
  owner). `<fb-status>` takes a typed `StatusKind` mapping to the canonical word +
  tone + ARIA in one place so word and color cannot disagree (S1/S3); the generic
  tint helpers take a `MarkTone` enum. Helpers emit the `fb-*`/redefined classes, so
  `app.css` stays the single style source.

## Capabilities

### New Capabilities

- `web-design-system`: the concrete, testable design-system surface of the web UI -
  the light/dark token set from one source, the personal-override theming mechanism
  and its pre-paint application (dark reachable via explicit override; system-default
  dark activation staged to page migration), self-hosted typography, the tokenized
  component classes (no literal brand/status hex), and the tag-helper marks that emit
  those classes with typed, invariant-safe tone. This is the implementation contract
  that `web-ux-conventions` rules (A1, A6, S2, S3, P1, P2, L2, T6, W1) are tested
  against for the tokens/components layer.

### Modified Capabilities

- None. This change implements `web-ux-conventions`; it does not alter any of its
  requirements. It ships behind existing pages, so no other capability's observable
  behavior changes.

## Impact

- MIT web work only, in `src/Freeboard` (the web UI). No EE code, no change to the
  reference graph; `Freeboard.Agent` and `Freeboard.CLI` are untouched.
- Affected files: `src/Freeboard/assets/css/app.css` (token block + components
  layer), `src/Freeboard/Pages/Shared/_Head.cshtml` (pre-paint script, font
  preload), new vendored font files and their license files under
  `src/Freeboard/wwwroot/fonts/`, and new tag helpers under
  `src/Freeboard/TagHelpers/` with unit tests under `tests/Freeboard.Web.Tests/`.
- Existing tests that key on `btn-primary`, `badge`, `badge-danger`,
  `badge-success`, `temp-password`, `soa-nodes`, and `data-node-id` must stay green;
  those markers are preserved, not renamed.

## Non-goals

- Page migration (P5): no existing `.cshtml` page is restyled or re-homed here.
- App shell (P3): the 236px nav rail, breadcrumb topbar, workspace switcher, theme
  toggle UI, and nav information architecture are not built here. P1 wires the
  theme storage/read; the toggle control lands with the shell.
- System-default dark activation (P5): no `@media (prefers-color-scheme: dark)` block
  ships here. Dark is authored and reachable via an explicit `fb-theme=dark` override;
  auto-applying dark from the OS preference lands with page migration.
- Interaction primitives (P4): command palette and object-detail drawer.
- Placeholder destinations (P6) and the conformance/drift suite (P7).
- Removing the legacy color aliases (P5) and building all ~40 marks as tag helpers
  (single-caller marks stay CSS classes until a second caller appears).
