using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>A domain row to upsert. Keyed on <see cref="Id"/>; never matched on title.</summary>
public sealed record DomainRow(string Id, string ApiVersion, string Title);

/// <summary>A control -> standard cross-ref row.</summary>
public sealed record ControlStandardRow(string ControlId, string StandardId);

/// <summary>A scope -> control cross-ref row.</summary>
public sealed record ScopeControlRow(string ScopeId, string ControlId);

/// <summary>
/// The flattened, id-keyed shape derived from a validated <see cref="GitOpsConfig"/>.
/// Pure (no database), so the mapping is unit testable without MySQL.
/// </summary>
public sealed class ImportPlan
{
    public IReadOnlyList<DomainRow> Standards { get; }

    public IReadOnlyList<DomainRow> Controls { get; }

    public IReadOnlyList<DomainRow> Scopes { get; }

    public IReadOnlyList<ControlStandardRow> ControlStandards { get; }

    public IReadOnlyList<ScopeControlRow> ScopeControls { get; }

    private ImportPlan(GitOpsConfig config)
    {
        Standards = config.Standards
            .Select(s => new DomainRow(s.Id, s.ApiVersion, s.Title))
            .ToList();
        Controls = config.Controls
            .Select(c => new DomainRow(c.Id, c.ApiVersion, c.Title))
            .ToList();
        Scopes = config.Scopes
            .Select(s => new DomainRow(s.Id, s.ApiVersion, s.Title))
            .ToList();

        // Distinct guards the composite-PK join tables against duplicate ids within a
        // single maps_to/controls list even if a caller skips Core validation. Record
        // equality is ordinal on the id strings, consistent with id identity.
        ControlStandards = config.Controls
            .SelectMany(c => c.MapsTo.Select(standardId => new ControlStandardRow(c.Id, standardId)))
            .Distinct()
            .ToList();
        ScopeControls = config.Scopes
            .SelectMany(s => s.Controls.Select(controlId => new ScopeControlRow(s.Id, controlId)))
            .Distinct()
            .ToList();
    }

    public static ImportPlan From(GitOpsConfig config) => new(config);

    public IReadOnlyList<string> StandardIds => Standards.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ControlIds => Controls.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ScopeIds => Scopes.Select(r => r.Id).ToList();
}
