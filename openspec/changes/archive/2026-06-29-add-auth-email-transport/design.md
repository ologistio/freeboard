## Context

The auth flows that need email are merged and wired against an optional sender.
This change generalizes the low-level per-email-kind seam into a generic,
provider-level email interface so a new provider implements only one method, and
names the abstraction `Email` (not `AuthEmail`) so future transactional or
operational email can reuse it.

The layering after this change:

- `Freeboard.Core/Email/IEmailSender.cs`: `SendAsync(EmailMessage, ct)` - the one
  method a provider implements. `EmailMessage` (`To`, `Subject`, `TextBody`,
  `HtmlBody?`) carries a fully built message. Core references nothing else.
- `src/Freeboard/Email/`: the concrete transports (`LoggingEmailSender`,
  `SmtpEmailSender`), `EmailOptions`, and the `EmailRegistration` switch. MailKit
  lives only here.
- `src/Freeboard/Auth/AuthEmailService.cs`: the web auth consumer. It builds the
  password-reset and magic-link `EmailMessage`s and delegates to `IEmailSender`.
  It owns the auth base URL.

Consumers resolve `AuthEmailService` optionally via
`sp.GetService<AuthEmailService>()`:

- `AuthEndpoints.cs` (forgot-password): sends only when a user exists,
  `PasswordResetEnabled` is true, and the service is present; always returns a
  uniform 200.
- `MfaLoginEndpoints.cs` and `SudoEndpoints.cs`: send the magic link only when
  the service is present.
- `MfaFactorService.cs`: offers the magic-link factor only when the service is
  present.
- `Program.cs`: fails fast at startup if `Auth:PasswordResetEnabled` is true but
  no `AuthEmailService` is registered (i.e. `Email:Transport` is none).

## Goals / Non-Goals

**Goals:**

- A generic provider seam in Core (`IEmailSender` + `EmailMessage`) that a new
  provider implements with one method.
- A real SMTP sender (MailKit) and a log-only sink, selectable from config.
- A web auth service that builds the auth messages and stays enumeration-safe.
- Zero change to production behaviour outside email delivery; default
  (`Transport=none`) is identical to today.

**Non-Goals:**

- Front-end UI; links point at placeholder paths that will not resolve yet.
- HTML bodies now (`HtmlBody` is the onramp; plain text only to start).
- Additional provider-API transports now (SES, Postmark, SendGrid); the seam
  makes each a one-method addition, tracked separately.
- Any change to auth flow logic, including sudo magic-link concurrency edges.
- Any email dependency in `Freeboard.Agent`, `Freeboard.CLI`, or
  `Freeboard.Enterprise`.

## Decisions

### Generic seam in Core; transports in the web project

`IEmailSender` and `EmailMessage` go in `Freeboard.Core/Email/` so any component
can build a message and the seam carries no transport concern. The concrete
senders (`LoggingEmailSender`, `SmtpEmailSender`), `EmailOptions`, and the
registration switch go in `src/Freeboard/Email/`.

Rationale: keeping the transports in the web project keeps MailKit out of Core
(Core references nothing) and respects the one-way EE rule (these are not paid,
enterprise-gated features, so they are MIT and must not go in
`Freeboard.Enterprise`). The web project is the only component that wires the full
auth stack, so the registration switch belongs there. Agent and CLI never
reference the web project, so they gain no email or MailKit dependency.

### EmailMessage with an HtmlBody onramp

`EmailMessage` carries `To`, `Subject`, `TextBody`, and `HtmlBody` (default
null). Today every caller builds text-only messages (`HtmlBody` null). When
`HtmlBody` is set, `SmtpEmailSender` sends a multipart/alternative (MailKit
`BodyBuilder` with both `TextBody` and `HtmlBody`). This is the smallest additive
path to HTML later without a seam change.

### AuthEmailService builds the auth messages

`AuthEmailService` exposes the two methods the auth endpoints already call -
`SendPasswordResetAsync(email, resetToken, ct)` and
`SendMagicLinkAsync(email, magicLinkToken, ct)` - so the call sites change only
the resolved type. It builds absolute links from the auth base URL (trailing
slash trimmed): `{BaseUrl}/reset-password?token={UrlEncoded(token)}` and
`{BaseUrl}/auth/magic-link?token={UrlEncoded(token)}`. The magic-link URL carries
only the link token; the client retains the `mfa_token` / `challenge_id`, so a
one-click cross-device link is out of scope. It builds a plain-text
`EmailMessage` (HtmlBody null) and calls `IEmailSender.SendAsync`. The token is
never logged.

### Config split: Email vs Auth:Email:BaseUrl

`EmailOptions` is bound from the generic `Email` section:

- `Transport`: `none` | `log` | `smtp`. Default `none`.
- `FromAddress`, `FromName`.
- `Smtp:Host`, `Smtp:Port`, `Smtp:UseStartTls`, `Smtp:Username`,
  `Smtp:Password`, `Smtp:TimeoutSeconds`.

The auth-link base URL is auth-specific, not a generic email concern, so it stays
under `Auth:Email:BaseUrl`. `AuthEmailService` reads and validates it.

`UseStartTls` is a bool (STARTTLS on 587 in production, unencrypted for a local
Mailpit on 1025); implicit TLS has no named consumer, so an enum would be
speculative. `TimeoutSeconds` maps to MailKit's millisecond `SmtpClient.Timeout`.
The link paths are constants in `AuthEmailService`, not config: only `BaseUrl`
varies between deployments. The SMTP password is a secret supplied out-of-band.

### Where the fail-fasts live after the split

- `EmailRegistration.Add` validates only the generic transport: `Transport=smtp`
  requires a non-empty `Email:Smtp:Host` and a parseable bare-addr-spec
  `Email:FromAddress`; an unknown `Transport` throws. The bare-addr-spec check
  (`HasLocalAndDomain` plus the equality with the parsed address, so a
  display-name form is rejected) is unchanged from the prior code.
- The auth base-URL validation moves to `AuthEmailService`'s constructor: it must
  be an absolute http/https URL with a host. Because `AuthEmailService` is
  registered only when a sender is present, an invalid `Auth:Email:BaseUrl` fails
  fast at startup.
- `Program.cs`: the password-reset startup fail-fast keys on `AuthEmailService`
  presence (transport none -> no service -> throw when
  `PasswordResetEnabled=true`).

### Log transport startup warning

`LoggingEmailSender` is a non-delivering developer sink. When `Email:Transport`
is `log`, `Program.cs` emits an `ILogger` Warning at startup ("The 'log' email
transport is a non-delivering development sink and must not be used in
production.") so it is never mistaken for a working transport.

### Send failure must not break the uniform forgot-password response

The forgot-password handler is enumeration-safe by always returning a uniform
200, but it only calls the sender on the known-user branch. A throw from the send
(an SMTP failure, a bad host, a timeout) would escape only for a real account and
surface as a 500 - an account-enumeration oracle. The mint-and-send is wrapped in
`catch (Exception ex) when (ex is not OperationCanceledException)` that logs the
recipient and `ex.GetType().Name` only (never the token, body, or exception
object) and falls through to the same uniform 200. The token row may already
exist; it expires unused. The magic-link send paths run post-identification and
are left to propagate.

### Token is a credential: not logged at info level

`LoggingEmailSender` logs the recipient and subject, never the body (which
carries the token). `SmtpEmailSender` likewise never logs the body. The forgot-
password failure log records the recipient and error type only. No token
fingerprint is logged: nothing correlates it, so it would be unused liability.

## Risks / Trade-offs

- [Dead links until UI ships] -> Accepted; emails carry valid tokens but the
  target paths do not exist yet.
- [Forgot-password send failure as an enumeration oracle] -> Closed by the
  uniform-200 try/catch.
- [Magic-link send failures surface to the caller] -> A throw becomes a 500 to an
  already-identified caller and leaks nothing about account existence. No
  retry/queue is added.
- [Send-cap consumed before delivery] -> Both magic-link paths record the send
  before calling the sender, so a failed delivery still burns one capped attempt.
  Accepted; the failure is logged.
- [Magic-link emails are not self-contained] -> The service passes only the link
  token; a one-click cross-device link needs a seam change, tracked separately.
- [New dependency: MailKit] -> Justified; the recommended .NET SMTP library, web
  project only.
- [Integration test needs a container] -> Gated on `FREEBOARD_TEST_SMTP` and skips
  cleanly when unset, like the MySQL tests.

## Migration Plan

No data migration. Rollout is config-only: operators set `Email:Transport` and
the SMTP settings plus `Auth:Email:BaseUrl` to enable delivery, then may set
`Auth:PasswordResetEnabled=true`. Rollback is setting `Transport=none`, which
restores today's behaviour. Existing deployments that set neither are unaffected.

## Open Questions

- Placeholder link paths (`/reset-password`, `/auth/magic-link`) are proposed
  defaults; the UI work may rename them. They are relative to `Auth:Email:BaseUrl`,
  so a later rename is a small string change.

## Verification

- `dotnet build` and `dotnet test` (no external deps) pass; the Mailpit
  integration test skips when `FREEBOARD_TEST_SMTP` is unset.
- Web endpoint tests assert: a real `AuthEmailService` backed by a recording
  `IEmailSender` records a message whose `To` is the user email and whose body
  carries the expected absolute URL; forgot-password stays a uniform 200 when the
  sender throws and never logs the token.
- `LoggingEmailSender` unit test asserts it logs recipient and subject, does not
  throw, and does not log the token at the default level.
- `AuthEmailService` tests assert the `Auth:Email:BaseUrl` fail-fast and the
  password-reset-enabled-with-transport-none startup throw.
- An architecture test pins MailKit to the web project and keeps Core MailKit-free.
- With Mailpit running and `FREEBOARD_TEST_SMTP` set, the integration test sends a
  reset and a magic-link message through `AuthEmailService` -> `SmtpEmailSender`
  and asserts one message per recipient carrying the expected absolute URL.
