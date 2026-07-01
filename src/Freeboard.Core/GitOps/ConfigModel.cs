namespace Freeboard.Core.GitOps;

/// <summary>
/// The only supported apiVersion for this increment.
/// </summary>
public static class GitOpsSchema
{
    public const string ApiVersion = "freeboard.io/v1alpha1";

    public const string KindStandard = "Standard";
    public const string KindControl = "Control";
    public const string KindOrganisation = "Organisation";
    public const string KindScope = "Scope";
}

/// <summary>
/// A compliance standard in scope. Identity is <see cref="Id"/>; <see cref="Title"/> is display only.
/// </summary>
public sealed record Standard
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// A requirement under one or more standards. <see cref="MapsTo"/> lists Standard ids.
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
    public List<Control> Controls { get; init; } = [];
    public List<Organisation> Organisations { get; init; } = [];
    public List<Scope> Scopes { get; init; } = [];
}
