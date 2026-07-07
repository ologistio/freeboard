namespace Freeboard.CLI;

/// <summary>
/// The <c>collector</c> command group. Reads the evidence-collector register through the Freeboard HTTP
/// API ONLY - it never touches the database. Base URL via <c>--api-url</c>/<c>FREEBOARD_API_URL</c>;
/// admin token via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>. Exit codes follow the CLI convention: 0
/// success, 1 input/validation, 3 operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class CollectorCommands
{
    /// <summary>List controls with their evaluation rule and attached evidence-collectors.</summary>
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

            var collectorsResult = await client.ListEvidenceCollectorsAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(collectorsResult, collectors => Print(controlsResult.Payload!, collectors));
        });
    }

    private static void Print(IReadOnlyList<ApiControl> controls, IReadOnlyList<ApiEvidenceCollector> collectors)
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
                var threshold = collector.Threshold is int t ? $"{t}%" : "-";
                Console.WriteLine(
                    $"    {collector.Id}  {collector.Title}  {collector.Type}  vendor {vendor}  {collector.Frequency}  threshold {threshold}");
                foreach (var entry in collector.Config)
                {
                    Console.WriteLine($"        {entry.Key}: {entry.Value}");
                }
            }
        }
    }
}
