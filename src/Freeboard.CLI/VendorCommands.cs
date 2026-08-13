namespace Freeboard.CLI;

/// <summary>
/// The <c>vendor</c> command group. Reads the vendor register through the Freeboard HTTP API ONLY -
/// it never touches the database. Base URL via <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; admin token
/// via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>. Exit codes follow the CLI convention: 0 success,
/// 1 input/validation, 3 operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class VendorCommands
{
    /// <summary>
    /// List vendors with their tier, data classes, certifications, and per-requirement/control exceptions
    /// and justifications. An absent tier or data class list prints <c>-</c>; a vendor with no
    /// certification prints no assurance line.
    /// </summary>
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

            var scopesResult = await client.ListScopesAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(scopesResult, scopes => Print(vendorsResult.Payload!, scopes));
        });
    }

    private static void Print(IReadOnlyList<ApiVendor> vendors, IReadOnlyList<ApiScope> scopes)
    {
        // Vendor exceptions are the unified scopes whose subject is a vendor; the endpoint has already
        // owner-narrowed them, so filtering by the visible vendor ids yields each vendor's rows.
        var scopesByVendor = scopes
            .GroupBy(s => s.Subject, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var vendor in vendors)
        {
            var tier = vendor.Tier ?? "-";
            var dataClasses = vendor.DataClasses.Count > 0 ? string.Join(",", vendor.DataClasses) : "-";
            Console.WriteLine($"{vendor.Id}  {vendor.Title}  {tier}  {dataClasses}");

            // Certifications print above the scope lines, as a per-vendor child list where absence is
            // silence. The status is the endpoint's, so the window that decides it lives in one process.
            foreach (var assurance in vendor.Assurances)
            {
                Console.WriteLine($"    {assurance.Standard}  {assurance.Expires}  {assurance.Status}");
            }

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
