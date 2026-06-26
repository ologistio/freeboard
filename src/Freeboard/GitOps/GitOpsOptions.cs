namespace Freeboard.GitOps;

/// <summary>
/// Web GitOps options bound from the <c>Freeboard:GitOps</c> config section.
/// </summary>
public sealed class GitOpsOptions
{
    public const string SectionName = "Freeboard:GitOps";

    /// <summary>When true, the app is read-only: mutating requests are rejected.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Optional git repo URL surfaced to callers. Empty means omitted.</summary>
    public string RepositoryUrl { get; set; } = string.Empty;
}
