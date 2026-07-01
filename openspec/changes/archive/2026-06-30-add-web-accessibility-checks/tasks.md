## 1. Add the test-only dependency

<!-- test(web): add axe-core-playwright to the WebE2E project -->

- [x] 1.1 Add the `Deque.AxeCore.Playwright` package reference to
  `tests/Freeboard.WebE2E/Freeboard.WebE2E.csproj`.

## 2. In-browser axe-core WCAG audit across all rendered views

<!-- test(web): add axe-core WCAG audit for all rendered views -->

- [x] 2.1 Add `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`, tagged
  `[Trait("Category", TestCategories.E2E)]` and gated with
  `[RequiresEnvVarTheory(EnvVar = E2EGate.EnvVar)]` + `Gate()`. Drive a
  data-driven case per rendered view: seed the session state that renders its
  real UI (anonymous, reset-token cookie, full session, force-reset-limited
  session, fresh-sudo session, or admin role), load the page in Chromium, run
  axe-core scoped to the WCAG 2.0/2.1 A and AA rule tags, and assert zero
  violations.
- [x] 2.2 Assert the audit landed on the intended view (the URL path matches),
  so a mis-seeded state fails instead of auditing a redirect to `/login`.
- [x] 2.3 Run with `FREEBOARD_TEST_E2E=1` and confirm zero violations across all
  views; confirm the suite SKIPS cleanly with no browser env.

## 3. Verify

<!-- verification step -->

- [x] 3.1 `dotnet build tests/Freeboard.WebE2E` is clean.
- [x] 3.2 With `FREEBOARD_TEST_E2E=1`, the audit passes across every rendered
  view (zero A/AA violations).
- [x] 3.3 With no browser env, the audit skips cleanly like the rest of the E2E
  suite.
