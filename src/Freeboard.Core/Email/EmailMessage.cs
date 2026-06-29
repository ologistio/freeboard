namespace Freeboard.Core.Email;

/// <summary>
/// A single outbound email a transport delivers. Plain text is the only form sent today;
/// <see cref="HtmlBody"/> is the additive onramp to HTML later. The body may carry a credential
/// (a reset or magic-link token) and must never be logged.
/// </summary>
/// <param name="To">The recipient address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="TextBody">The plain-text body, always set.</param>
/// <param name="HtmlBody">
/// The optional HTML body. When null (the default) the message is text-only; when set, a transport
/// sends both parts as a multipart/alternative.
/// </param>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);
