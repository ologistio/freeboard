## Context

The project tiers tests by `Category` trait: untagged Unit (always-on, in-process),
`Integration` (MySQL/SMTP), `E2E` (real browser, gated), and `NFR`
(non-functional, deterministic, no external services). The `NFR` tier existed in
code (`TestCategories.Nfr`) and CI (a stub job) but had no tests.

## Decisions

- Two tiers, not one. Accessibility splits cleanly into checks that need a live
  DOM (colour contrast, ARIA computed roles, focus order) and checks that do
  not (language, title, headings, viewport, label-for wiring). The DOM-free
  checks go in an always-on NFR test so a regression fails fast on every run
  with no browser; the DOM checks go in the gated browser tier via axe-core.
- Static baseline runs in-process. It uses the in-memory `AuthWebFactory`
  (`CreateClient`), not the HTTPS Kestrel boot the browser tests use. The static
  checks read server-rendered HTML, which the in-memory server returns
  identically, so the baseline needs neither a socket, a cert, nor a browser.
- Tag the baseline `NFR`, not Unit. Accessibility is a non-functional
  requirement and the repo reserves a tier and CI job for it. Tagging it `NFR`
  keeps it out of the Unit coverage run and routes it to the NFR job. Because it
  lives in the `WebE2E` project, the E2E job's run excludes `Category=NFR` so it
  does not also execute there.
- axe-core via `Deque.AxeCore.Playwright`. The package bundles the axe-core
  engine and injects it through Playwright, so the audit needs no network fetch
  and no vendored JS blob. The audit is scoped to the `wcag2a`, `wcag2aa`,
  `wcag21a`, `wcag21aa` rule tags - the conformance target most teams hold to -
  and asserts zero violations.
- AngleSharp for HTML parse. The label-association and heading-count checks need
  real HTML parsing; regex over markup is fragile. AngleSharp is the standard
  .NET HTML parser and keeps the assertions readable.

## Risks / Trade-offs

- The axe rule set is version-pinned by `Deque.AxeCore.Playwright`. Upgrading the
  package can surface new violations on unchanged pages. That is the audit
  working as intended, but it means a dependency bump can fail the E2E tier
  without a product change. Accepted: the alternative (pinning a rule allowlist)
  hides real regressions.
- The static baseline overlaps slightly with the axe audit (both check labels).
  Accepted: the baseline must stand alone without a browser, and the overlap is
  cheap.
