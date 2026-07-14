# web-design-system Specification

## Purpose

The design-system contract for the web UI: the light and dark token set defined
from one source, the system-default-with-personal-override theming applied before
first paint, self-hosted typography, the tokenized component classes that carry no
literal color, and the invariant-safe mark tag helpers.
## Requirements
### Requirement: Full token set from one source, both themes

The web UI (`src/Freeboard`) SHALL define its design tokens in
`assets/css/app.css` as one set with a light and a dark value for every token,
sourced from the ratified palette in `stories/Colors.stories.js`. The set SHALL
include the brand triad (`brand`, `brand-ink`, `brand-soft`), the eight neutrals
(`field`, `panel`, `panel-dim`, `ink`, `muted`, `faint`, `line`, `line-strong`),
the five semantic colors each with a base, a soft ground, and an on-soft `-ink` text
token (`ok`, `warn`, `fail`, `info`, `neutral`), the radii (`--r-sm`, `--r`), and the
elevation shadows. There SHALL NOT be a token that exists in only one theme. This
implements `web-ux-conventions` A6 for the token layer.

#### Scenario: Every token resolves in both themes

- **WHEN** the compiled `app.css` is inspected in the light and the dark state
- **THEN** every brand, neutral, semantic-base, semantic-soft, radius, and
  elevation token resolves to a value in each theme, and the values match the
  ratified palette

#### Scenario: Semantic status meaning is stable across themes

- **WHEN** the same semantic token (for example `fail`) is read in light and in dark
- **THEN** it denotes the same status in both, so status semantics do not change
  with theme

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

### Requirement: Both authored themes reachable by personal override; system-default activation staged

The design system SHALL author both a light and a dark value for every token from
one set (the A6 "one token set" invariant), and SHALL provide a per-person override
applied through a `data-theme` attribute on the `<html>` element, stored as a personal
setting (not a workspace one) under `localStorage` `fb-theme` (`light|dark|system`).
The light values SHALL apply on `:root` and the dark values SHALL apply under
`[data-theme="dark"]`, so an explicit override reaches either authored theme.

System-default dark activation is STAGED: this change SHALL NOT emit an
`@media (prefers-color-scheme: dark)` activation block, so a dark operating-system
preference does not auto-apply dark while pages still carry non-tokenized color
utilities. With `fb-theme` set to `system` or absent, the app SHALL render light.
System-default activation via `prefers-color-scheme` lands when pages migrate AND the
dark palette passes the same accessibility audit as light (the audit currently covers
light only). This
partially implements `web-ux-conventions` A6: the single light+dark token set and the
override mechanism exist and are verified, while system-default activation is deferred.

#### Scenario: No explicit override renders light

- **WHEN** a person has `fb-theme` set to `system` or has set no override, under a dark
  operating-system preference
- **THEN** the app renders light, because no `prefers-color-scheme` dark activation
  block is emitted in this change

#### Scenario: Explicit dark override yields the dark token set

- **WHEN** a person's `fb-theme` is `dark`
- **THEN** `data-theme="dark"` is set on `<html>` and every token resolves to its
  authored dark value

#### Scenario: Explicit light override wins under a dark system preference

- **WHEN** a person's `fb-theme` is `light`, under a dark operating-system preference
- **THEN** `data-theme="light"` is set on `<html>` and the tokens resolve to the light
  theme

### Requirement: Stored theme is applied before first paint

The stored theme override SHALL be applied to the document before first paint, so
the page never renders in the wrong theme and then corrects. The applying script
SHALL be small, self-hosted, and compatible with a strict Content-Security-Policy
(no dependency on `unsafe-inline` beyond a statically hashable snippet, and no
third-party origin).

#### Scenario: No flash of the wrong theme

- **WHEN** a person whose override is dark loads any page
- **THEN** the first painted frame is already dark, with no visible flash of the
  light theme

### Requirement: Self-hosted typography with no third-party fetch

The web UI SHALL self-host the sans family (Schibsted Grotesk, weights
400/500/600/700) and the mono family (IBM Plex Mono, weights 400/500/600) under the
web project, declared with `@font-face` using `font-display:swap`, and wired to the
`--font-sans` and `--font-mono` tokens. The running app SHALL NOT fetch fonts from
any third-party origin. Each vendored family SHALL carry its redistribution license
file alongside the font files.

#### Scenario: Fonts load from the app origin only

- **WHEN** a page loads and renders text
- **THEN** every font request targets the app's own origin, and none targets a
  third-party font host

#### Scenario: Both families are wired to tokens

- **WHEN** an element uses `--font-sans` or `--font-mono`
- **THEN** it renders in the self-hosted Schibsted Grotesk or IBM Plex Mono
  respectively

### Requirement: Legacy color aliases keep resolving during migration

The pre-existing token names SHALL keep resolving as aliases onto the new tokens
(`--color-brand`, `--color-brand-hover`, `--color-danger`) for the duration of this
change, so pages and utilities that reference them keep building
and rendering. Removal of these aliases is out of scope for this change.

#### Scenario: Existing pages still build

- **WHEN** the web project is built after the token set is replaced
- **THEN** the build succeeds and pages that reference `brand`, `brand-hover`, or
  `danger` still compile and render

### Requirement: Component classes are defined from tokens only

The `fb-*` component set SHALL be rebuilt in `app.css` `@layer components` from
`stories/_marks.js` (and the per-component stories, which are the authoritative
spec), with every literal color replaced by a token. Every EXISTING live component
class in the layer (buttons, badges, card, form controls, notices, links, and the
shell/account classes) SHALL likewise take its colors from the semantic tokens, not
from framework built-in palette colors, so it themes in both modes. The components
layer SHALL contain no literal color value - hex, `rgb()`/`rgba()`, `hsl()`/`hsla()`,
or named color - inside any rule body; literal color is permitted only inside a
custom-property declaration or `@font-face`.

#### Scenario: No literal color in the components layer rule bodies

- **WHEN** the `@layer components` rule bodies of `app.css` are scanned
- **THEN** they contain no literal color value; each color is a token reference, and
  literal color appears only in a custom-property declaration or `@font-face`

#### Scenario: No framework built-in color utility in the components layer

- **WHEN** the `@layer components` rule bodies of `app.css` are scanned for framework
  built-in palette utilities (for example `neutral-*`, `white`, `green-*`, `amber-*`
  used as a color)
- **THEN** none is present; every color utility is a tokenized one (`bg-panel`,
  `border-line`, `text-ink`, ...) or a `var(--color-...)` reference, so no class is
  left light-only

#### Scenario: A component renders correctly in both themes

- **WHEN** any component class - a new `fb-*` mark or a retokenized established class -
  is rendered in light and in dark
- **THEN** it takes its colors from the tokens and meets WCAG AA contrast in both

### Requirement: One class per job with a documented reconciliation map

There SHALL be exactly one class system: a single class per visual job, one naming
convention, with no two classes doing the same job. Established bare names that
pages and tests already key on (`btn-primary`, `btn-secondary`, `btn-danger`,
`btn-sm`, `badge`, `badge-*`, `card`) SHALL be redefined against the new tokens
rather than duplicated by an `fb-*` twin; `fb-*` names SHALL be introduced only for
genuinely new marks. The full reconciliation class map SHALL be recorded in the
change's design.md. The components layer SHALL carry a short comment at its top stating
the one-class-per-job rule and why established names are redefined rather than twinned,
citing the permanent rule IDs; that comment SHALL NOT restate the full map.

#### Scenario: Established tested markers are preserved

- **WHEN** the components layer is applied to existing pages
- **THEN** the classes `btn-primary`, `badge`, `badge-danger`, and `badge-success`
  still exist and style their elements, so tests keyed on them stay valid

#### Scenario: No duplicate class for one job

- **WHEN** the class map is reviewed
- **THEN** each visual job resolves to one class, with no `fb-*` twin of a redefined
  bare name doing the same job

#### Scenario: Reconciliation map in design, rule in the source comment

- **WHEN** the components layer and the change's design.md are inspected
- **THEN** the full reconciliation class map is present in design.md, and the
  components layer's top comment states the one-class-per-job rule and cites the
  permanent rule IDs without restating the full map

### Requirement: Invariant-carrying marks are emitted by tag helpers

Marks that carry a status invariant or ARIA SHALL be authored as ASP.NET tag
helpers rather than re-inlined per page - status seal (S2/S3), provenance stamp
(P1/P2 provenance), badge, tag, due date (T6), chip (L2), and owner. The status mark
SHALL bind its word, its color/tone, and its ARIA to a single typed status kind that
covers the complete S1 status vocabulary - one canonical kind for every S1 status, with
the synonym pairs (Ready/Passing, Drifting/Degraded) each mapping to one kind - so every
status is drawn from S1, no label outside it is representable, a status word and its
color cannot be authored to disagree, and red is reachable only for failing or overdue
(S3). The generic tint marks (badge, tag) SHALL take a typed tone enum whose members
correspond one-to-one to the tint classes those marks emit, so no tone lacks a class and
no emitted tint class lacks a tone, and a tone that violates S3 is unrepresentable; the
filter chip carries a selected state and count rather than a tone. Each tag helper SHALL
emit the corresponding `fb-*` or redefined class, so `app.css` remains the single style
source. A mark with a single caller MAY remain a plain CSS class until a second caller
appears.

#### Scenario: Status word and color cannot be authored to disagree

- **WHEN** an author places a status mark
- **THEN** its word and its color come from one typed status kind, so an informational
  or due-soon status cannot be rendered red and its word cannot contradict its color

#### Scenario: Every status in the vocabulary has a typed kind

- **WHEN** the status kinds are enumerated against the S1 vocabulary
- **THEN** every S1 status resolves to exactly one kind (the synonym pairs Ready/Passing
  and Drifting/Degraded each to one canonical kind), so no status is missing and no label
  outside S1 is representable

#### Scenario: Tint tone maps one-to-one to its classes

- **WHEN** the badge and tag tone enum is compared with the tint classes those marks
  emit
- **THEN** each tone maps to exactly one emitted class and each emitted tint class has a
  tone, with no info tone (no info tint class exists) and a brand tone present

#### Scenario: Tag helper output is the shared class vocabulary

- **WHEN** a mark tag helper renders
- **THEN** its output carries the same `fb-*` or redefined class the CSS defines, and
  no mark inlines its own colors

#### Scenario: Status carries shape and word, not color alone

- **WHEN** the status mark renders
- **THEN** it emits a shape and a word, so its meaning survives with color removed
  (S2)

### Requirement: The foundation ships behind existing pages

Landing the tokens, theming, fonts, component classes, and tag helpers SHALL NOT
migrate or re-home any existing page, and SHALL NOT regress existing page or
end-to-end tests. Preserved test markers (`temp-password`, `soa-nodes`,
`data-node-id`, `btn-primary`, `badge`, `badge-danger`, `badge-success`) SHALL
remain intact.

#### Scenario: Existing tests stay green

- **WHEN** the web test suite runs after this change
- **THEN** it passes, with the preserved markers still present in the rendered
  markup

