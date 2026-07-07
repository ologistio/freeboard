using System.Security.Claims;
using Freeboard.Api;
using Freeboard.Persistence;
using Freeboard.Web;

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
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    version = r.Version,
                    authority = r.Authority,
                    publisher = r.Publisher,
                    source_url = r.SourceUrl,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/requirements", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetRequirementsAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    standard = r.Standard,
                    theme = r.Theme,
                    statement = r.Statement,
                    guidance = r.Guidance,
                    citation = new { label = r.CitationLabel, url = r.CitationUrl },
                }));
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

        reads.MapGet("/organisations", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetOrganisationsAsync(ct);
                var accessible = await access.AccessibleOrgIdsAsync(user, rows, ct);
                // Narrow to the accessible set, and null a parent id the caller cannot access so an
                // inaccessible organisation's existence is not disclosed (the selector treats a node
                // with a null parent as a root).
                return Results.Ok(rows.Where(r => accessible.Contains(r.Id)).Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    kind = r.Kind,
                    parent = r.Parent is not null && accessible.Contains(r.Parent) ? r.Parent : null,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/scopes", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetScopesAsync(ct);
                var accessible = await access.AccessibleOrgIdsAsync(user, await store.GetOrganisationsAsync(ct), ct);
                return Results.Ok(rows.Where(r => accessible.Contains(r.Organisation)).Select(r => new
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

        reads.MapGet("/requirement-scopes", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetRequirementScopesAsync(ct);
                var accessible = await access.AccessibleOrgIdsAsync(user, await store.GetOrganisationsAsync(ct), ct);
                return Results.Ok(rows.Where(r => accessible.Contains(r.Organisation)).Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    organisation = r.Organisation,
                    requirement = r.Requirement,
                    disposition = r.Disposition,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Vendors and vendor-scopes are org-independent reference data (the flat model, no
        // organisation dimension), so - unlike /organisations, /scopes, /requirement-scopes - they are
        // intentionally NOT narrowed by IOrgAccess: any authenticated user reads every vendor and every
        // exception justification. Revisit if a later change adds an organisation dimension.
        reads.MapGet("/vendors", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetVendorsAsync(ct);
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/vendor-scopes", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetVendorScopesAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    vendor = r.Vendor,
                    requirement = r.Requirement,
                    control = r.Control,
                    disposition = r.Disposition,
                    justification = r.Justification,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/statement-of-applicability/{standardId}",
            async (string standardId, IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
            {
                try
                {
                    var inputs = await store.GetStatementOfApplicabilityInputsAsync(ct);
                    // Resolve over the FULL tree first (so inherited dispositions survive), then filter
                    // the node list to the accessible subtree.
                    var accessible = await access.AccessibleOrgIdsAsync(user, inputs.Organisations, ct);
                    var nodes = StatementOfApplicability.Resolve(
                            inputs.Organisations, inputs.Scopes, inputs.Requirements, inputs.RequirementScopes, standardId)
                        .Where(n => accessible.Contains(n.Id))
                        .ToList();
                    return Results.Ok(new
                    {
                        standard = standardId,
                        nodes = nodes.Select(n => new
                        {
                            id = n.Id,
                            title = n.Title,
                            kind = n.Kind,
                            parent = n.Parent is not null && accessible.Contains(n.Parent) ? n.Parent : null,
                            disposition = n.Disposition,
                            resolution = n.Resolution.ToWireValue(),
                            requirements = n.Requirements.Select(r => new
                            {
                                requirement = r.Requirement,
                                disposition = r.Disposition,
                                resolution = r.Resolution.ToWireValue(),
                            }),
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
                        requirements = (int?)counts.Requirements,
                        organisations = (int?)counts.Organisations,
                        scopes = (int?)counts.Scopes,
                        requirementScopes = (int?)counts.RequirementScopes,
                        vendors = (int?)counts.Vendors,
                        vendorScopes = (int?)counts.VendorScopes,
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
                        requirements = (int?)null,
                        organisations = (int?)null,
                        scopes = (int?)null,
                        requirementScopes = (int?)null,
                        vendors = (int?)null,
                        vendorScopes = (int?)null,
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
