## Context

The project tiers tests by `Category` trait: untagged Unit (always-on,
in-process), `Integration` (MySQL/SMTP), and `E2E` (real browser, gated). The
browser E2E suite already boots the real app over an HTTPS Kestrel origin with
in-memory fakes and seeds sessions to reach authenticated pages.

## Decisions

- One tier, in the browser. Accessibility is verified with axe-core against the
  live DOM, which needs a real browser, so it lives entirely in the gated `E2E`
  tier. There is no separate static/non-browser replica: a second tier would
  duplicate coverage and add a parallel route list to keep in sync.
- Cover every rendered view, in its real state. The audit is data-driven over a
  catalogue of routes, each tagged with the session state that renders its real
  UI. States are seeded with the same `E2EAppFixture`/`AuthWebFactory` helpers
  the rest of the E2E suite uses (`SeedSession`, `SeedSessionWithSudo`,
  `MakeUser`) and set as the session cookie on the browser context. The
  reset-password form is reached through its `?token=` GET, which stashes the
  transient reset-token cookie and redirects to the bare form.
- Assert the landed URL. Each case checks that the browser ended on the intended
  path, not a redirect to `/login` or a funnel page. Without this a mis-seeded
  state would audit the wrong page and pass falsely.
- Scope to rendered views. Endpoints whose GET only redirects (sign-out,
  session-revoke, the MFA remove and recovery-regenerate handlers) and the
  magic-link consumer render no standalone UI, so they are excluded.
- axe-core via `Deque.AxeCore.Playwright`. The package bundles the axe-core
  engine and injects it through Playwright, so the audit needs no network fetch
  and no vendored JS blob.
- Maximal standard set. The audit runs every standard the engine supports -
  `wcag2a/aa/aaa`, `wcag21a/aa`, `wcag22aa`, `section508`, `EN-301-549`, and
  `best-practice` - and asserts zero violations. Only the `experimental` tag is
  excluded: those rules are explicitly unstable and would break the suite on an
  engine bump for no conformance gain. Enabling AAA surfaced two real gaps on the
  existing pages (button contrast below 7:1, and an empty actions-column table
  header); both are fixed in this change rather than lowering the bar.
- Actionable failure output. The assertion prints, per violation, the rule,
  impact, help text, docs URL, and each failing node's selector and HTML, so a
  developer or agent can fix it from the test log alone.

## Risks / Trade-offs

- The axe rule set is version-pinned by `Deque.AxeCore.Playwright`. Upgrading the
  package can surface new violations on unchanged pages. That is the audit
  working as intended, but a dependency bump can fail the E2E tier without a
  product change. Accepted: the alternative (pinning a rule allowlist) hides real
  regressions.
- Being browser-only, the audit does not run in a plain `dotnet test` without a
  browser; it runs in CI's E2E job (which installs Chromium) and skips locally
  otherwise. Accepted: axe needs a live DOM, and a non-browser replica is the
  duplication this design avoids.
