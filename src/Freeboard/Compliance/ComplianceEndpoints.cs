using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// Read-only HTTP endpoints serving the persisted compliance domain through
/// <see cref="IComplianceStore"/>. GET-only, so the read-only middleware does not
/// touch them. On an unreachable store the read endpoints return RFC 7807 / HTTP 503;
/// the status endpoint degrades to all-null counts with HTTP 200.
/// </summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/standards", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetStandardsAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        app.MapGet("/api/controls", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetControlsAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, maps_to = r.MapsTo }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        app.MapGet("/api/scopes", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetScopesAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, controls = r.Controls }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        app.MapGet("/api/compliance/status", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var counts = await store.GetCountsAsync(ct);
                return Results.Ok(new
                {
                    persisted = new
                    {
                        standards = (int?)counts.Standards,
                        controls = (int?)counts.Controls,
                        scopes = (int?)counts.Scopes,
                    },
                });
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                // Degrade to all-null counts; the app stays up.
                return Results.Ok(new
                {
                    persisted = new { standards = (int?)null, controls = (int?)null, scopes = (int?)null },
                });
            }
        });
    }

    private static IResult Unreachable() => Results.Problem(
        title: "Compliance store unreachable",
        detail: "The compliance store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException
            or InvalidOperationException
            or TimeoutException;
}
