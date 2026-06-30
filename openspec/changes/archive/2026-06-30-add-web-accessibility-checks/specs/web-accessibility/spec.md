## ADDED Requirements

### Requirement: Public auth pages meet a static accessibility baseline on every run

The web app's public auth pages SHALL meet a static accessibility baseline that
is verified on every test run without a browser - sign-in (`/login`),
forgot-password (`/forgot-password`), and reset-password (`/reset-password`).
Each page SHALL declare a non-empty document language, a non-empty page
title, exactly one top-level heading, and a responsive viewport, and SHALL give
every user-operable form control a programmatic label (a `<label for>` matching
the control id, an `aria-label`, an `aria-labelledby`, or a wrapping `<label>`).
Hidden, submit, button, reset, and image inputs are exempt from the label
requirement because the user does not enter a value into them.

This baseline SHALL be enforced in the non-functional (`NFR`) test tier and SHALL
run in-process against the server-rendered HTML, so it executes in a plain
`dotnet test` with no browser and no external services, and a regression fails
fast on every run.

#### Scenario: A page meets the baseline

- **WHEN** a public auth page is served and its rendered HTML is inspected
- **THEN** the `<html>` element has a non-empty `lang`, the page has a non-empty
  `<title>`, there is exactly one `<h1>`, a `meta[name=viewport]` is present, and
  every user-operable form control has a programmatic label

#### Scenario: A dropped form label fails the baseline

- **WHEN** a public auth page renders a user-operable form control with no
  associated label
- **THEN** the baseline check for that page fails and identifies the unlabelled
  control

#### Scenario: The baseline runs without a browser

- **WHEN** the test suite runs with no browser and no external services
- **THEN** the static accessibility baseline still executes and reports its
  result, rather than skipping

### Requirement: Public auth pages have no automated WCAG A/AA violations

Each public auth page SHALL pass an automated axe-core audit against the live
browser DOM for the WCAG 2.0 and 2.1 level A and AA success criteria, with zero
violations. This audit covers the rules a static HTML check cannot, such as
colour contrast, ARIA wiring, and focus order.

The audit SHALL run in the browser (`E2E`) test tier and SHALL be gated on a
launchable browser: when no browser is available the audit SKIPS cleanly rather
than failing, consistent with the rest of the browser suite.

#### Scenario: A page has no A/AA violations

- **WHEN** a public auth page is loaded in a real browser and audited with
  axe-core for WCAG 2.0/2.1 level A and AA
- **THEN** the audit reports zero violations

#### Scenario: The audit skips cleanly without a browser

- **WHEN** the audit runs with no launchable browser
- **THEN** it is skipped (not failed), like the rest of the browser E2E tier
