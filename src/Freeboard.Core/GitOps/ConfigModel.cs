namespace Freeboard.Core.GitOps;

/// <summary>
/// The only supported apiVersion for this increment.
/// </summary>
public static class GitOpsSchema
{
    public const string ApiVersion = "freeboard.io/v1alpha1";

    public const string KindStandard = "Standard";
    public const string KindControl = "Control";
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

/// <summary>
/// An org unit or asset group that controls apply to. <see cref="Controls"/> lists Control ids.
/// </summary>
public sealed record Scope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<string> Controls { get; init; } = [];
}

/// <summary>
/// The aggregate config model loaded from a directory.
/// </summary>
public sealed record GitOpsConfig
{
    public List<Standard> Standards { get; init; } = [];
    public List<Control> Controls { get; init; } = [];
    public List<Scope> Scopes { get; init; } = [];
}
