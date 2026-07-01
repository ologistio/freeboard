## Why

The web app's pages - the public auth pages, the authenticated account and MFA
pages, the sudo-gated enrolment pages, and the admin pages - are what every
user reaches, including users on assistive technology, but nothing guards their
accessibility. A future edit can introduce a colour-contrast regression, a
missing form label, an unlabelled control, or broken ARIA and no test would
catch it.

This change adds an in-browser accessibility audit that loads every rendered
view in its real session state and runs the axe-core WCAG engine against the
live DOM.

## What Changes

- Add `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs` (tagged
  `Category=E2E`). It loads each rendered view in Chromium and runs axe-core for
  WCAG 2.0 and 2.1 level A and AA, asserting zero violations. It is gated like
  the rest of the browser suite and SKIPS cleanly without a browser.
- Cover every rendered view, each in the session state that renders its real UI,
  seeded the same way the other E2E tests seed sessions (the
  `E2EAppFixture`/`AuthWebFactory` helpers `SeedSession`, `SeedSessionWithSudo`,
  and `MakeUser`):
  - public: `/login`, `/forgot-password`, `/reset-password` (via the
    reset-token cookie), `/setup`, and the `/login/mfa/*` challenge pages;
  - full session: `/account`, `/account/mfa`, `/account/password/change`,
    `/account/sessions`, `/account/sudo`;
  - force-reset-limited session: `/account/complete-reset`;
  - fresh-sudo session: `/account/mfa/totp`, `/account/mfa/passkey`;
  - admin role: `/admin/users`, `/admin/usercredential`.
- Assert the audit reached the intended view (the landed URL matches), so a
  mis-seeded state fails instead of silently auditing a redirect to `/login`.
- Add one test-only package reference to `tests/Freeboard.WebE2E`:
  `Deque.AxeCore.Playwright` (bundles the axe-core engine).

Views that render no standalone UI are out of scope: the GET-redirect POST
endpoints (`/logout`, `/account/sessions/revoke`, the MFA `*/remove` and
recovery-regenerate handlers) and the magic-link consumer (`/auth/magic-link`).

No production code changes. The pages already satisfy the audit (zero axe A/AA
violations); the test locks that in and fails on regression.

This change is MIT (default). All code lands in the test project, which is MIT.
Nothing is added to or moved into `src/Freeboard.Enterprise`; accessibility of
the pages is a base-product quality, not a paid enterprise carve-out.

## Non-goals

- No production code or markup change. The pages already pass; this change only
  adds verification.
- No separate non-browser test tier. The audit runs entirely in the gated E2E
  browser tier; there is no static HTML replica to keep in sync.
- No full manual WCAG audit, no screen-reader testing, and no coverage of
  criteria axe-core cannot detect automatically. The audit asserts the
  automatable A/AA subset, not full conformance.
- No coverage of views that render no standalone UI (GET-redirect POST endpoints
  and the magic-link consumer).

## Capabilities

### New Capabilities

- `web-accessibility`: An accessibility contract for the app's rendered views,
  enforced by an in-browser axe-core WCAG 2.0/2.1 A/AA audit (zero violations)
  in the gated E2E tier, covering every rendered view in the session state that
  renders its real UI.

### Modified Capabilities

<!-- None. -->

## Impact

- `tests/Freeboard.WebE2E/AccessibilityAuditE2ETests.cs` - new, E2E-tagged.
- `tests/Freeboard.WebE2E/Freeboard.WebE2E.csproj` - add
  `Deque.AxeCore.Playwright`.
- No production code, CI, database, or migration change.
