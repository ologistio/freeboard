namespace Freeboard.CLI;

/// <summary>
/// The <c>attestation-template</c> command group. Reads the attestation-template register through the
/// Freeboard HTTP API ONLY - it never touches the database. Base URL via
/// <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; admin token via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>.
/// Exit codes follow the CLI convention: 0 success, 1 input/validation, 3 operational/HTTP failure
/// (401/403/5xx/connection refused).
/// </summary>
public sealed class AttestationTemplateCommands
{
    /// <summary>List controls with the attestation templates attached to each.</summary>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int List(string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            // A template carries its control id, so - unlike the collector command - no /controls call is
            // needed; group the templates by control id directly.
            var result = await client.ListAttestationTemplatesAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(result, Print);
        });
    }

    private static void Print(IReadOnlyList<ApiAttestationTemplate> templates)
    {
        var byControl = templates
            .GroupBy(t => t.Control, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byControl)
        {
            Console.WriteLine($"{group.Key}");
            foreach (var template in group)
            {
                var body = string.IsNullOrEmpty(template.Body) ? "no body" : "has body";
                Console.WriteLine($"    {template.Id}  {template.Title}  [{template.Type}]  ({body})");
                foreach (var field in template.Fields)
                {
                    var options = field.Options.Count == 0 ? string.Empty : $" ({string.Join(", ", field.Options)})";
                    Console.WriteLine($"        field {field.Id}  {field.Label}  [{field.Type}]{options}");
                }

                if (template.PassMark is int passMark)
                {
                    Console.WriteLine($"        pass mark: {passMark}%");
                }

                foreach (var item in template.Quiz)
                {
                    Console.WriteLine($"        quiz {item.Id}  {item.Prompt}  ({string.Join(", ", item.Options)})");
                }
            }
        }
    }
}
