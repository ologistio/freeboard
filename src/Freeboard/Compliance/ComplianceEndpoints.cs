using Freeboard.Api;
using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// Read-only HTTP endpoints serving the persisted compliance domain through
/// <see cref="IComplianceStore"/>. GET-only, so the read-only middleware does not
/// touch them, and behind the default authorization policy so an anonymous caller is 401'd
/// (any authenticated user may read; no admin role required). On an unreachable store the read
/// endpoints return RFC 7807 / HTTP 503; the status endpoint degrades to all-null counts with
/// HTTP 200.
/// </summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();

        reads.MapGet("/standards", async (IComplianceStore store, CancellationToken ct) =>
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

        reads.MapGet("/controls", async (IComplianceStore store, CancellationToken ct) =>
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

        reads.MapGet("/organisations", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetOrganisationsAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, kind = r.Kind, parent = r.Parent }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/scopes", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetScopesAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    organisation = r.Organisation,
                    standard = r.Standard,
                    disposition = r.Disposition,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/statement-of-applicability/{standardId}",
            async (string standardId, IComplianceStore store, CancellationToken ct) =>
            {
                try
                {
                    var organisations = await store.GetOrganisationsAsync(ct);
                    var scopes = await store.GetScopesAsync(ct);
                    var nodes = StatementOfApplicability.Resolve(organisations, scopes, standardId);
                    return Results.Ok(new
                    {
                        standard = standardId,
                        nodes = nodes.Select(n => new
                        {
                            id = n.Id,
                            title = n.Title,
                            kind = n.Kind,
                            parent = n.Parent,
                            disposition = n.Disposition,
                            resolution = n.Resolution.ToWireValue(),
                        }),
                    });
                }
                catch (Exception ex) when (IsStoreFailure(ex))
                {
                    return Unreachable();
                }
            });

        reads.MapGet("/compliance/status", async (IComplianceStore store, CancellationToken ct) =>
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
                        organisations = (int?)counts.Organisations,
                        scopes = (int?)counts.Scopes,
                    },
                });
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                // Degrade to all-null counts; the app stays up.
                return Results.Ok(new
                {
                    persisted = new
                    {
                        standards = (int?)null,
                        controls = (int?)null,
                        organisations = (int?)null,
                        scopes = (int?)null,
                    },
                });
            }
        });
    }

    private static IResult Unreachable() => Results.Problem(
        title: "Compliance store unreachable",
        detail: "The compliance store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException
            or InvalidOperationException
            or TimeoutException;
}
