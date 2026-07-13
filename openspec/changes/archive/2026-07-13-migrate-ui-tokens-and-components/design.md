## Context

The migration roadmap (`tmp/ui-migration-critical-path.md`) sequences the work from
the ratified `web-ux-conventions` capability (P0) down to page migration and
conformance. This change is phases P1 (token foundation, typography, theming) and P2
(component library) as one shippable increment. It is pure `src/Freeboard` (web,
MIT) work.

Current state:

- `assets/css/app.css` has a three-color `@theme` (`brand #215091`, `brand-hover`,
  `danger`) and a small `@layer components` set: `btn-primary/secondary/danger/sm`,
  `badge` + `badge-*`, `card`, `app-card`, `nav-link*`, `menu-item`, `acct-tab*`,
  `form-input`, `notice*`, `link`.
- The full palette and the `fb-*` component library exist only in Storybook
  (`stories/Colors.stories.js`, `stories/_marks.js`), story-scoped with literal hex.
- CSS is built by bun/Tailwind v4 (`package.json`: `build:css` runs the Tailwind CLI
  on `assets/css/app.css`), wired into `dotnet build` via the `BuildAssets` MSBuild
  target. `_Head.cshtml` links `~/css/app.css` and defers `~/js/app.js` (Alpine).
- No Content-Security-Policy header is set anywhere in the web project today; a
  strict CSP is planned for a later phase.
- `@addTagHelper *, Freeboard` is already wired in `Pages/_ViewImports.cshtml`; no
  tag helper exists yet. The `OrgSelector` view component is the closest existing
  server-rendered-component pattern.
- Tests key on these classes/attributes and must stay green: `btn-primary`, `badge`,
  `badge-danger`, `badge-success` (AdminUser/CustomRoles page tests), `temp-password`
  (credential display), `soa-nodes` + `data-node-id` (Statement of Applicability).

Constraints: implement `web-ux-conventions` (cite A1, A6, S2, S3, P1, P2, L2, T6, W1);
AA contrast in both themes; no literal brand/status hex in the components layer;
ship behind existing pages (no page migration); MIT web-only, reference graph and
one-way EE rule respected; all `.claude/rules/*.md`.

## Goals / Non-Goals

**Goals:**

- One token set, light and dark, from `stories/Colors.stories.js`, replacing the
  three-color `@theme`, with legacy names aliased so existing pages keep building.
- Both themes authored from one token set; a per-person `data-theme` override applied
  before first paint with no flash. Dark is reachable via an explicit `fb-theme=dark`
  override this change; system-default dark activation (`prefers-color-scheme`) is
  staged to page migration.
- Self-hosted Schibsted Grotesk and IBM Plex Mono, no runtime third-party fetch.
- The `fb-*` component library rebuilt from tokens in `@layer components`, one class
  per job, documented reconciliation map, no literal brand/status hex.
- A tag-helper mechanism plus the seven high-frequency invariant/ARIA marks, tone as
  a typed enum, emitting the shared classes.

**Non-Goals:**

- App shell (P3), interaction primitives (P4), page migration (P5), placeholders
  (P6), conformance suite (P7).
- The theme-toggle UI control (lands with the shell in P3; P1 provides the storage
  read/apply).
- Building all ~40 marks as tag helpers. Single-caller marks stay CSS classes.
- Removing legacy color aliases (P5).

## Plan synthesis (A/B reconciliation)

This change was planned independently twice: Plan A (the Planner, already written as
these artifacts) and Plan B (Codex, from the same repo). This section records what
each contributed and how the divergences were settled; the detail lives in the
decisions below.

- Agreed (both plans, kept): one change covering P1+P2 only; implement
  `web-ux-conventions` without changing it; ship behind existing pages; palette from
  the Colors story; system-default theme with a personal `data-theme` override; a
  pre-paint script before the stylesheet; self-hosted OFL fonts, no runtime Google
  Fonts; a tokenized `@layer components` set with no literal brand/status hex;
  preserve the established class names pages and tests key on; tag helpers only for
  the invariant/ARIA-bearing marks with a typed tone enum; guard tests for
  no-literal-hex, contrast, and the tag helpers.
- Divergences resolved:
  1. Token tiering - Plan A single-tier `@theme` vs Plan B two-tier
     (`--fb-*` runtime + `--color-*` aliases). Kept single-tier (D1).
  2. Contrast - Plan B measured AA failures in the raw palette. Verified against the
     actual values; real for five pairs. Added D-contrast: adjusted on-soft `-ink`
     text tokens plus a darkened light `faint`, synced to the story, with a contrast
     guard.
  3. Preserved classes - Plan B listed more names. Grepped tests and `.cshtml`;
     the preserved set in D5 is now evidence-based with sources.
  4. Theme storage - Plan A `fb-theme` (light/dark, absent=system) vs Plan B
     `freeboard.theme` (light/dark/system). Merged: key `fb-theme`, tri-state
     `light|dark|system` (D3).
  5. app.js theme setter - Plan B put a setter in `app.js` now; Plan A deferred it.
     Deferred to P3 with the toggle UI: nothing in P1/P2 writes the preference (D3).
  6. Guards - took Plan B's fuller set: no-literal-hex, contrast (both themes), font
     origin, pre-paint CSP shape, preserved-marker regression, tag-helper units (D7).

## Decisions

### D1 Token architecture: single-tier CSS custom properties, switched by theme

Decision: single-tier. Author the palette as Tailwind v4 `@theme` custom properties
and switch them by theme. Reject Plan B's two-tier split (`--fb-*` runtime vars plus
`--color-*` Tailwind aliases).

In Tailwind v4 the `@theme` block defines the design tokens, and generated utilities
reference them at runtime as `var(--color-...)` rather than inlining the value. So
overriding a token under a selector cascades to both the utilities and the component
CSS with no second name. Light values live in `@theme` (`:root`) with a re-asserting
`[data-theme="light"]` block. Dark values are authored too - the full dark token set
exists from one source (A6) - but as an override under `[data-theme="dark"]` ONLY.

Activation is staged. This change does NOT emit the
`@media (prefers-color-scheme: dark)` activation block, so a dark system preference
does NOT auto-apply dark to un-migrated pages this phase. Dark is reachable only by an
explicit `fb-theme=dark` override (which sets `data-theme="dark"`), which is enough to
verify dark correctness for the tokenized component and shell classes without shipping
a half-themed dark state to pages still on hard color utilities. System-default dark
activation (the media-query block) lands in P5 with page migration.

`[data-theme="light"]` and `[data-theme="dark"]` are single-selector rules of equal
specificity, so an explicit `[data-theme="light"]` beats `[data-theme="dark"]` by
SOURCE ORDER, not by specificity; author the light re-assertion after so an explicit
light choice wins, or raise the override specificity deliberately with
`html[data-theme="..."]`. A behaviour test (D7) proves `data-theme="light"` renders
the light tokens and that absent any explicit override the app renders light (no
system-preference dark this phase). One switch flips the whole UI. Token names mirror
the story
(`--color-brand`, `--color-brand-ink`, `--color-brand-soft`, `--color-field`,
`--color-panel`, `--color-ink`, `--color-ok`, `--color-ok-soft`, ...), plus the
semantic on-soft text tokens from D-contrast, the non-semantic component color
`--color-on-brand` (button label on the solid brand fill), the elevation tokens
`--shadow` (and any additional shadow tokens the marks need, for example a stronger
`--shadow-lg`), `--r-sm`/`--r`, and
`--font-sans`/`--font-mono`. Every color a `@layer components` rule body needs is one
of these tokens; the layer holds no literal color (D7).

Why not two-tier: Plan B's `--fb-*` layer solves a problem Tailwind v4 does not have
here - utilities already read the theme var at runtime, so a single name is both the
runtime value and the utility source. A second name per token doubles the surface
(every token declared and kept in sync twice), invites drift, and buys nothing this
phase (code-as-liability). The one case that would need indirection is a token
consumed inside another token's definition (for example a `color-mix()` that must
resolve the value at build time, which needs `@theme inline`); the design avoids that
by keeping each soft ground an authored value rather than a computed one. If a future
phase needs a build-time-substituted token, apply `@theme inline` to that single
token then, not a blanket second tier now.

Other alternatives: a Tailwind `dark:` variant per utility (rejected - duplicates
every color in markup, fights A6's "one token set", and does not cover component
CSS); two separate stylesheets (rejected - doubles the source of truth and the
build).

### D-contrast Adjusted accessible tokens so components clear AA in both themes

The raw palette in `stories/Colors.stories.js` does not clear WCAG AA (4.5:1 for
normal text) for every text pairing. Measured ratios (sRGB, soft grounds composited
over their panel):

| Pairing | Theme | Ratio | AA text |
| --- | --- | --- | --- |
| `faint` text on `panel`/`field`/`panel-dim` | light | 3.16 / 2.81 / 3.04 | fail |
| `faint` text on `panel`/`field`/`panel-dim` | dark | 4.34 / 4.78 / 4.60 | fail on `panel` |
| `warn` base as word on `warn-soft` | light | 4.19 | fail |
| `neutral` base as word on `neutral-soft` | light | 4.24 | fail |
| `brand` as word on `brand-soft` | dark | 4.19 | fail |
| `fail` base as word on `fail-soft` | dark | 4.13 | fail |

Everything else clears AA: `ink`/`muted` on all grounds in both themes (muted worst
case 4.96 light, 6.84 dark); `ok`/`info` word-on-soft in both themes (worst 4.57);
`brand-ink` on `brand-soft` (7.97 light, 5.75 dark); `on-brand` (white) on the `brand`
button (6.90); every semantic base as text on `field`/`panel` in dark (>= 5.14). Dark
`faint` does NOT clear AA on every ground: its worst case is 4.34 on dark `panel`
(below the 4.5 text threshold), so it is corrected too (see move 2).

Decision - do not ship the raw values for the failing pairs. Two moves:

1. Status/semantic word text uses an on-soft `-ink` token per semantic
   (`--color-ok-ink`, `--color-warn-ink`, `--color-fail-ink`, `--color-info-ink`,
   `--color-neutral-ink`), mirroring the existing `brand-ink`. The base color
   (`--color-ok`, ...) stays the seal fill and dot - an 8px shape is non-text
   graphical and only needs 3:1, which every base clears. Each `-ink` is tuned to
   clear AA (>= 4.5) on both its soft ground and `panel` in both themes; where a base
   already clears AA the `-ink` may equal it. Candidate values that pass: light
   `warn-ink` #875e08 (4.99 on soft, 5.78 on panel), light `neutral-ink` #626a66
   (4.77 / 5.57); dark `fail-ink` #ec8a82 (5.26 / 6.55); the dark brand word uses the
   existing `brand-ink` #aca6f4 (5.75).
2. `faint` is darkened in both themes to clear AA as text on its grounds (it carries
   eyebrows, mono provenance labels, and table headers per `stories/_marks.js`, which
   are meaningful text). Light candidate #66706b clears 4.5 on `panel` and
   `panel-dim`; if a `faint` eyebrow sits directly on `field`, darken until it clears
   there too. Dark `faint` #7d8781 fails on `panel` (4.34); candidate #828c86 clears
   4.5 on all three dark grounds (4.64 `panel`, 5.11 `field`, 4.91 `panel-dim`). Both
   corrections are mirrored into `Colors.stories.js` NEUTRALS.

These are palette corrections, so update `Colors.stories.js` in the same change to
match, keeping the reference and the app in sync. The in-code components-layer comment
states the REASON only - a deliberate AA-contrast correction from the ratified palette
values (a deliberate deviation, which `comment-etiquette.md` permits) - not an
old-hex -> new-hex history (which the same rule bars as a changelog). The precise
old/new hex values live in this design doc and the story delta, not in a source
comment. This implements
`web-ux-conventions` A1 and the AA-in-both-themes half of A6 for the token layer. The
contrast guard (D7) computes these ratios and fails the build if any text pair drops
below 4.5 or any seal/fill below 3:1, in either theme.

### D2 Legacy aliases

Keep `--color-brand`, `--color-brand-hover`, `--color-danger` as aliases:
`--color-brand` maps to the new brand, `--color-brand-hover` to `brand-ink`,
`--color-danger` to `fail`. Existing utilities (`bg-brand`, `text-danger`, etc.) and
the redefined `btn-*`/`badge-*` keep working. This is the smallest change that keeps
the build green while P2 proceeds; the aliases are removed in P5.

### D3 Theming mechanism and pre-paint script

Storage: the person's override in `localStorage` under one key, `fb-theme`, with a
tri-state value `light | dark | system`. `light` and `dark` set
`document.documentElement.dataset.theme` to that value; `system` (and an absent or
unrecognised value) sets nothing, and because this change does not emit the
`@media (prefers-color-scheme: dark)` activation block, the app renders light. The
system-default dark activation is staged to P5 with page migration; until then `system`
means light. The tri-state is chosen over Plan A's two-state (absent = system) because
the P3 toggle is a three-way control and an explicit `system` distinguishes "chose to
follow the system" from "never set", which a two-state key cannot; storing it now keeps
the key forward-compatible with the P5 system activation. The `fb-theme` key name is
kept (Plan A) over Plan B's `freeboard.theme` for consistency with the `fb-` prefix
used across the classes and to keep the pre-paint snippet short. A tiny script in
`_Head.cshtml`, before the stylesheet link, reads the key and applies the attribute.
Because it runs synchronously in `<head>` before body render, there is no flash.

Setter: none in this change. Plan B proposed a small theme-setter API in `app.js`
now; deferred. Nothing in P1/P2 renders a control that writes `fb-theme` - the toggle
UI is P3 - so a setter would be an unused API (code-as-liability). P1 ships only the
read/apply half; P3 adds the toggle and the setter together.

CSP: no CSP exists today, so an inline snippet works now. To stay strict-CSP-safe
later, the snippet is static (no interpolation), so it has a stable SHA-256 that a
future `script-src` can allowlist by hash - no nonce infrastructure needed. The
alternative, a self-hosted synchronous external `theme-init.js` in `<head>`, is also
CSP-safe (`script-src 'self'`) but adds a render-blocking same-origin request; the
hashed inline snippet is smaller and faster. Chosen: hashed inline snippet, with the
external-file option kept as the fallback if the future CSP forbids inline entirely.
The theme toggle that writes the key is P3; P1 ships only the read/apply half.

### D4 Font vendoring

Vendor `.woff2` files under `src/Freeboard/wwwroot/fonts/` (served directly, not
through the Tailwind pipeline), with `@font-face` declarations in `app.css` using
`font-display:swap`. Weights: Schibsted Grotesk 400/500/600/700, IBM Plex Mono
400/500/600 - seven faces. Preload the two most-used faces (sans 400 and 500) in
`_Head.cshtml` with the full attribute set:
`<link rel="preload" as="font" type="font/woff2" href="..." crossorigin>`. Fonts
fetch in CORS mode, so a preload missing `crossorigin` (with `as="font"` and
`type="font/woff2"`) does not match the `@font-face` request and the browser fetches
the face twice; the four attributes together make the preload hit. Wire `--font-sans`
and `--font-mono`.

`unicode-range`: state per face whether the vendored `.woff2` is a Latin subset. A
Latin-subset face declares a matching `unicode-range` on each `@font-face` so the
browser only downloads it when Latin glyphs are used; a full multi-script face omits
`unicode-range` and accepts a larger size. Schibsted Grotesk is vendored as the
upstream Latin subset (with `unicode-range`); IBM Plex Mono is vendored as the full
unmodified upstream face (no `unicode-range`) for the RFN reason below.

Both families are the SIL Open Font License 1.1: Schibsted Grotesk from the
`google/fonts` repository (its `OFL.txt`) and IBM Plex Mono from the `IBM/plex`
distribution (the `@ibm/plex-mono` npm package, whose `LICENSE.txt` is OFL 1.1).
Vendor the EXACT upstream license file for BOTH families next to their font files,
unmodified.

Provenance / Reserved Font Name (RFN): OFL 1.1 condition 3 forbids distributing a
MODIFIED font under a Reserved Font Name; an unmodified original may keep it. IBM
Plex Mono's license declares `Reserved Font Name "Plex"`, so it is vendored as the
UNMODIFIED, full upstream face and served under the family name "IBM Plex Mono" -
verbatim redistribution of the original is permitted, so there is no RFN question and
no residual human step. Schibsted Grotesk is vendored as a Fontsource Latin subset
(a derivative), but its OFL declares NO Reserved Font Name, so its subset naming is
unaffected. Full detail is in `src/Freeboard/wwwroot/fonts/PROVENANCE.md`.

### D5 Class reconciliation - one class per job

Rule: a visual job has exactly one class. Where an established bare name is already
used by pages or asserted by tests, redefine that name against the new tokens; do not
create an `fb-*` twin. Introduce `fb-*` only for genuinely new marks. The full map
below lives in this design doc ONLY; the in-code comment states just the rule, not the
table (see task 4.3):

| Job | Class (canonical) | Source |
| --- | --- | --- |
| Primary button | `btn-primary` (redefined) | was `fb-btn--brand` |
| Secondary button | `btn-secondary` (redefined) | was `fb-btn` default |
| Danger button | `btn-danger` (redefined) | new tokens |
| Quiet button | `btn-quiet` (new, in `btn-*` family) | was `fb-btn--quiet` |
| Small/dense button | `btn-sm`, table button folds into `btn-sm` | was `fb-btn--sm`, `fb-tbtn` |
| Badge (pill) | `badge` + `badge-brand/neutral/success/warn/danger` (redefined) | tests key on these |
| Tag (rect) | `fb-tag` (+ `--brand/ok/warn/fail`) (new) | distinct mark from badge |
| Static card | `card` (redefined) | tests/pages use it |
| Structured panel | `fb-panel` (+ `__head/__body/__meta`) (new) | new mark |
| Status seal + word | `fb-status` + `fb-seal` (new) | S2/S3 mark |
| Provenance stamp | `fb-stamp` (+ `manual/gen`) (new) | P1/P2 mark |
| Owner/avatar | `fb-owner` + `fb-av` (new) | new mark |
| Due date | `fb-due` (+ `soon/over`) (new) | T6 mark |
| Filter chip | `fb-chip` (new) | L2 mark |
| Tabs | `fb-tab*` (new) | `acct-tab*` stays until P5 migrates account pages |
| Table + marks | `fb-tbl`, `fb-grp`, `fb-tdname`, ... (new) | new marks |
| Stats/bars/feed/framework | `fb-stat*`, `fb-bar`, `fb-feed`, `fb-fwrow` (new) | new marks |
| Report marks | `fb-rdoc`, `fb-rmetrics`, `fb-delta`, `fb-spark`, ... (new) | new marks |
| Toolbar/search/guidance | `fb-toolbar`, `fb-search`, `fb-spacer`, `fb-guidance` (new) | new marks |

Preserved classes are evidence-based, not speculative. Grepped `src/Freeboard`
`.cshtml` and `tests/`:

- Test-asserted (breaking these breaks a test): `btn-primary`
  (`CustomRolesPageTests.cs:101`), `badge-danger`
  (`StatementOfApplicabilityPageTests.cs:135,256`), `badge-success` (same file :319),
  `soa-nodes` (`StatementOfApplicabilityPageTests.cs` + `table.soa-nodes` in the E2E
  suites), `data-node-id` (SoA page and E2E), `temp-password`
  (`AdminUserPagesTests.cs:159,578`, `.temp-password` in `AdminUserPagesE2ETests`).
  `soa-nodes`, `data-node-id`, and `temp-password` are page markers, not style
  classes, but they must survive; this change does not touch the pages that emit them.
- Markup-used, not test-asserted (breaking these regresses a rendered page):
  `btn-secondary` (7 uses), `btn-danger` (7), `btn-sm` (6), `badge` base plus
  `badge-brand/neutral/warn` (all present in `.cshtml`), `card` (24), `app-card` (3),
  `form-input` (30), `form-label` (25), `notice` + `notice-error` (29), `link` (20),
  `nav-link` + `nav-link-active`, `menu-item`, `acct-tab` + `acct-tab-active`. The nav
  and account classes are live in the shell partials today
  (`Pages/Shared/_Layout.cshtml`, `_AccountNav.cshtml`,
  `Components/OrgSelector/Default.cshtml` and `_Node.cshtml`), so they are preserved,
  not dead.

Every live component class is retokenized so it themes in both modes. The current
definitions in `assets/css/app.css` reference Tailwind BUILT-IN palette colors
(`neutral-*`, `white`, `green-*`, `amber-*`, `ring-neutral-*`), which do not switch
under `[data-theme="dark"]`. Redefining `--color-brand`/`-hover`/`-danger` does not
touch a `border-neutral-300` or a `bg-white`, so as written these classes would render
light-only under an explicit dark override - the tokenized component/shell classes
would be half-themed in dark, and A6 would fail for those classes. The fix is to change
the COLOR SOURCE of
every one of these class definitions to the new semantic tokens (`field`, `panel`,
`panel-dim`, `ink`, `muted`, `faint`, `line`, `line-strong`, and the semantic
bases / `-soft` / `-ink`), while keeping the preserved class NAMES (per D5's
one-class-per-job rule). This changes their palette source only; it does NOT restyle
any page markup (that stays P5). The live classes to retokenize are the full current
components layer: `form-label`, `form-input`, `btn-primary`, `btn-secondary`,
`btn-danger`, `btn-sm`, `link`, `notice`, `notice-error`, `badge` +
`badge-brand/neutral/success/warn/danger`, `nav-link`, `nav-link-active`,
`nav-section-title`, `menu-item`, `app-card`, `card`, `acct-tab`, `acct-tab-active`.
Mapping is by role: `neutral-900` -> `ink`, `neutral-700`/`neutral-600` -> `ink` or
`muted` by emphasis, `white`/`bg-white` -> `panel`, `bg-neutral-50`/`bg-neutral-100`
-> `panel-dim`, `border-neutral-*` -> `line` or `line-strong`,
`ring-neutral-300` -> `ring-line-strong` (the focus-ring tone on `btn-secondary`),
`green-*` -> `ok`/`ok-ink`, `amber-*` -> `warn`/`warn-ink`, `danger` -> `fail` (via the
D2 alias). The one non-semantic color a component needs - the button label on the solid
`brand` fill - is a token, `--color-on-brand` (replacing the literal white), so it too
themes and clears the guard. No live component class keeps a Tailwind built-in color or
a literal for a themed surface: there is no rule-body color exception. Shell-specific
NEW `fb-` marks (rail,
topbar, navitem, drawer) are defined by their owning phase (P3/P4), not here, to avoid
dead CSS; this change ports the content and mark classes P2 lists.

### D6 Tag-helper mechanism and which marks get one

Author small tag helpers under `src/Freeboard/TagHelpers/` (namespace `Freeboard`,
already covered by `@addTagHelper *, Freeboard`). Two typed enums keep marks
invariant-safe at author time:

- `StatusKind` for `<fb-status>`: encodes the COMPLETE S1 status vocabulary from
  `web-ux-conventions`. S1 lists nine statuses, two of them as synonym pairs
  (Ready/Passing, Drifting/Degraded); `StatusKind` has one canonical member per status,
  so every S1 status is representable and no label outside S1 is:

  | `StatusKind` | Word | Tone (seal + color) | Alias |
  | --- | --- | --- | --- |
  | `Passing` | "Passing" | Ok (green seal) | Ready |
  | `Failing` | "Failing" | Fail (red seal) | - |
  | `DueSoon` | "Due soon" | Warn (amber seal) | - |
  | `Overdue` | "Overdue" | Fail (red seal) | - |
  | `Drifting` | "Drifting" | Warn (amber seal) | Degraded |
  | `Snoozed` | "Snoozed" | Neutral (outline seal) | - |
  | `Waiting` | "Waiting" | Neutral (outline seal) | - |
  | `Draft` | "Draft" | Neutral (outline seal) | - |
  | `OutOfScope` | "Out of scope" | Neutral (outline seal) | - |

  `<fb-status>` maps each kind to its canonical WORD, its tone (hence color), and its
  ARIA (the seal is decorative `aria-hidden`; the word is the accessible text, so the
  status reads without color per S2) in ONE place, so the word and the color cannot
  disagree and S3 (red only for failing/overdue) holds by construction. A bare tone
  attribute is not enough here: it would still permit
  `<fb-status tone="Fail">Due soon</fb-status>` or a label outside the vocabulary.
  Keying `<fb-status>` on `StatusKind` makes both unrepresentable. Ready and Degraded
  are aliases of `Passing` and `Drifting` (same tone and seal): S1 lists them as
  synonyms, so they are one status each, not a distinct kind; a distinct word for them,
  if ever needed, is a later addition, not a new status.
- `MarkTone { Neutral, Brand, Ok, Warn, Fail }` for the generic tint helpers
  `<fb-badge>` and `<fb-tag>`. Its members map one-to-one to the tint classes those
  helpers emit, so no tone lacks a class and no emitted tint class lacks a tone:

  | `MarkTone` | `<fb-badge>` class | `<fb-tag>` class |
  | --- | --- | --- |
  | `Neutral` | `badge-neutral` | `fb-tag` (base) |
  | `Brand` | `badge-brand` | `fb-tag--brand` |
  | `Ok` | `badge-success` | `fb-tag--ok` |
  | `Warn` | `badge-warn` | `fb-tag--warn` |
  | `Fail` | `badge-danger` | `fb-tag--fail` |

  Red is reachable only through `Fail`, so S3 holds. `Info` is dropped (neither badge
  nor tag has an info tint - the blue info seal belongs to the status marks, not these
  tint helpers) and `Brand` is added (both badge and tag carry a brand tint).
  `<fb-chip>` is a filter chip: it takes a label, a count, and a boolean selected state
  (the `.fb-chip.on` class), not a tone - it has no tint variants - so it does not take
  `MarkTone`.

Each helper emits the canonical class(es) and its required ARIA/shape, nothing inline.

Build now (high-frequency, invariant/ARIA-bearing): `<fb-status>` (seal + word),
`<fb-stamp>` (provenance kind), `<fb-badge>`, `<fb-tag>`, `<fb-due>` (relative/
absolute + overdue-in-words, T6), `<fb-chip>` (label + count + on-state, L2),
`<fb-owner>` (initials avatar + name). Seven helpers plus the enum. Everything else
(`fb-seal` alone, sparklines, deltas, feed, panels, stats, report marks, tabs,
toolbar, guidance) stays a CSS class until a second caller appears (code-as-liability:
no single-caller helper). Helpers follow the `OrgSelector` conventions for structure
and are unit-tested by driving `Process` and asserting the emitted classes/ARIA.

### D7 Verification strategy

- Build stays green: `dotnet build` (runs `bun run build`) succeeds; aliases keep
  existing pages compiling.
- No-literal-color guard: a unit test scans the `@layer components` rule bodies of
  the SOURCE `assets/css/app.css` and fails on ANY literal color value - hex,
  `rgb()`/`rgba()`, `hsl()`/`hsla()`, or a CSS named color - not only brand/status
  hex. `stories/_marks.js` carries neutral hexes and `rgba(...)` (quiet-button hover,
  stamp borders, the seal shadow) that a brand/status-only scan would miss, so the
  broad rule is required. Literal color is permitted in EXACTLY TWO places: inside a
  custom-property DECLARATION (`--x: value`, wherever declared - the `@theme` and
  `[data-theme="light"]`/`[data-theme="dark"]` blocks all declare tokens) and inside
  `@font-face`. It is forbidden inside every `@layer components` rule body, with ZERO
  rule-body exceptions: every color a rule body needs - including shadow color -
  references a token (`var(--color-...)`, `--shadow`, `--color-on-brand`). There is no
  "documented shadow" escape hatch: a shadow that
  needs a specific tone declares that tone as a token in the allowed zone and
  references it. The test reads the source file (not the gitignored, minified
  `wwwroot/css/app.css`) and parses it once. Enforces the S2/S3/A6 "tokens only"
  invariant.
- No built-in color-UTILITY guard: the no-literal-color guard above catches literal
  hex/rgb/hsl/named values but NOT Tailwind built-in palette utilities
  (`bg-white`, `border-neutral-300`, `text-green-600`), which are class names, not
  literals - so completeness of the retokenize would otherwise rest on manual review
  with no test. A second assertion scans the `@layer components` rule bodies for
  built-in palette utility tokens (`neutral-\d`, `\bwhite\b` used as a color utility,
  `green-`, `amber-`, `red-`, `blue-` used as colors) and fails on any hit: only
  tokenized color utilities (`bg-panel`, `border-line`, `text-ink`, ...) or
  `var(--color-...)` are allowed. This is the machine check behind the D5 retokenize
  completeness.
- AA contrast guard: a unit test computes the WCAG contrast ratio for the
  load-bearing pairs in both themes, from the token values parsed from the SAME single
  source of truth - the source `assets/css/app.css` `@theme` (light) and
  `[data-theme="dark"]` (dark) declaration blocks, read and parsed once (soft grounds
  composited over their panel). Dark correctness is checked from the dark token set
  even though it is not system-activated this phase - the explicit-override path
  reaches those values. Text pairs (`ink`/`muted`/`faint` on `field`/`panel`/
  `panel-dim` in BOTH themes - so dark `faint` on `panel` is asserted >= 4.5 - each
  semantic `-ink` word on its soft ground and on `panel`, `brand-ink` on `brand-soft`,
  `on-brand` on `brand`) assert >= 4.5, and seal/fill-vs-ground pairs (each semantic
  base on its soft ground) assert >= 3.0. This is the test that catches the D-contrast
  regressions; it encodes A1 and the AA-in-both-themes half of A6 as a test, not a
  comment.
- Font-origin guard: a test boots the app over `WebApplicationFactory`, fetches
  `/css/app.css` (the served stylesheet) and the rendered `<head>`, and asserts every
  font request targets the app origin - no `fonts.googleapis.com` or
  `fonts.gstatic.com`. It reads the served response, not the gitignored build output
  on disk.
- Cascade / staged-activation guard: a test proves an explicit `data-theme="light"`
  renders the light tokens and an explicit `data-theme="dark"` renders the dark tokens
  (so the override reaches both token sets), and that with NO explicit override the app
  renders light - the source emits no `@media (prefers-color-scheme: dark)` block this
  phase, so a dark system preference does not auto-apply dark (D1, D3).
- Pre-paint CSP-shape guard: a test renders the head and asserts the theme snippet
  precedes the stylesheet link and that the snippet text is BYTE-IDENTICAL across two
  requests made with different `fb-theme` / user state (proving no request-time
  interpolation), so it is hash-allowlistable under a future strict CSP.
- Preserved-marker regression: existing `tests/Freeboard.Web.Tests` and E2E suites
  stay green with the D5 markers intact (`btn-primary`, `badge`, `badge-danger`,
  `badge-success`, `temp-password`, `soa-nodes`, `data-node-id`).
- Tag helpers: unit tests per helper - correct classes, ARIA, tone mapping (illegal
  tones unrepresentable by construction). `<fb-status>` maps each `StatusKind` to the
  canonical word, tone, and ARIA in one place; the test asserts that kind -> word/tone/
  ARIA mapping and that a word and color cannot disagree (an illegal word/tone pairing
  is unrepresentable). Status emits shape + word (S2); due emits "overdue" in words
  (T6); provenance names its source and age (web-ux P1/P2 provenance).

### Authoring constraint: disambiguate rule IDs from phase labels

"P1" and "P2" are overloaded in this work: they name migration PHASES (this change is
phases P1+P2) and they are `web-ux-conventions` provenance REQUIREMENTS (P1 source
stamp, P2 MANUAL stamp). This design doc uses "P1"/"P2" for phases by local
convention, which is safe here. But anything that can reach code, comments, commit
messages, or test names SHALL NOT use a bare "P1"/"P2" for a phase: write provenance
rule citations unambiguously (for example "web-ux P1/P2 provenance" or "P1 source
stamp") and never label a phase "P1/P2" in source. Phase labels are process references
and are excluded from source by `no-process-references.md` regardless.

## Risks / Trade-offs

- [Flash of wrong theme] -> Pre-paint synchronous head script sets `data-theme`
  before the stylesheet renders (D3); verified by loading a dark-override page.
- [Font licensing uncertainty] -> Both families treated as OFL 1.1 (D4); vendor each
  family's exact upstream license file and confirm at the merge-time checklist. If a
  family turns out non-redistributable, swap to a licensed replacement that keeps the
  human/machine split (the token indirection makes this a one-line `--font-sans` /
  `--font-mono` change).
- [AA contrast fails for a text pair] -> Known: six raw-palette pairs fail (light
  `faint`, dark `faint` on `panel`, light `warn`/`neutral` word-on-soft, dark
  `brand`/`fail` word-on-soft). Resolved in D-contrast (semantic `-ink` word tokens
  plus a darkened `faint` in both themes), synced into `Colors.stories.js`. The
  contrast guard (D7) holds the line and catches any future regression in either
  theme.
- [Half-themed dark reaching users before pages migrate] -> Dark is authored (one
  token set, A6) but activated ONLY by an explicit `fb-theme=dark` override; this
  change emits no `@media (prefers-color-scheme: dark)` block, so a dark system
  preference does not auto-flip un-migrated pages (still on hard color utilities) into
  a partial dark state. System-default dark activation lands with page migration (P5).
  Dark correctness is verified now via the explicit override and the contrast guard
  (D7), so nothing dark-side is untested.
- [Two class systems drift] -> One-class-per-job rule and the documented map (D5);
  the no-literal-hex guard, the built-in-utility guard, and preserved-marker tests keep
  the boundary honest.
- [Storybook drift] -> Out of scope to fully solve here (P7 owns the drift guard),
  but the components layer is ported faithfully from `_marks.js` and the map records
  the mapping; flagged in Open Questions.
- [Scope creep into the shell] -> Shell-only marks are explicitly deferred to P3
  (D5) so this change adds no dead CSS.
- [CSP for the pre-paint script] -> No CSP today; the snippet is static and
  hash-allowlistable later (D3), with an external-file fallback.

## Migration Plan

Land in dependency order behind existing pages, one Conventional Commit per group
(see `tasks.md`): tokens + aliases, then theming/pre-paint, then fonts, then the
component classes + reconciliation map, then the tag-helper mechanism + marks + tests,
then the guard/contrast tests. No page is migrated. Rollback is a revert of the CSS
and tag-helper commits; because legacy aliases persist and no page markup changes,
reverting cannot strand a migrated page.

## Open Questions

1. Font licensing sign-off: RESOLVED. Both families are OFL 1.1 per upstream -
   Schibsted Grotesk from `google/fonts` (its `OFL.txt`) and IBM Plex Mono from
   `IBM/plex` (its `LICENSE.txt`). The change vendors each family's EXACT upstream
   license file next to its font files, and the OFL 1.1 redistribution terms are
   confirmed met (licenses included, Reserved Font Name handled - see below). No
   residual pre-merge sign-off remains; this is not an authoring or merge blocker.
   If a family later turns out not clearable, pick a licensed replacement that
   preserves the human/machine split (a one-line `--font-sans` / `--font-mono` change).
   Reserved Font Name: RESOLVED in code, no residual human step. IBM Plex Mono
   carries the Reserved Font Name "Plex", so it is vendored as the UNMODIFIED, full
   upstream face and served under the "IBM Plex Mono" family name - OFL condition 3
   bars the RFN only on a modified version, and verbatim redistribution of the
   original is permitted. Schibsted Grotesk is a Fontsource Latin subset (derivative)
   but declares no RFN, so it needs no action. Full detail is in
   `src/Freeboard/wwwroot/fonts/PROVENANCE.md`.
2. Pre-paint CSP approach: accept the hashed inline snippet now (with a static hash
   to allowlist when CSP lands), or prefer a self-hosted external `theme-init.js`
   from the start?
3. Storybook drift control (P7 decision, noted here): point the Storybook Tailwind
   build at the real `app.css`, or add a check that the `fb-*` class list in
   `_marks.js` matches the components layer? Not resolved in this change.
4. Redefining `btn-primary`/`badge`/`card` now shifts their light palette on pages not
   yet migrated (intended: the true bar this change is light-mode palette migration
   visible on all pages, dark authored and reachable via explicit override). Confirm
   the interim mixed look (new marks on old page layouts) is acceptable until P3-P5,
   versus holding the redefinition.
