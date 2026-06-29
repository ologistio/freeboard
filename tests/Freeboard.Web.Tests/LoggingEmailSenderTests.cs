using Freeboard.Core.Email;
using Freeboard.Email;
using Microsoft.Extensions.Logging;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the non-delivering log sink: it logs the recipient and subject, does not throw,
/// and never puts the body (which carries the token) into any captured entry at information level
/// or above.
/// </summary>
public sealed class LoggingEmailSenderTests
{
    [Fact]
    public async Task LogsRecipientAndSubjectWithoutBody()
    {
        var capture = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(capture);

        var body = "Reset link: https://freeboard.example/reset-password?token=secret-reset-token";
        await sender.SendAsync(new EmailMessage("user@example.com", "Reset your Freeboard password", body));

        var entry = Assert.Single(capture.Entries);
        Assert.True(entry.Level >= LogLevel.Information);
        Assert.Contains("user@example.com", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Reset your Freeboard password", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-reset-token", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotThrow()
    {
        var sender = new LoggingEmailSender(new CapturingLogger<LoggingEmailSender>());
        await sender.SendAsync(new EmailMessage("user@example.com", "Your Freeboard sign-in link", "link: x"));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
