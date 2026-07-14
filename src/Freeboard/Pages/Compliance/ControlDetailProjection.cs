using Freeboard.Compliance;
using Freeboard.Pages.Shared;
using Freeboard.TagHelpers;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Projects one Statement of Applicability control into the shared <see cref="ObjectDetailView"/> anatomy.
/// Both the list page's inline drawer templates and the full-page detail call this one helper, so the
/// drawer and the full page cannot diverge (O4). Facets the drill-down projection does not carry render
/// as explicit O2 empties.
/// </summary>
public static class ControlDetailProjection
{
    /// <summary>
    /// Maps a control shown under a requirement for an organisation into the anatomy. Relations carry the
    /// requirement the control satisfies and its proving checks, both inside the single relations facet to
    /// keep O3's fixed section order. Per-check evaluated statuses come only from
    /// <paramref name="collectorStatus"/> (the per-collector evidence-status read), never synthesised. The
    /// control-level status is left null: the drill-down projection carries no evaluated control result and
    /// <see cref="SoaControlNode.Evaluation"/> is the check-combine rule, not a pass/fail - so the status
    /// facet renders "Not evaluated" rather than a fabricated pass (S6).
    /// </summary>
    public static ObjectDetailView Map(
        string organisationId,
        SoaRequirementNode requirement,
        SoaControlNode control,
        Func<string, string, string, string> collectorStatus)
    {
        var satisfies = new List<ObjectDetailRow>
        {
            new($"{requirement.Id} {requirement.Title}", Tag: MarkTone.Brand),
        };

        var checks = new List<ObjectDetailRow>(control.Checks.Count);
        foreach (var check in control.Checks)
        {
            if (check.Kind == SoaCheckKind.Collector)
            {
                var status = collectorStatus(organisationId, requirement.Id, check.Id);
                checks.Add(new ObjectDetailRow(check.Title, Status: MapCollectorStatus(status), Note: NoteFor(status)));
            }
            else
            {
                checks.Add(new ObjectDetailRow(check.Title, Note: "Attestation"));
            }
        }

        var relations = new List<ObjectDetailSection>
        {
            new("Satisfies", satisfies, AsTags: true),
            new("Proving checks", checks),
        };

        return new ObjectDetailView(
            Eyebrow: control.Id,
            Title: control.Title,
            Status: null,
            Assertion: null,
            Relations: relations,
            Evidence: [],
            Guidance: null,
            History: []);
    }

    // Stale degrades every dependent check to the warn (Drifting) seal rather than a muted note, so stale
    // data never reads as passing and the degraded state is visible (S6); its NoteFor names the stale
    // state. Unknown alone stays sealless and speaks through NoteFor. Red (Failing) stays reserved for a
    // hard failure.
    private static StatusKind? MapCollectorStatus(string status) => status switch
    {
        "Passing" => StatusKind.Passing,
        "HardFailure" => StatusKind.Failing,
        "SoftFailure" => StatusKind.Drifting,
        "Stale" => StatusKind.Drifting,
        _ => null,
    };

    private static string? NoteFor(string status) => status switch
    {
        "Stale" => "Collection stopped",
        "Unknown" => "Not collected",
        _ => null,
    };
}
