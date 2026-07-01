## ADDED Requirements

### Requirement: Rendered web views have no automated WCAG A/AA violations

Every rendered web view SHALL pass an automated axe-core audit against the live
browser DOM for the WCAG 2.0 and 2.1 level A and AA success criteria, with zero
violations. The audited views are the public auth pages (sign-in,
forgot-password, reset-password, first-admin setup, and the login MFA challenge
pages), the authenticated account and MFA pages, the sudo-gated enrolment pages,
and the admin pages.

Each view SHALL be audited in the session state that renders its real UI: an
anonymous request for the public pages, a valid reset-token cookie for the
reset-password form, a full session for the account pages, a force-reset-limited
session for the completion page, a fresh-sudo session for the sudo-gated
enrolment pages, and an admin-role session for the admin pages. The audit SHALL
confirm it reached the intended view and did not follow a redirect (for example
to the login page), so a mis-seeded state fails the check rather than silently
auditing the wrong page.

Views that render no standalone UI are out of scope: the GET-redirect POST
endpoints (sign-out, session-revoke, the MFA remove and recovery-regenerate
handlers) and the magic-link consumer, which only redirect.

The audit SHALL run in the browser (`E2E`) test tier and SHALL be gated on a
launchable browser: when no browser is available the audit SKIPS cleanly rather
than failing, consistent with the rest of the browser suite.

#### Scenario: A view has no A/AA violations

- **WHEN** a rendered view is loaded in a real browser in the session state that
  renders its real UI and audited with axe-core for WCAG 2.0/2.1 level A and AA
- **THEN** the audit reports zero violations

#### Scenario: A mis-seeded state fails instead of auditing the wrong page

- **WHEN** the session state for a view is wrong and the request is redirected
  away (for example to the login page)
- **THEN** the check fails because the landed URL does not match the intended
  view, rather than passing against the redirected page

#### Scenario: The audit skips cleanly without a browser

- **WHEN** the audit runs with no launchable browser
- **THEN** it is skipped (not failed), like the rest of the browser E2E tier
