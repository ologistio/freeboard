## Context

The web UI holds itself to a maximal axe-core bar that includes WCAG AAA
(`color-contrast-enhanced`, 7:1 for normal text). The neutral secondary-text tokens
were authored to WCAG AA (4.5:1). On the authenticated app shell, page content renders
on the off-white `--color-field` ground (`#f1f2ee` in light), which is lower-contrast
than pure `panel` white, so AA-only neutral text fails AAA there. Twelve authenticated
views fail exactly one rule on three element kinds; all use `muted`/`faint` or the
built-in `text-neutral-600` (`#525252`).

## Goals

- Make the rendered neutral text meet 7:1 while keeping the maximal audit bar.
- Preserve the `ink` > `muted` > `faint` hierarchy (a compressed but real ladder).
- Catch this regression class in the fast local test suite, not only E2E.

## Contrast math

WCAG relative luminance on resolved sRGB, ratio `(L_hi + 0.05) / (L_lo + 0.05)`.
Worst-case ground per theme is the lowest-contrast panel a token renders on: `field`
in light, `panel` in dark. Targets are >= 7.2 for margin.

Light (worst ground `field` `#f1f2ee`, brighter ground `panel` `#ffffff`):

| token | old | new | ratio on field | ratio on panel |
| --- | --- | --- | --- | --- |
| `muted` | `#4d534f` | `#464b48` | 7.91 | 8.90 |
| `faint` | `#66706b` | `#4a4f4c` | 7.43 | 8.35 |

Dark (worst ground `panel` `#1d2220`, `field` `#151917`):

| token | old | new | ratio on panel | ratio on field |
| --- | --- | --- | --- | --- |
| `muted` | `#a2aba5` | `#b5beb8` | 8.47 | 9.32 |
| `faint` | `#828c86` | `#a9b2ac` | 7.41 | 8.16 |

`ink` is already far above 7:1 in both themes (>= 13:1) and is unchanged. All four new
values clear 7.2 on every ground they render on; the light `faint` lands close to the
old `muted`, a deliberate compression of the neutral ladder.

## Decisions

- Re-tune only `muted` and `faint`; leave `ink` and the semantic/brand palette. The
  audit flagged only the neutral secondary-text tokens; re-tuning the semantic words
  to AAA is out of scope and would force a palette redesign the audit does not require.
- Migrate failing body copy from the built-in `text-neutral-600` utility to the
  `text-muted` token utility. This routes the text through the AAA-calibrated token
  (so it passes) and reduces built-in-utility usage. Only text-color classes change;
  no layout or structural classes are touched.
- Split the source contrast guard's text check: neutral text (`ink`/`muted`/`faint`)
  asserts 7:1; semantic `-ink` and `brand-ink` words keep 4.5:1; seals/fills keep 3:1.
  A blanket 7:1 raise would fail the semantic word pairs, which are correctly AA and
  render as small badges the AAA large-text allowance and audit scope tolerate.

## Risks / trade-offs

- The neutral ladder is compressed (light `muted` and `faint` differ by ~0.5 in ratio).
  Acceptable: both remain distinct and both clear the strict bar; a wider ladder is not
  possible while both must exceed 7:1 on the same ground.
- Public auth pages still use `text-neutral-600`; they render on the white panel ground
  where `#525252` already clears AAA (7.83:1), so they are left unchanged and stay
  passing. If a future page moves that copy onto `field`, migrate it to `text-muted`.
