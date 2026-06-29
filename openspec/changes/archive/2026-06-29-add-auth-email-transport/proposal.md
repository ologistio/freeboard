## Why

The auth flows that require email delivery (password reset, magic-link MFA,
sudo step-up magic-link) are already merged, but no concrete email transport
exists and none is registered. As a result password reset cannot be enabled (the
startup fail-fast would throw) and magic-link MFA is never offered. This change
supplies the missing transport behind a generic, provider-level email seam so
operators can turn those flows on, and so future transactional/operational email
can reuse the same seam.

## What Changes

- Add a generic email seam in `Freeboard.Core`: `IEmailSender` with a single
  `SendAsync(EmailMessage, ct)` method, and an `EmailMessage` record (`To`,
  `Subject`, `TextBody`, optional `HtmlBody`). A new provider implements only one
  method. `HtmlBody` is null today (text-only) and is the additive onramp to HTML
  later. Core pulls no web/MailKit dependency.
- Add `LoggingEmailSender`: a non-delivering log-only sink for development and
  unit tests. It logs the recipient and subject, never the body (which carries the
  token); no log entry at Information level or above contains the token. It
  satisfies the presence of a registered sender but does not deliver a usable
  token. The system logs a startup warning when this transport is selected.
- Add `SmtpEmailSender`: a real SMTP transport built on MailKit, sending plain-
  text mail (and a multipart/alternative when `HtmlBody` is set).
- Add `EmailOptions` bound from a new `Email` config section (transport
  selection, from-identity, SMTP host/port/TLS/credentials/timeout).
  `Smtp:UseStartTls` defaults to true, `Smtp:Port` to 587, `Smtp:TimeoutSeconds`
  to 30. The auth-link base URL is NOT part of generic `EmailOptions`; it stays
  under `Auth:Email:BaseUrl`.
- Add `AuthEmailService` in the web auth layer: it builds the auth messages
  (password reset, magic link) and delegates delivery to `IEmailSender`. It owns
  the auth base URL and validates it (absolute http/https with a host) eagerly, so
  an invalid `Auth:Email:BaseUrl` fails fast at startup when the service is
  registered.
- Register the sender in `Program.cs` based on `Email:Transport`
  (`none` | `log` | `smtp`). `none` registers nothing, preserving today's
  behaviour. Register `AuthEmailService` only when a sender is present. The
  password-reset startup fail-fast keys on `AuthEmailService` presence: enabling
  password reset with `Transport=none` still throws. A second fail-fast rejects
  `Transport=smtp` with a missing host or invalid from-address, and rejects an
  unknown `Transport` value.
- Catch and log a send failure in the forgot-password handler so the response
  stays a uniform 200 (closing an account-enumeration oracle on the known-user
  branch). The failure log records the recipient and error type only, never the
  token.
- Migrate the auth consumers (forgot-password, magic-link login, sudo magic-link,
  the MFA factor service) from the old per-kind seam to optionally resolving
  `AuthEmailService`. The optional-presence semantics are unchanged.
- Add a MailKit-backed integration test that sends both a password-reset and a
  magic-link email through `AuthEmailService` -> `SmtpEmailSender` to a Mailpit
  container, gated on `FREEBOARD_TEST_SMTP` and skipping cleanly when unset.

MIT placement: the seam (`IEmailSender`, `EmailMessage`) lives in
`Freeboard.Core`; the concrete senders and options live in the web project
(`src/Freeboard/Email/`) and `AuthEmailService` in `src/Freeboard/Auth/`. They
are not EE features, so nothing goes in `Freeboard.Enterprise`. MailKit is
referenced only by the web project. Agent and CLI gain no email dependency.

## Capabilities

### New Capabilities

- `auth-email-transport`: config-driven selection of the concrete `IEmailSender`
  (none / log / smtp); the log sink's token safety and non-production startup
  warning; the SMTP sender delivering an `EmailMessage` (plain text, or
  multipart when `HtmlBody` is set); `AuthEmailService` building the auth messages
  and keeping forgot-password enumeration-safe; and the seam staying web-scoped
  and EE-free with `IEmailSender`/`EmailMessage` in Core.

### Modified Capabilities

<!-- None. The consuming auth flows already define their behaviour; this change
     supplies the transport behind the existing optional seam and does not change
     any existing requirement. -->

## Impact

- New code: `src/Freeboard.Core/Email/IEmailSender.cs`,
  `src/Freeboard.Core/Email/EmailMessage.cs`;
  `src/Freeboard/Email/EmailOptions.cs`,
  `src/Freeboard/Email/LoggingEmailSender.cs`,
  `src/Freeboard/Email/SmtpEmailSender.cs`,
  `src/Freeboard/Email/EmailRegistration.cs`;
  `src/Freeboard/Auth/AuthEmailService.cs`; wiring in `src/Freeboard/Program.cs`;
  a try/catch around the forgot-password send in
  `src/Freeboard/Auth/AuthEndpoints.cs`; consumer migration in
  `MfaLoginEndpoints.cs`, `SudoEndpoints.cs`, `MfaFactorService.cs`.
- Removed: the old per-kind seam `Freeboard.Persistence.Auth.IAuthEmailSender`.
- New dependency: MailKit (web project only).
- Config: new generic `Email` section, plus the existing `Auth:Email:BaseUrl`.
  SMTP password is a secret supplied via env / user-secrets / config provider;
  never committed.
- Tests: web auth endpoint tests back a real `AuthEmailService` with a recording
  `IEmailSender`; a `LoggingEmailSender` unit test; an `EmailRegistration` /
  `AuthEmailService` registration test set; a gated Mailpit integration test; an
  architecture test pinning MailKit to the web project and Core MailKit-free.
- No change to production behaviour outside email delivery. With `Transport=none`
  (the default) the app behaves exactly as it does today.

## Non-goals

- No front-end UI. The reset and magic-link emails point at placeholder front-end
  paths that will not resolve until the UI ships; that is accepted.
- No HTML email bodies now (plain text only to start; `HtmlBody` is the onramp,
  tracked as a follow-up issue).
- No additional provider-API transports now (SES, Postmark, SendGrid, etc.); the
  generic seam makes them a one-method addition, tracked as follow-up issues.
- No change to the sudo magic-link concurrency edge cases or any other auth flow
  logic.
- No email dependency in `Freeboard.Agent` or `Freeboard.CLI`.
