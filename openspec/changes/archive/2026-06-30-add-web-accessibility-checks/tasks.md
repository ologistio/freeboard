## 1. Add the test-only dependencies

<!-- test(web): add AngleSharp and axe-core-playwright to the WebE2E project -->

- [x] 1.1 Add `AngleSharp` and `Deque.AxeCore.Playwright` package references to
  `tests/Freeboard.WebE2E/Freeboard.WebE2E.csproj`.

## 2. Always-on static accessibility baseline

<!-- test(web): add always-on static accessibility baseline for public auth pages -->

- [x] 2.1 Add `tests/Freeboard.WebE2E/AccessibilityBaselineTests.cs`, tagged
  `[Trait("Category", TestCategories.Nfr)]`. Serve `/login`, `/forgot-password`,
  and `/reset-password` through the in-memory `AuthWebFactory` (no browser),
  parse the HTML with AngleSharp, and assert: non-empty `html[lang]`, non-empty
  `<title>`, exactly one `<h1>`, a `meta[name=viewport]`, and a programmatic
  label (label-for, aria-label, aria-labelledby, or wrapping label) for every
  user-operable form control.
- [x] 2.2 Run `dotnet test tests/Freeboard.WebE2E --filter "Category=NFR"` with no
  browser env and confirm it passes (3 cases).

## 3. In-browser axe-core WCAG audit

<!-- test(web): add axe-core WCAG audit for public auth pages -->

- [x] 3.1 Add `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`, tagged
  `[Trait("Category", TestCategories.E2E)]` and gated with
  `[RequiresEnvVarTheory(EnvVar = E2EGate.EnvVar)]` + `Gate()`. Load each public
  auth page in Chromium and run axe-core scoped to the WCAG 2.0/2.1 A and AA rule
  tags, asserting zero violations.
- [x] 3.2 Run the audit with `FREEBOARD_TEST_E2E=1` and confirm zero violations
  (3 cases); confirm it SKIPS cleanly with no browser env.

## 4. Activate the NFR CI tier

<!-- ci: run the NFR test tier and keep it out of the E2E run -->

- [x] 4.1 Replace the NFR stub job in `.github/workflows/test.yml` with a real
  build-and-run of `dotnet test --filter "Category=NFR"`.
- [x] 4.2 Add `--filter "Category!=NFR"` to the E2E job's run so the NFR-tagged
  baseline does not double-run there.

## 5. Verify

<!-- verification step -->

- [x] 5.1 `dotnet build tests/Freeboard.WebE2E` is clean.
- [x] 5.2 With no browser env, `dotnet test tests/Freeboard.WebE2E` runs the NFR
  baseline (3 pass) and skips the browser tiers - the suite no longer reports
  all-skipped.
- [x] 5.3 With `FREEBOARD_TEST_E2E=1`, the axe audit passes (zero A/AA
  violations).
