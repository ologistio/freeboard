namespace Freeboard.CLI;

/// <summary>
/// The <c>vendor</c> command group. Reads the vendor register through the Freeboard HTTP API ONLY -
/// it never touches the database. Base URL via <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; admin token
/// via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>. Exit codes follow the CLI convention: 0 success,
/// 1 input/validation, 3 operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class VendorCommands
{
    /// <summary>List vendors with their per-requirement/control exceptions and justifications.</summary>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int List(string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            var vendorsResult = await client.ListVendorsAsync(ct).ConfigureAwait(false);
            if (vendorsResult.Outcome != ApiOutcome.Success)
            {
                // A failed vendor read is operational/auth; surface it without a second call.
                return ApiCommandRunner.Translate(vendorsResult, _ => { });
            }

            var scopesResult = await client.ListVendorScopesAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(scopesResult, scopes => Print(vendorsResult.Payload!, scopes));
        });
    }

    private static void Print(IReadOnlyList<ApiVendor> vendors, IReadOnlyList<ApiVendorScope> scopes)
    {
        var scopesByVendor = scopes
            .GroupBy(s => s.Vendor, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var vendor in vendors)
        {
            Console.WriteLine($"{vendor.Id}  {vendor.Title}");
            if (!scopesByVendor.TryGetValue(vendor.Id, out var vendorScopes))
            {
                continue;
            }

            foreach (var scope in vendorScopes)
            {
                var target = scope.Requirement ?? scope.Control ?? "-";
                var kind = scope.Requirement is not null ? "requirement" : "control";
                // An Out exception is never printed without its justification.
                var reason = string.Equals(scope.Disposition, "Out", StringComparison.Ordinal)
                    ? $" - {scope.Justification}"
                    : string.Empty;
                Console.WriteLine($"    {scope.Disposition}  {kind} {target}{reason}");
            }
        }
    }
}
