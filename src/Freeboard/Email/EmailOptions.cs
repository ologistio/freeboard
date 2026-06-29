namespace Freeboard.Email;

/// <summary>The email transport selected by config.</summary>
public enum EmailTransport
{
    /// <summary>No sender registered; email delivery is off (default).</summary>
    None = 0,

    /// <summary>A non-delivering developer sink that logs the recipient and subject only.</summary>
    Log = 1,

    /// <summary>A real SMTP transport (MailKit).</summary>
    Smtp = 2,
}

/// <summary>
/// Email options bound from the <c>Email</c> section. Selects the transport and carries the
/// from-identity used on every message. The concrete sender consumes a raw bound instance (not
/// <c>IOptions</c>), matching the <c>AuthCryptoOptions</c> concrete-singleton pattern. Auth-link
/// concerns (the base URL for reset/magic-link URLs) live with auth, not here.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>The transport to register: <c>none</c> (default), <c>log</c>, or <c>smtp</c>.</summary>
    public EmailTransport Transport { get; set; } = EmailTransport.None;

    /// <summary>The From address used on every email.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>The display name paired with <see cref="FromAddress"/>.</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>SMTP settings, used only when <see cref="Transport"/> is <c>smtp</c>.</summary>
    public EmailSmtpOptions Smtp { get; set; } = new();
}

/// <summary>SMTP connection settings for the <c>smtp</c> transport, bound from <c>Email:Smtp</c>.</summary>
public sealed class EmailSmtpOptions
{
    /// <summary>The SMTP server host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The SMTP server port. Default 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// When true (default), connect with STARTTLS so tokens are never sent in the clear. An
    /// operator must explicitly set this false to send over an unencrypted connection (e.g. a
    /// local Mailpit on 1025).
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>The SMTP username. When empty, the sender skips authentication.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The SMTP password. A secret: supply it via env / user-secrets / a config provider and
    /// never commit it.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>The connect/send timeout in seconds. Bounds a hung server. Default 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
