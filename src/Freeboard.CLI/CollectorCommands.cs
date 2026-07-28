using ConsoleAppFramework;

namespace Freeboard.CLI;

/// <summary>
/// The <c>collector</c> command group. Reads the collector register through the Freeboard HTTP
/// API ONLY - it never touches the database. Base URL via <c>--api-url</c>/<c>FREEBOARD_API_URL</c>;
/// admin token via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>. Exit codes follow the CLI convention: 0
/// success, 1 input/validation, 3 operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class CollectorCommands
{
    /// <summary>List controls with their evaluation rule and attached collectors.</summary>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int List(string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            var controlsResult = await client.ListControlsAsync(ct).ConfigureAwait(false);
            if (controlsResult.Outcome != ApiOutcome.Success)
            {
                // A failed controls read is operational/auth; surface it without a second call.
                return ApiCommandRunner.Translate(controlsResult, _ => { });
            }

            var collectorsResult = await client.ListCollectorsAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(collectorsResult, collectors => Print(controlsResult.Payload!, collectors));
        });
    }

    /// <summary>Issue a machine credential for a collector. Prints the raw token once.</summary>
    /// <param name="collectorId">The collector id to issue a credential for.</param>
    /// <param name="expiresAt">Optional ISO 8601 expiry (e.g. 2027-01-01T00:00:00Z). Omit for no expiry.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    [Command("credential issue")]
    public int CredentialIssue(
        [Argument] string collectorId, string? expiresAt = null, string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            var result = await client.IssueCollectorCredentialAsync(collectorId, expiresAt, ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(result, issued =>
            {
                // The raw token is shown exactly once; the server stores only its keyed HMAC.
                Console.WriteLine(issued.Token);
                Console.Error.WriteLine(
                    $"Issued credential {issued.CredentialId} for collector {issued.CollectorId}"
                    + (issued.ExpiresAt is { Length: > 0 } e ? $" (expires {e})." : "."));
            });
        });
    }

    /// <summary>Revoke a machine credential for a collector.</summary>
    /// <param name="collectorId">The collector id that owns the credential.</param>
    /// <param name="credentialId">The credential id to revoke.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    [Command("credential revoke")]
    public int CredentialRevoke(
        [Argument] string collectorId, [Argument] string credentialId, string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            var result = await client.RevokeCollectorCredentialAsync(collectorId, credentialId, ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(
                result, _ => Console.Error.WriteLine($"Revoked credential {credentialId} for collector {collectorId}."));
        });
    }

    private static void Print(IReadOnlyList<ApiControl> controls, IReadOnlyList<ApiCollector> collectors)
    {
        var collectorsByControl = collectors
            .GroupBy(c => c.Control, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var control in controls)
        {
            var evaluation = string.IsNullOrWhiteSpace(control.Evaluation) ? "-" : control.Evaluation;
            Console.WriteLine($"{control.Id}  {control.Title}  [evaluation: {evaluation}]");
            if (!collectorsByControl.TryGetValue(control.Id, out var attached))
            {
                continue;
            }

            foreach (var collector in attached)
            {
                var vendor = string.IsNullOrEmpty(collector.Vendor) ? "-" : collector.Vendor;
                var provider = string.IsNullOrEmpty(collector.Provider) ? "-" : collector.Provider;
                var threshold = collector.Threshold is int t ? $"{t}%" : "-";
                Console.WriteLine(
                    $"    {collector.Id}  {collector.Title}  {collector.Type}  provider {provider}  vendor {vendor}  "
                    + $"{collector.Frequency}  threshold {threshold}");
                PrintConfig(collector.Type, collector.Config);
            }
        }
    }

    // Branch on the TYPE, not on which members happen to be populated: a script or agent collector
    // registers no config key at all, so it must print nothing rather than fall into the attestation
    // branch and report "no body". The body is reported as a has/no indicator rather than printed - the
    // markdown is for the register page to render, and the CLI only needs to say whether one is
    // authored. The quiz answer is never received, so it can never be printed.
    private static void PrintConfig(string type, ApiCollectorConfig config)
    {
        if (string.Equals(type, "integration", StringComparison.Ordinal))
        {
            foreach (var check in config.Checks)
            {
                Console.WriteLine($"        check {check.Name}  [{check.Severity}]");
            }

            return;
        }

        if (type is not ("manual" or "training"))
        {
            return;
        }

        Console.WriteLine($"        {(string.IsNullOrEmpty(config.Body) ? "no body" : "has body")}");
        foreach (var field in config.Fields)
        {
            var options = field.Options.Count == 0 ? string.Empty : $" ({string.Join(", ", field.Options)})";
            Console.WriteLine($"        field {field.Id}  {field.Label}  [{field.Type}]{options}");
        }

        if (config.PassMark is int passMark)
        {
            Console.WriteLine($"        pass mark: {passMark}%");
        }

        foreach (var item in config.Quiz)
        {
            Console.WriteLine($"        quiz {item.Id}  {item.Prompt}  ({string.Join(", ", item.Options)})");
        }
    }
}
