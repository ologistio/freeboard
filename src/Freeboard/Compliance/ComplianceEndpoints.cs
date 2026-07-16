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
                return Results.Ok(rows.Select(r => new { id = r.Id, title = r.Title, maps_to = r.MapsTo, evaluation = r.Evaluation }));
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

        // A vendor is visible only when its owner (a Company/Department asset) is in the caller's
        // accessible-org set; a vendor with a null or dangling owner is visible to no one (fail-closed).
        // vendor-scopes narrow the same way, so a hidden vendor leaks neither its id nor its Out exception
        // justifications. This narrowing covers /vendors and /vendor-scopes only; /evidence-collectors and
        // /integration-connections still expose a hidden vendor's id.
        reads.MapGet("/vendors", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var accessible = await access.AccessibleOrgIdsAsync(user, await store.GetOrganisationsAsync(ct), ct);
                var rows = await store.GetVendorsAsync(ct);
                return Results.Ok(rows.Where(r => r.Owner is not null && accessible.Contains(r.Owner))
                    .Select(r => new { id = r.Id, title = r.Title }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        reads.MapGet("/vendor-scopes", async (IComplianceStore store, IOrgAccess access, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                var accessible = await access.AccessibleOrgIdsAsync(user, await store.GetOrganisationsAsync(ct), ct);
                var visibleVendors = (await store.GetVendorsAsync(ct))
                    .Where(v => v.Owner is not null && accessible.Contains(v.Owner))
                    .Select(v => v.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var rows = await store.GetVendorScopesAsync(ct);
                return Results.Ok(rows.Where(r => visibleVendors.Contains(r.Vendor)).Select(r => new
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

        // Evidence-collectors are org-independent reference data (no organisation dimension), so - like
        // /vendors - they are intentionally NOT narrowed by IOrgAccess: any authenticated user reads
        // every collector, including its config map. Revisit if a later change adds an org dimension.
        reads.MapGet("/evidence-collectors", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetEvidenceCollectorsAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    control = r.Control,
                    vendor = r.Vendor,
                    type = r.Type,
                    frequency = r.Frequency,
                    threshold = r.Threshold,
                    config = r.Config,
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Attestation-templates are org-independent reference data (no organisation dimension), so - like
        // /vendors and /evidence-collectors - they are intentionally NOT narrowed by IOrgAccess: any
        // authenticated user reads every template. The quiz items carry no answer: the store returns an
        // answer-free QuizItemView, so the correct answer never appears in the JSON.
        reads.MapGet("/attestation-templates", async (IComplianceStore store, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetAttestationTemplatesAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    title = r.Title,
                    control = r.Control,
                    type = r.Type,
                    body = r.Body,
                    fields = r.Fields.Select(f => new { id = f.Id, label = f.Label, type = f.Type, options = f.Options }),
                    pass_mark = r.PassMark,
                    quiz = r.Quiz.Select(q => new { id = q.Id, prompt = q.Prompt, options = q.Options }),
                }));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                return Unreachable();
            }
        });

        // Integration-connections are org-independent reference data (no organisation dimension), so - like
        // /vendors and /evidence-collectors - they are intentionally NOT narrowed by IOrgAccess. token_resolvable
        // is composed at read time from the out-of-band token resolver; the token value never appears here.
        reads.MapGet("/integration-connections", async (IComplianceStore store, IIntegrationTokenResolver tokens, CancellationToken ct) =>
        {
            try
            {
                var rows = await store.GetIntegrationConnectionsAsync(ct);
                return Results.Ok(rows.Select(r => new
                {
                    id = r.Id,
                    provider = r.Provider,
                    base_url = r.BaseUrl,
                    discovery_cadence = r.DiscoveryCadence,
                    vendor = r.Vendor,
                    token_resolvable = tokens.IsResolvable(r.Id),
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
                    // Confirm the standard exists before projecting. Under opt-out an absent standard
                    // would otherwise resolve every organisation In by default, presenting a typo or
                    // deleted standard as applicable to all orgs instead of returning not found.
                    var standards = await store.GetStandardsAsync(ct);
                    if (!standards.Any(s => string.Equals(s.Id, standardId, StringComparison.Ordinal)))
                    {
                        return Results.NotFound();
                    }

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
                        evidenceCollectors = (int?)counts.EvidenceCollectors,
                        attestationTemplates = (int?)counts.AttestationTemplates,
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
                        evidenceCollectors = (int?)null,
                        attestationTemplates = (int?)null,
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
