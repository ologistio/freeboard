using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Auth;
using Freeboard.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Freeboard.Web.Tests;

/// <summary>
/// SMTP delivery integration test against a local Mailpit, driving AuthEmailService through
/// SmtpEmailSender so both the auth message building and the SMTP transport are exercised. Gated on
/// FREEBOARD_TEST_SMTP and skips cleanly when unset, exactly like the MySQL tests, so the default
/// test run needs no container. The env var encodes both targets in one connection-string-shaped
/// value: <c>Smtp=127.0.0.1:1025;Http=http://127.0.0.1:8025</c> - the SMTP host:port to send
/// through and the Mailpit HTTP API base for the messages API.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private const string EnvVar = "FREEBOARD_TEST_SMTP";

    [SkippableFact]
    public async Task SendsBothAuthMailKindsToMailpit()
    {
        var config = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(config), $"{EnvVar} not set; SMTP integration test skipped.");

        var (smtpHost, smtpPort, httpBase) = ParseConfig(config!);
        using var http = new HttpClient();

        // Start from a clean mailbox so the per-kind assertions see only this run's messages.
        await http.DeleteAsync($"{httpBase}/api/v1/messages");

        var options = new EmailOptions
        {
            Transport = EmailTransport.Smtp,
            FromAddress = "noreply@freeboard.test",
            FromName = "Freeboard",
            Smtp = new EmailSmtpOptions
            {
                Host = smtpHost,
                Port = smtpPort,
                UseStartTls = false, // Mailpit on 1025 accepts an unencrypted connection.
            },
        };
        var sender = new SmtpEmailSender(options, NullLogger<SmtpEmailSender>.Instance);
        var auth = new AuthEmailService(sender, "https://freeboard.example");

        await auth.SendPasswordResetAsync("reset-user@example.com", "reset-token-123");
        await auth.SendMagicLinkAsync("magic-user@example.com", "magic-token-456");

        var resetBody = await PollForMessageBodyAsync(http, httpBase, "reset-user@example.com");
        Assert.Contains("https://freeboard.example/reset-password?token=reset-token-123", resetBody, StringComparison.Ordinal);

        var magicBody = await PollForMessageBodyAsync(http, httpBase, "magic-user@example.com");
        Assert.Contains("https://freeboard.example/auth/magic-link?token=magic-token-456", magicBody, StringComparison.Ordinal);
    }

    private static (string Host, int Port, string HttpBase) ParseConfig(string raw)
    {
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? smtp = null;
        string? httpBase = null;
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (string.Equals(key, "Smtp", StringComparison.OrdinalIgnoreCase))
            {
                smtp = value;
            }
            else if (string.Equals(key, "Http", StringComparison.OrdinalIgnoreCase))
            {
                httpBase = value;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(smtp), $"{EnvVar} must contain Smtp=host:port.");
        Assert.False(string.IsNullOrWhiteSpace(httpBase), $"{EnvVar} must contain Http=base-url.");
        var hostPort = smtp!.Split(':', 2);
        return (hostPort[0], int.Parse(hostPort[1]), httpBase!.TrimEnd('/'));
    }

    /// <summary>
    /// Polls the Mailpit messages API until at least one message addressed to <paramref name="to"/>
    /// is present, asserts that exactly one is (a duplicate or stray send fails the test), then
    /// returns its body text. Mailpit delivers asynchronously, so a short poll is needed rather than
    /// a single read.
    /// </summary>
    private static async Task<string> PollForMessageBodyAsync(HttpClient http, string httpBase, string to)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var list = await http.GetFromJsonAsync<JsonElement>($"{httpBase}/api/v1/messages");
            var matches = list.GetProperty("messages").EnumerateArray().Where(m =>
                m.GetProperty("To").EnumerateArray().Any(t =>
                    string.Equals(t.GetProperty("Address").GetString(), to, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count > 0)
            {
                var match = Assert.Single(matches);
                var id = match.GetProperty("ID").GetString();
                var detail = await http.GetFromJsonAsync<JsonElement>($"{httpBase}/api/v1/message/{id}");
                return detail.GetProperty("Text").GetString() ?? string.Empty;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"No Mailpit message addressed to {to} arrived in time.");
        return string.Empty;
    }
}
