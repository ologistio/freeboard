# auth-email-transport Specification

## Purpose
TBD - created by archiving change add-auth-email-transport. Update Purpose after archive.
## Requirements
### Requirement: Config-driven sender selection

The system SHALL select the concrete `IEmailSender` registration from the
`Email:Transport` setting, with values `none`, `log`, or `smtp`. The default
SHALL be `none`. When `none`, the system SHALL register no sender and no
`AuthEmailService`, so the existing auth flows behave exactly as they do with no
sender present (forgot-password returns a uniform 200 and magic-link MFA is not
offered).

#### Scenario: Default transport registers no sender

- **WHEN** `Email:Transport` is unset or set to `none`
- **THEN** no `IEmailSender` is registered
- **AND** `forgot-password` returns a uniform 200 without sending mail
- **AND** magic-link MFA is not offered

#### Scenario: Log transport registers the logging sender

- **WHEN** `Email:Transport` is `log`
- **THEN** `LoggingEmailSender` is registered as the `IEmailSender`

#### Scenario: SMTP transport registers the SMTP sender

- **WHEN** `Email:Transport` is `smtp`
- **THEN** `SmtpEmailSender` is registered as the `IEmailSender`

#### Scenario: Password reset enabled with no transport fails fast

- **WHEN** `Auth:PasswordResetEnabled` is true and `Email:Transport` is `none`
- **THEN** the application throws at startup so the password-reset flow can never
  run without a way to deliver the token

#### Scenario: SMTP transport with missing host or from-address fails fast

- **WHEN** `Email:Transport` is `smtp` and `Email:Smtp:Host` is missing or blank,
  or `Email:FromAddress` is missing, blank, or not a parseable bare email address
- **THEN** the application throws at startup with a clear message, so an SMTP
  transport that could not deliver a token never starts

#### Scenario: Unknown transport value fails fast

- **WHEN** `Email:Transport` is set to a value that is not `none`, `log`, or
  `smtp`
- **THEN** the application throws at startup rather than silently registering no
  sender

### Requirement: Logging sender does not leak tokens and warns in non-production

The `LoggingEmailSender` SHALL log the recipient email address and the subject,
and SHALL NOT include the message body (which carries the reset or magic-link
token) in any log entry at Information level or above. It SHALL complete the send
without throwing. `LoggingEmailSender` is a non-delivering developer sink: it
satisfies the presence of a registered sender (so password reset can be enabled
and magic-link is offered) but does not deliver a usable token. When the `log`
transport is selected, the system SHALL emit a startup warning that it is a
non-delivering development sink and must not be used in production.

#### Scenario: Send is logged without the token

- **WHEN** the logging sender is asked to send a message whose body carries a
  reset or magic-link token
- **THEN** a log entry records the recipient and the subject
- **AND** the raw token does not appear in any captured log entry at Information
  level or above
- **AND** the call completes without throwing

#### Scenario: Selecting the log transport warns at startup

- **WHEN** `Email:Transport` is `log`
- **THEN** the system logs a Warning at startup that the log transport is a
  non-delivering development sink and must not be used in production

### Requirement: Forgot-password stays uniform when the sender fails

The forgot-password handler SHALL return a uniform 200 even when the registered
sender throws. A send failure SHALL be caught and logged (recipient and error
type only, never the token) and SHALL NOT change the response, so a failed
delivery to a known account is indistinguishable from a request for an unknown
account. This keeps the flow enumeration-safe under send failure.

#### Scenario: Forgot-password returns 200 when the sender throws

- **WHEN** a known account requests a password reset and the registered sender
  throws during delivery
- **THEN** the handler returns the same uniform 200 it returns for an unknown
  account
- **AND** the failure is logged without the raw token

### Requirement: AuthEmailService builds the auth messages

The `AuthEmailService` SHALL build the password-reset and magic-link
`EmailMessage`s and delegate delivery to `IEmailSender`. It SHALL build absolute
URLs from `Auth:Email:BaseUrl`: a reset-password URL for password-reset mail and
a magic-link URL for magic-link mail. The magic-link URL SHALL carry the
delivered link token only; it does not carry the MFA or sudo challenge
identifier. `AuthEmailService` SHALL validate that `Auth:Email:BaseUrl` is an
absolute http(s) URL with a host; because it is registered only when a sender is
present, an invalid or missing base URL SHALL fail fast at startup. It SHALL NOT
log the token.

#### Scenario: Password-reset message carries the absolute reset URL

- **WHEN** `SendPasswordResetAsync` is called with a recipient and a reset token
- **THEN** an `EmailMessage` is sent whose `To` is the recipient
- **AND** whose body contains an absolute reset-password URL carrying the token

#### Scenario: Magic-link message carries the absolute magic-link URL

- **WHEN** `SendMagicLinkAsync` is called with a recipient and a link token
- **THEN** an `EmailMessage` is sent whose body contains an absolute magic-link
  URL carrying the link token only

#### Scenario: Invalid auth base URL fails fast

- **WHEN** an email transport is configured and `Auth:Email:BaseUrl` is missing,
  blank, or not an absolute http(s) URL with a host
- **THEN** the application throws at startup

### Requirement: SMTP sender delivers an EmailMessage

The `SmtpEmailSender` SHALL send an `EmailMessage` to the recipient via the
configured SMTP server, using the configured from-address and from-name. It SHALL
send a plain-text body when `HtmlBody` is null, and a multipart/alternative
carrying both the text and HTML bodies when `HtmlBody` is set. `UseStartTls` SHALL
default to true; it SHALL connect using STARTTLS when `Email:Smtp:UseStartTls` is
true and SHALL connect over an unencrypted connection (`SecureSocketOptions.None`)
only when it is explicitly false. It SHALL authenticate only when a username is
configured. It SHALL open a fresh SMTP client per send, honor the passed
`CancellationToken`, and apply the configured `Email:Smtp:TimeoutSeconds`.

#### Scenario: Plain-text message is delivered

- **WHEN** an `EmailMessage` with a null `HtmlBody` is sent and the SMTP server
  accepts it
- **THEN** one plain-text message is delivered to the recipient

#### Scenario: HTML message is delivered as multipart

- **WHEN** an `EmailMessage` with a non-null `HtmlBody` is sent
- **THEN** the delivered message is a multipart/alternative carrying both the
  text and HTML bodies

#### Scenario: Authentication is skipped when no username is configured

- **WHEN** `Email:Smtp:Username` is empty
- **THEN** the sender connects and sends without attempting SMTP authentication

#### Scenario: Unencrypted connection when STARTTLS is disabled

- **WHEN** `Email:Smtp:UseStartTls` is false
- **THEN** the sender connects with `SecureSocketOptions.None` (the path a local
  Mailpit on port 1025 accepts) and delivers the message

### Requirement: Email transport stays web-scoped and EE-free

The generic email seam (`IEmailSender` and `EmailMessage`) SHALL live in
`Freeboard.Core` and SHALL pull no web or MailKit dependency. The concrete
senders, their options, and `AuthEmailService` SHALL live in the web project
(`src/Freeboard/Email/` and `src/Freeboard/Auth/`). No email transport code SHALL
be added to `Freeboard.Enterprise`, `Freeboard.Agent`, or `Freeboard.CLI`, and the
MailKit dependency SHALL be referenced only by the web project.

#### Scenario: Seam in Core, transports in the web project

- **WHEN** the seam and the concrete senders are added
- **THEN** `IEmailSender` and `EmailMessage` reside in `Freeboard.Core` with no
  MailKit dependency
- **AND** the concrete senders, options, and `AuthEmailService` reside in the web
  project
- **AND** `Freeboard.Enterprise`, `Freeboard.Agent`, and `Freeboard.CLI` gain no
  email or MailKit dependency

