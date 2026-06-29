## 1. Generic seam in Core

- [x] 1.1 Add `IEmailSender` in `src/Freeboard.Core/Email/IEmailSender.cs` with a
      single `SendAsync(EmailMessage, ct)` method.
- [x] 1.2 Add `EmailMessage` in `src/Freeboard.Core/Email/EmailMessage.cs`: `To`,
      `Subject`, `TextBody`, and `HtmlBody` (string?, default null). XML-doc that a
      null `HtmlBody` means text-only. Core pulls no web/MailKit dependency.

## 2. Web transports and options

- [x] 2.1 Add `EmailOptions`/`EmailSmtpOptions` in
      `src/Freeboard/Email/EmailOptions.cs` bound from `Email`: `Transport`
      (none|log|smtp, default none), `FromAddress`, `FromName`, nested SMTP
      (Host, Port=587, UseStartTls=true, Username, Password, TimeoutSeconds=30).
      XML-doc that `Smtp:Password` is a secret. `SectionName = "Email"`.
- [x] 2.2 Add `LoggingEmailSender` implementing `IEmailSender`: log recipient +
      subject at Information, never the body; do not throw.
- [x] 2.3 Add `SmtpEmailSender` implementing `IEmailSender`: build the
      `MimeMessage` from the `EmailMessage`; plain text when `HtmlBody` is null,
      multipart/alternative (MailKit `BodyBuilder`) when set. Fresh `SmtpClient`
      per send, `Timeout = TimeoutSeconds*1000`,
      `ConnectAsync(Host, Port, UseStartTls?StartTls:None, ct)`,
      `AuthenticateAsync` only when a username is set, `SendAsync`,
      `DisconnectAsync(true)`. Never log the body. MailKit stays web-only.
- [x] 2.4 Add `EmailRegistration.Add(services, configuration)`: bind `Email`,
      switch on `Transport` (none registers nothing; log the sink; smtp via a
      factory closing over the bound options); unknown transport throws; smtp
      fail-fast on missing host / non-bare-addr-spec from-address. Return the bound
      options.

## 3. Auth message builder

- [x] 3.1 Add `AuthEmailService` in `src/Freeboard/Auth/AuthEmailService.cs`:
      `SendPasswordResetAsync` / `SendMagicLinkAsync` build absolute links from
      `Auth:Email:BaseUrl` (trailing slash trimmed, url-encoded token), build a
      plain-text `EmailMessage`, and call `IEmailSender.SendAsync`. Validate the
      base URL (absolute http/https with host) in the constructor so an invalid
      value fails fast at startup when the service is registered. Never log the
      token.

## 4. Remove the old seam and migrate consumers

- [x] 4.1 Remove `src/Freeboard.Persistence/Auth/IAuthEmailSender.cs`; Persistence
      keeps no email seam.
- [x] 4.2 Migrate `AuthEndpoints.cs` (forgot-password), `MfaLoginEndpoints.cs`,
      `SudoEndpoints.cs`, and `MfaFactorService.cs` to optionally resolve
      `AuthEmailService`; preserve the optional-presence semantics and the
      enumeration-safe try/catch.

## 5. Program.cs wiring

- [x] 5.1 Call `EmailRegistration.Add` before Build; register `AuthEmailService`
      as a singleton only when a sender is present (transport != none), resolving
      `IEmailSender` + `Auth:Email:BaseUrl`.
- [x] 5.2 Key the password-reset startup fail-fast on `AuthEmailService` presence.
- [x] 5.3 Emit an `ILogger` Warning at startup when `Email:Transport == log`.

## 6. Tests

- [x] 6.1 Replace the recording fake with `RecordingEmailSender : IEmailSender`
      and `ThrowingEmailSender : IEmailSender`; back the endpoint tests with a real
      `AuthEmailService` over a `RecordingEmailSender`; assert the recorded
      message `To` and the absolute URL in the body; assert forgot-password stays
      200 and never logs the token when the sender throws.
- [x] 6.2 `LoggingEmailSender` unit test: logs recipient + subject, does not throw,
      token never at Information+.
- [x] 6.3 `EmailRegistration` tests (none/log/smtp/unknown + smtp fail-fast) and
      `AuthEmailService` base-URL fail-fast + password-reset-with-transport-none
      startup throw.
- [x] 6.4 Gated Mailpit integration test (`FREEBOARD_TEST_SMTP`) driving
      `AuthEmailService` -> `SmtpEmailSender`; skip cleanly when unset.
- [x] 6.5 Architecture test: MailKit referenced only by the web project; Core
      MailKit-free.

## 7. Docs and verification

- [x] 7.1 Update `docs/authentication.md` to the `Email` config split (generic
      `Email:*` vs `Auth:Email:BaseUrl`) and the log-transport caveat/warning.
- [x] 7.2 Keep the Mailpit compose service and the `FREEBOARD_TEST_SMTP` gate in
      `CLAUDE.md`.
- [x] 7.3 `dotnet build` clean (0 warnings); `dotnet test` green with Mailpit and
      MySQL tests skipping.
