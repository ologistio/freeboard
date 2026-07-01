## ADDED Requirements

### Requirement: Rendered web views meet every supported accessibility standard

Every rendered web view SHALL pass an automated axe-core audit against the live
browser DOM with zero violations, evaluated against every accessibility standard
the engine supports: WCAG 2.0, 2.1, and 2.2 at levels A, AA, and AAA, Section
508, and EN 301 549, plus the engine's best-practice rules. Only the engine's
experimental rules are excluded, because they are unstable across engine
versions. The audited views are the public auth pages (sign-in, forgot-password,
reset-password, first-admin setup, and the login MFA challenge pages), the
authenticated account and MFA pages, the sudo-gated enrolment pages, and the
admin pages.

Each view SHALL be audited in the session state that renders its real UI: an
anonymous request for the public pages, a valid reset-token cookie for the
reset-password form, a full session for the account pages, a force-reset-limited
session for the completion page, a fresh-sudo session for the sudo-gated
enrolment pages, and an admin-role session for the admin pages. The audit SHALL
confirm it reached the intended view and did not follow a redirect (for example
to the login page), so a mis-seeded state fails the check rather than silently
auditing the wrong page.

When the audit fails, the reported message SHALL identify, per violation, the
rule, its impact, its help text and documentation URL, and each failing node's
CSS selector and HTML, so a developer or agent can locate and fix the issue
without re-running the browser.

Views that render no standalone UI are out of scope: the GET-redirect POST
endpoints (sign-out, session-revoke, the MFA remove and recovery-regenerate
handlers) and the magic-link consumer, which only redirect.

The audit SHALL run in the browser (`E2E`) test tier and SHALL be gated on a
launchable browser: when no browser is available the audit SKIPS cleanly rather
than failing, consistent with the rest of the browser suite.

#### Scenario: A view has no violations under any supported standard

- **WHEN** a rendered view is loaded in a real browser in the session state that
  renders its real UI and audited with axe-core against WCAG 2.0/2.1/2.2 A/AA/AAA,
  Section 508, EN 301 549, and best-practice rules
- **THEN** the audit reports zero violations

#### Scenario: A mis-seeded state fails instead of auditing the wrong page

- **WHEN** the session state for a view is wrong and the request is redirected
  away (for example to the login page)
- **THEN** the check fails because the landed URL does not match the intended
  view, rather than passing against the redirected page

#### Scenario: A failure reports actionable detail

- **WHEN** a view has one or more violations
- **THEN** the failure message lists each violation's rule, impact, help text and
  documentation URL, and the failing node's selector and HTML

#### Scenario: The audit skips cleanly without a browser

- **WHEN** the audit runs with no launchable browser
- **THEN** it is skipped (not failed), like the rest of the browser E2E tier
