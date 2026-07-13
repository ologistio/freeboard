## 1. Token foundation (feat(web): full token set, both themes)

- [x] 1.1 Replace the three-color `@theme` block in
  `src/Freeboard/assets/css/app.css` with the full light token set from
  `stories/Colors.stories.js`: brand triad, 8 neutrals, 5 semantic (base + soft +
  a new on-soft `-ink` word token each), the non-semantic component color
  `--color-on-brand` (button label on the solid brand fill, replacing the literal
  white), `--r-sm`/`--r`,
  and the elevation shadow tokens (`--shadow`, plus any additional shadow token the
  marks need). Every color a `@layer components` rule body will reference must be a
  token declared here (or in the `[data-theme]` blocks) so the layer holds no literal.
- [x] 1.2 Apply the D-contrast corrections so text pairs clear AA: add
  `--color-{ok,warn,fail,info,neutral}-ink` used for status word text (base stays the
  seal fill), and darken `faint` in BOTH themes to clear AA on its grounds (light
  candidate #66706b; dark #7d8781 -> #828c86, which clears 4.5 on dark
  `panel`/`field`/`panel-dim`). Mirror the corrections into `stories/Colors.stories.js`
  - a STRUCTURAL change, not just value edits:
  - Add a per-semantic on-soft `-ink` field (light + dark) to each SEMANTIC entry
    (today `{n,word,l,d,sl,sd,u}`, no `-ink`).
  - Change `semanticCard` to paint the status WORD from the new `-ink` field, not
    `t.l` (the base on the soft ground, which is exactly the failing pattern); keep the
    8px seal filled from the base.
  - Flow the light + dark `faint` change into the NEUTRALS `faint` entry (`l` and `d`).
  The in-code components-layer comment states the REASON only - a deliberate
  AA-contrast correction from the ratified palette (a deliberate deviation, permitted
  by `comment-etiquette.md`) - NOT an old-hex -> new-hex history (barred as a
  changelog). Keep the precise old/new hex deltas in design.md and the story delta.
- [x] 1.3 Author the full dark token set as an override under `[data-theme="dark"]`
  ONLY, plus a `[data-theme="light"]` block that re-asserts the light values. Do NOT
  emit an `@media (prefers-color-scheme: dark)` activation block in this change: dark
  is authored (one set, A6) but reachable only via an explicit `fb-theme=dark` override,
  so a dark system preference does not auto-apply to un-migrated pages (system-default
  dark activation is staged to page migration). `[data-theme="light"]` and
  `[data-theme="dark"]` are equal specificity, so an explicit `data-theme="light"` wins
  by SOURCE ORDER: author the light re-assertion AFTER (or use `html[data-theme="..."]`
  to raise specificity deliberately).
- [x] 1.4 Keep `--color-brand`, `--color-brand-hover`, `--color-danger` as aliases
  onto the new tokens so existing utilities and pages keep building.
- [x] 1.5 Build the assets (`cd src/Freeboard && bun run build`) and confirm the
  compiled `app.css` resolves every token in both themes.

## 2. Theming mechanism and pre-paint script (feat(web): system-default theme with personal override)

- [x] 2.1 Add the pre-paint snippet to `src/Freeboard/Pages/Shared/_Head.cshtml`,
  before the stylesheet link, that reads `localStorage` `fb-theme` (tri-state
  `light|dark|system`): set `data-theme` on `<html>` for `light`/`dark`, set nothing
  for `system` or an absent/unrecognised value. Because this change emits no
  `@media (prefers-color-scheme: dark)` block, `system`/absent renders light; the
  system-default dark activation is staged to page migration. Keep the snippet static
  (no request-time interpolation) so it has a stable hash for a future CSP. No setter
  in this change - the toggle that writes `fb-theme` is P3.
- [x] 2.2 Verify no flash: load a page with a dark override set and confirm the first
  painted frame is dark.

## 3. Self-hosted typography (feat(web): vendor Schibsted Grotesk and IBM Plex Mono)

- [x] 3.1 Vendor the `.woff2` faces under `src/Freeboard/wwwroot/fonts/`: Schibsted
  Grotesk 400/500/600/700, IBM Plex Mono 400/500/600. Vendor the EXACT upstream OFL
  1.1 license file for BOTH families next to the fonts, unmodified: Schibsted Grotesk
  `OFL.txt` from `google/fonts`, IBM Plex Mono `LICENSE.txt` from `IBM/plex`. Record
  per face whether the vendored file is a Latin subset or a full multi-script face.
- [x] 3.2 Add `@font-face` declarations (`font-display:swap`) in `app.css` and wire
  `--font-sans` / `--font-mono` to the two families. If the vendored faces are Latin
  subsets (the default), declare a matching `unicode-range` on each `@font-face`; if a
  face is a full multi-script file, omit `unicode-range` and note its accepted size.
- [x] 3.3 Preload the two highest-use faces (sans 400 and 500) in `_Head.cshtml` with
  the full attribute set so the preload matches the CORS-mode `@font-face` request and
  the browser does not fetch twice:
  `<link rel="preload" as="font" type="font/woff2" href="..." crossorigin>`.
- [x] 3.4 Confirm no runtime font request targets a third-party origin.

## 4. Component library from tokens (feat(web): tokenized fb-* component layer)

- [x] 4.1 In `app.css` `@layer components`, port the `fb-*` marks from
  `stories/_marks.js` (page header, buttons, table + marks, status seal, stamp, tag,
  owner/avatar, due date, chip, tabs, panels/stats/bars/framework, feed, report
  marks, toolbar/search, guidance), replacing every literal brand/status hex with a
  token. Omit shell-only marks (rail, topbar, navitem, drawer) - those belong to P3/P4.
- [x] 4.2 Redefine the established tested names against the new tokens without
  creating `fb-*` twins: `btn-primary/secondary/danger/sm` (+ new `btn-quiet`),
  `badge` + `badge-*`, `card`. Fold `fb-tbtn` into `btn-sm`.
- [x] 4.3 Retokenize EVERY remaining live component class in `app.css` so it themes in
  both modes. These classes currently reference Tailwind BUILT-IN colors (`neutral-*`,
  `white`, `green-*`, `amber-*`, `ring-neutral-*`), which do NOT switch on
  `[data-theme="dark"]`, so as written they render light-only under an explicit dark
  override (half-themed dark for the tokenized classes, and A6 fails for them). Change
  only the COLOR SOURCE of each class definition to the new semantic tokens; keep the
  class NAMES and do NOT restyle any page markup (markup stays P5). Classes:
  `form-label`, `form-input`, `btn-secondary` (its `focus:ring-neutral-300`), `link`,
  `notice`, `notice-error`, `nav-link`, `nav-link-active`, `nav-section-title`,
  `menu-item`, `app-card`, `acct-tab`, `acct-tab-active`. Map by role:
  `neutral-900` -> `ink`; `neutral-700`/`neutral-600` -> `ink` or `muted` by emphasis;
  `white`/`bg-white` -> `panel` (and the button label on the solid `brand` fill ->
  `on-brand` token, not literal white); `bg-neutral-50`/`bg-neutral-100` -> `panel-dim`;
  `border-neutral-*` -> `line` or `line-strong`; `ring-neutral-300` -> `ring-line-strong`
  (the focus-ring tone); `green-*` -> `ok`/`ok-ink`; `amber-*` -> `warn`/`warn-ink`;
  `danger` -> `fail` (D2 alias). No live class may keep a built-in color or literal for
  a themed surface - there is no rule-body color exception.
- [x] 4.4 Add a SHORT reconciliation comment at the top of the components layer stating
  only the one-class-per-job rule and the non-obvious "why established names are
  redefined, not twinned"; cite rule IDs (S2/S3/T6/L2) which are permanent. Do NOT
  paste the full D5 map and do NOT use any phase labels (P3/P4/P5/P7) - those are
  process references barred from source. The full map lives in design.md only. Confirm
  one class per job, no duplicates.
- [x] 4.5 Build assets and confirm existing pages still render: light-mode palette
  migration visible on all pages, and the retokenized component/shell classes correct
  under an explicit `fb-theme=dark` override (palette source shift only, no markup
  change). Un-migrated page markup on hard color utilities stays light until P5.

## 5. Tag-helper mechanism and marks (feat(web): invariant-safe mark tag helpers)

- [x] 5.1 Add two typed enums under `src/Freeboard/TagHelpers/`. `StatusKind` encodes
  the COMPLETE S1 status vocabulary as one canonical member per status - `Passing`
  (alias Ready), `Failing`, `DueSoon`, `Overdue`, `Drifting` (alias Degraded),
  `Snoozed`, `Waiting`, `Draft`, `OutOfScope` - so every S1 status maps to a member and
  no label outside S1 is representable; for `<fb-status>`. `MarkTone { Neutral, Brand,
  Ok, Warn, Fail }` for the generic tint helpers `<fb-badge>` and `<fb-tag>`, its
  members matching one-to-one the tint classes they emit
  (`badge-neutral/brand/success/warn/danger` and `fb-tag` base/`--brand`/`--ok`/`--warn`/`--fail`);
  no `Info` member (no info tint class exists), `Brand` present (badge and tag both
  carry a brand tint). `<fb-chip>` takes a selected state and count, not a tone.
- [x] 5.2 Build the seven high-frequency tag helpers, each emitting the canonical
  class(es) and required ARIA/shape: `<fb-status>`, `<fb-stamp>`, `<fb-badge>`,
  `<fb-tag>`, `<fb-due>` (relative/absolute + overdue in words), `<fb-chip>`,
  `<fb-owner>`. `<fb-status>` takes a `StatusKind` and maps it to the canonical WORD +
  tone + ARIA in ONE place, so the word and color cannot disagree and S3 (red only for
  failing/overdue) holds by construction - a bare tone attribute would still allow
  `<fb-status tone="Fail">Due soon</fb-status>`. `<fb-badge>` and `<fb-tag>` take
  `MarkTone` (each member maps to its one emitted tint class); red is reachable only
  through `Fail`. `<fb-chip>` takes a label, count, and boolean selected state, not a
  tone.
- [x] 5.3 Leave all other marks as CSS classes (no single-caller helpers).

## 6. Guards and tests (test(web): contrast, no-literal-hex, tag helpers)

- [x] 6.1 Add a no-literal-color guard that scans the `@layer components` rule bodies
  of the SOURCE `assets/css/app.css` (not the gitignored, minified
  `wwwroot/css/app.css`) and fails on ANY literal color value - hex, `rgb()`/`rgba()`,
  `hsl()`/`hsla()`, or a CSS named color, not only brand/status hex. Literal color is
  permitted in EXACTLY TWO places: inside a custom-property declaration (`--x: value`,
  in the `@theme` and `[data-theme="light"]`/`[data-theme="dark"]` blocks) and inside
  `@font-face`. Forbid it in every `@layer components` rule body with ZERO rule-body
  exceptions: shadow color references a token (`--shadow`, `--color-on-brand`,
  `var(--color-...)`). No "documented shadow" escape hatch.
- [x] 6.2 Add a built-in color-UTILITY guard: the guard in 6.1 catches literals but
  not Tailwind built-in palette utilities (class names, not literals). Assert that the
  `@layer components` rule bodies contain no built-in palette utility token - pattern
  `neutral-\d`, `\bwhite\b` used as a color utility, `green-`, `amber-`, `red-`,
  `blue-` used as colors - only tokenized color utilities (`bg-panel`, `border-line`,
  `text-ink`, ...) or `var(--color-...)`. This is the machine check behind the D5
  retokenize completeness (no page/class left light-only).
- [x] 6.3 Add a contrast guard that computes WCAG ratios from the token values parsed
  once from the SAME single source - the source `assets/css/app.css` `@theme` (light)
  and `[data-theme="dark"]` (dark) declaration blocks (soft grounds composited over
  their panel). Dark is checked from its authored token set even though not
  system-activated this phase. In BOTH themes: text pairs (`ink`/`muted`/`faint` on
  `field`/`panel`/`panel-dim` - so dark `faint` on `panel` is asserted >= 4.5 - each
  semantic `-ink` word on its soft ground and on `panel`, `brand-ink` on `brand-soft`,
  `on-brand` on `brand`) assert >= 4.5; seal/fill pairs (each semantic base on its soft
  ground) assert >= 3.0.
- [x] 6.4 Add a font-origin guard that boots the app over `WebApplicationFactory`,
  fetches the served `/css/app.css` and the rendered `<head>`, and asserts every font
  request targets the app origin (no `fonts.googleapis.com` / `fonts.gstatic.com`).
  Read the served responses, not a gitignored path on disk.
- [x] 6.5 Add a pre-paint CSP-shape guard: render the head and assert (a) the theme
  snippet precedes the stylesheet link and (b) the snippet text is BYTE-IDENTICAL
  across two requests made with different `fb-theme` / user state (proving no
  request-time interpolation), so it is hash-allowlistable.
- [x] 6.6 Add a cascade / staged-activation guard: prove `data-theme="light"` renders
  the light tokens and `data-theme="dark"` the dark tokens (override reaches both
  sets), and that with NO explicit override the app renders light - the source emits no
  `@media (prefers-color-scheme: dark)` block, so a dark system preference does not
  auto-apply dark this phase.
- [x] 6.7 Add unit tests per tag helper: correct classes, ARIA, and tone mapping.
  Assert `<fb-status>` maps each `StatusKind` to the canonical word + tone + ARIA and
  that a word/tone that disagree is unrepresentable; assert `StatusKind` has a member
  for every S1 status (completeness), so the vocabulary cannot be under-covered. Assert
  `<fb-badge>` and `<fb-tag>` map each `MarkTone` member to its one emitted tint class
  and that every emittable tint class has a `MarkTone` member (no tone without a class,
  no class without a tone). Status emits shape + word (S2), due emits "overdue" in words
  (T6), provenance names source and age (web-ux P1/P2 provenance), illegal `MarkTone`
  combinations are unrepresentable by construction.

## 7. Verification (chore(web): confirm build and suites green)

- [x] 7.1 `dotnet build` succeeds (asset build runs via `BuildAssets`).
- [x] 7.2 `dotnet test tests/Freeboard.Web.Tests` passes with the preserved markers
  intact (`btn-primary`, `badge`, `badge-danger`, `badge-success`, `temp-password`,
  `soa-nodes`, `data-node-id`).
- [x] 7.3 `npx markdownlint-cli2 "**/*.md"` clean for any docs touched.
