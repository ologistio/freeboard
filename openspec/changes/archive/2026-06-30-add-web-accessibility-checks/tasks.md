## 1. Add the test-only dependency

<!-- test(web): add axe-core-playwright to the WebE2E project -->

- [x] 1.1 Add the `Deque.AxeCore.Playwright` package reference to
  `tests/Freeboard.WebE2E/Freeboard.WebE2E.csproj`.

## 2. In-browser axe-core audit across all rendered views

<!-- test(web): add axe-core accessibility audit for all rendered views -->

- [x] 2.1 Add `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`, tagged
  `[Trait("Category", TestCategories.E2E)]` and gated with
  `[RequiresEnvVarTheory(EnvVar = E2EGate.EnvVar)]` + `Gate()`. Drive a
  data-driven case per rendered view: seed the session state that renders its
  real UI (anonymous, reset-token cookie, full session, force-reset-limited
  session, fresh-sudo session, or admin role), load the page in Chromium, run
  axe-core against every supported standard (WCAG 2.0/2.1/2.2 A/AA/AAA, Section
  508, EN 301 549, best-practice; experimental excluded), and assert zero
  violations.
- [x] 2.2 Assert the audit landed on the intended view (the URL path matches),
  so a mis-seeded state fails instead of auditing a redirect to `/login`.
- [x] 2.3 On failure, report each violation's rule, impact, help text, docs URL,
  and the failing node's selector and HTML.
- [x] 2.4 Run with `FREEBOARD_TEST_E2E=1` and confirm zero violations across all
  views; confirm the suite SKIPS cleanly with no browser env.

## 3. Fix the violations the maximal bar surfaced

<!-- fix(web): meet WCAG AAA and best-practice on the auth pages -->

- [x] 3.1 Darken `--accent` in `src/Freeboard/wwwroot/css/auth.css` so white
  button text clears the AAA 7:1 contrast threshold (1.4.6).
- [x] 3.2 Add a `visually-hidden` utility to `auth.css` and use it for the
  sessions table's actions-column header in
  `src/Freeboard/Pages/Account/Sessions.cshtml` (best-practice empty-table-header).

## 4. Verify

<!-- verification step -->

- [x] 4.1 `dotnet build tests/Freeboard.WebE2E` is clean.
- [x] 4.2 With `FREEBOARD_TEST_E2E=1`, the audit passes across every rendered
  view (zero violations under all supported standards).
- [x] 4.3 With no browser env, the audit skips cleanly like the rest of the E2E
  suite.
