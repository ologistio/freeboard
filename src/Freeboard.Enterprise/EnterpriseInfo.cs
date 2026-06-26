namespace Freeboard.Enterprise;

/// <summary>
/// Marker for enterprise-only functionality. Code in this library is covered
/// by the EE license carve-outs and must not be referenced by community-only
/// components (the Agent and CLI).
/// </summary>
public static class EnterpriseInfo
{
    public const string Edition = "Enterprise";
}
