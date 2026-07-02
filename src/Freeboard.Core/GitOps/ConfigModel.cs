namespace Freeboard.Core.GitOps;

/// <summary>
/// The only supported apiVersion for this increment.
/// </summary>
public static class GitOpsSchema
{
    public const string ApiVersion = "freeboard.io/v1alpha1";

    public const string KindStandard = "Standard";
    public const string KindRequirement = "Requirement";
    public const string KindControl = "Control";
    public const string KindOrganisation = "Organisation";
    public const string KindScope = "Scope";
}

/// <summary>
/// A compliance standard in scope. Identity is <see cref="Id"/>; <see cref="Title"/> is display only.
/// <see cref="Version"/> and <see cref="Authority"/> are required; <see cref="Publisher"/> and
/// <see cref="SourceUrl"/> are optional (blank means absent).
/// </summary>
public sealed record Standard
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>The scheme version, e.g. "3.3". Required, non-empty.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The body that owns the scheme. Required, non-empty.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>The delivery/certification body, distinct from the authority. Optional.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>Absolute http/https URL of the official source. Optional.</summary>
    public string SourceUrl { get; init; } = string.Empty;
}

/// <summary>
/// A published normative statement belonging to exactly one <see cref="Standard"/>.
/// Identity is <see cref="Id"/>. <see cref="Title"/> is a short display label; <see cref="Statement"/>
/// is the full normative text. <see cref="Theme"/> is a free-form label grouping the standard's
/// requirements. The citation (<see cref="CitationLabel"/> + <see cref="CitationUrl"/>) points at the
/// published source.
/// </summary>
public sealed record Requirement
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Owning standard id (single).</summary>
    public string Standard { get; init; } = string.Empty;

    /// <summary>Free-form theme label; required, non-empty.</summary>
    public string Theme { get; init; } = string.Empty;

    /// <summary>The full normative requirement text.</summary>
    public string Statement { get; init; } = string.Empty;

    /// <summary>Optional helper text; blank means absent.</summary>
    public string Guidance { get; init; } = string.Empty;

    /// <summary>Human label for the published source.</summary>
    public string CitationLabel { get; init; } = string.Empty;

    /// <summary>Absolute http/https link to the published source.</summary>
    public string CitationUrl { get; init; } = string.Empty;
}

/// <summary>
/// An implemented control mapped to one or more requirements. <see cref="MapsTo"/> lists
/// <see cref="Requirement"/> ids; a control's standard is derivable from each requirement's owner.
/// </summary>
public sealed record Control
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<string> MapsTo { get; init; } = [];
}

/// <summary>What an organisation node represents in the tree.</summary>
public enum OrganisationKind
{
    Company,
    Department,
}

/// <summary>
/// A node in the organisation tree being assessed. Identity is <see cref="Id"/>.
/// <see cref="Parent"/> is the id of another organisation, empty for a root.
/// </summary>
public sealed record Organisation
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Raw Company/Department text as authored under the YAML key <c>type</c>; validation maps it
    /// to <see cref="OrganisationKind"/>. This is the organisation's kind, distinct from the
    /// document discriminator <see cref="Kind"/>.
    /// </summary>
    public string OrgKind { get; init; } = string.Empty;

    /// <summary>Parent organisation id, empty for a root.</summary>
    public string Parent { get; init; } = string.Empty;
}

/// <summary>Whether an organisation is in or out of scope for a standard.</summary>
public enum ScopeDisposition
{
    In,
    Out,
}

/// <summary>
/// Maps one <see cref="Organisation"/> to one <see cref="Standard"/> with a
/// <see cref="Disposition"/>. Identity is <see cref="Id"/>; at most one Scope exists
/// per <c>(organisation, standard)</c> pair.
/// </summary>
public sealed record Scope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Organisation { get; init; } = string.Empty;
    public string Standard { get; init; } = string.Empty;

    /// <summary>Raw disposition text as authored; validation maps it to <see cref="ScopeDisposition"/>.</summary>
    public string Disposition { get; init; } = string.Empty;
}

/// <summary>
/// The aggregate config model loaded from a directory.
/// </summary>
public sealed record GitOpsConfig
{
    public List<Standard> Standards { get; init; } = [];
    public List<Requirement> Requirements { get; init; } = [];
    public List<Control> Controls { get; init; } = [];
    public List<Organisation> Organisations { get; init; } = [];
    public List<Scope> Scopes { get; init; } = [];
}
