# Vendored font provenance

## Source and modification status

- **IBM Plex Mono 400/500/600** (`IBMPlexMono-Regular.woff2`,
  `IBMPlexMono-Medium.woff2`, `IBMPlexMono-SemiBold.woff2`): the **unmodified,
  full** upstream web faces, taken verbatim from the authoritative IBM/Plex
  distribution (the `@ibm/plex-mono` npm package, `fonts/complete/woff2/`).
  They are not subset or otherwise altered, so the `@font-face` rules in
  `assets/css/app.css` carry **no** `unicode-range`.
- **Schibsted Grotesk 400/500/600/700** (`schibsted-grotesk-latin-*-normal.woff2`):
  the **Latin subsets** from the Fontsource CDN, so they are modified/derivative
  copies. The `@font-face` rules declare a matching `unicode-range`.

Both families are SIL Open Font License 1.1. The upstream license files are
vendored next to the faces, unmodified: `schibsted-grotesk-OFL.txt` and
`ibm-plex-mono-LICENSE.txt`.

## Reserved Font Name (OFL) status

OFL 1.1 condition 3 forbids a **modified/derivative** font from being
distributed under a Reserved Font Name (RFN); an unmodified original may keep it.

- **IBM Plex Mono:** its license declares `Reserved Font Name "Plex"`. The faces
  shipped here are the unmodified upstream originals, so serving them under the
  family name `"IBM Plex Mono"` (which contains "Plex") is permitted - OFL only
  bars the RFN on modified versions. No RFN action is required, and no human
  residual step remains: verbatim redistribution of the original is allowed.
- **Schibsted Grotesk:** its OFL header declares **no** Reserved Font Name
  (copyright line is "The Schibsted-Grotesk Project Authors", with no
  `with Reserved Font Name`). Shipping its subset under the family name
  `"Schibsted Grotesk"` therefore does not trigger an RFN restriction, even
  though the subset is a derivative.

## Redistribution

Both families are redistributed here in accordance with OFL 1.1: the fonts are
bundled with the application (never sold on their own), each family's license is
included verbatim, and neither family's RFN is used on a modified version.
