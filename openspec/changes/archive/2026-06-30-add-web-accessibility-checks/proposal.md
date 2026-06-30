## Why

The web app's public auth pages (sign-in, forgot-password, reset-password) are
the first thing every user reaches, including users on assistive technology, but
nothing guards their accessibility. A future edit can drop a form label, a page
heading, or the page language and no test would catch it. The project already
reserves a non-functional test tier (`Category=NFR`) and a CI job for exactly
this kind of quality gate, but that tier had no tests, so the NFR job was a
passing stub.

This change adds accessibility coverage for the public auth pages in two tiers:
a fast, always-on static baseline that needs no browser, and a high-fidelity
in-browser audit that runs the axe-core WCAG engine against the live DOM.

## What Changes

- Add an always-on static accessibility baseline
  (`tests/Freeboard.WebE2E/AccessibilityBaselineTests.cs`, tagged
  `Category=NFR`). It serves each public auth page through the in-memory
  `AuthWebFactory` (no Chromium, no MySQL), parses the rendered HTML with
  AngleSharp, and asserts the static WCAG checks: a non-empty `html[lang]`, a
  non-empty `<title>`, exactly one `<h1>`, a `meta[name=viewport]`, and a
  programmatic label for every user-operable form control.
- Add an in-browser axe-core audit
  (`tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs`, tagged
  `Category=E2E`). It loads each page in Chromium and runs axe-core for WCAG 2.0
  and 2.1 level A and AA, asserting zero violations. It is gated like the rest of
  the browser suite and SKIPS cleanly without a browser.
- Activate the NFR CI job: replace the stub step in `.github/workflows/test.yml`
  with a real build-and-run of `--filter "Category=NFR"`, and exclude
  `Category=NFR` from the E2E job's run so the baseline does not double-run.
- Add two test-only package references to `tests/Freeboard.WebE2E`: `AngleSharp`
  (HTML parse) and `Deque.AxeCore.Playwright` (bundles the axe-core engine).

No production code changes. The public auth pages already satisfy both tiers
(zero axe A/AA violations); the tests lock that in and fail on regression.

This change is MIT (default). All code lands in the test project and the CI
workflow, both MIT. Nothing is added to or moved into `src/Freeboard.Enterprise`;
accessibility of the public pages is a base-product quality, not a paid
enterprise carve-out.

## Non-goals

- No production code or markup change. The pages already pass; this change only
  adds verification.
- No accessibility coverage beyond the three public auth pages. The
  authenticated `/account` and `/admin` pages are out of scope here and can be
  added later by extending the same two tiers.
- No full manual WCAG audit, no screen-reader testing, and no coverage of
  criteria axe-core cannot detect automatically. The axe tier asserts the
  automatable A/AA subset, not full conformance.
- No new always-on browser dependency. The always-on tier is browser-free; the
  browser audit stays gated and skips without Chromium.

## Capabilities

### New Capabilities

- `web-accessibility`: An accessibility contract for the public auth pages,
  enforced in two test tiers - an always-on, browser-free static baseline
  (page language, title, single top-level heading, viewport, labelled form
  controls) in the NFR tier, and an in-browser axe-core WCAG 2.0/2.1 A/AA audit
  with zero violations in the gated E2E tier.

### Modified Capabilities

<!-- None. -->

## Impact

- `tests/Freeboard.WebE2E/AccessibilityBaselineTests.cs` - new, NFR-tagged.
- `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs` - new, E2E-tagged.
- `tests/Freeboard.WebE2E/Freeboard.WebE2E.csproj` - add `AngleSharp` and
  `Deque.AxeCore.Playwright`.
- `.github/workflows/test.yml` - activate the NFR job; exclude `Category=NFR`
  from the E2E run.
- No production code, database, or migration change.
