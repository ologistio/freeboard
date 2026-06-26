namespace Freeboard.Core.GitOps;

/// <summary>
/// A single structured problem found while loading or validating config.
/// Errors are data: the loader and validator never throw on bad input.
/// </summary>
public sealed record Diagnostic
{
    /// <summary>Config file the problem was found in, relative path where known.</summary>
    public string? File { get; init; }

    /// <summary>1-based line number where known, otherwise null.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column number where known, otherwise null.</summary>
    public int? Column { get; init; }

    /// <summary>Human-readable description of the problem.</summary>
    public required string Message { get; init; }

    public override string ToString()
    {
        var location = File;
        if (location is not null && Line is int line)
        {
            location += $":{line}";
            if (Column is int col)
            {
                location += $":{col}";
            }
        }

        return location is null ? Message : $"{location}: {Message}";
    }
}

/// <summary>
/// The result of loading and/or validating a config directory: the typed model
/// plus every diagnostic collected. <see cref="IsValid"/> is true when there are
/// no diagnostics.
/// </summary>
public sealed record ConfigResult
{
    public required GitOpsConfig Config { get; init; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    public bool IsValid => Diagnostics.Count == 0;
}
