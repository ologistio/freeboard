using System.Text.RegularExpressions;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins WHERE an organisation-anchored authorization resource may be built. A resource handed to the
/// authorizer with an organisation id and no ancestry gets the plain parent walk, which crosses a
/// non-organisation link - so a grant beyond that link would authorize a write on the far side of it.
/// Every organisation gate therefore anchors on the organisation-BOUNDED chain, which only
/// <c>AuthzRequestCache</c> builds. The authorizer keeps its walk for resources that carry no
/// organisation at all, so this is the only thing standing between a new caller and the wider chain: a
/// construction that names an organisation elsewhere would compile, pass every other test, and widen
/// silently. Source-scanning because the rule is about the call site, not a runtime value.
/// </summary>
public sealed class AuthzResourceConstructionTests
{
    private const string AnchorFile = "AuthzRequestCache.cs";

    // Both ways a resource is built: the explicit construction, and the target-typed one, which is the
    // same call with the type moved to the left of the `=` and may be declared nullable.
    private const string NewResource =
        @"new\s+AuthzResource\s*\(|\bAuthzResource\s*\??\s+\w+\s*=\s*new\s*\(";

    private static readonly Regex Construction = new(NewResource, RegexOptions.Compiled);

    // Permitted shape: a literal null as the third positional argument (the organisation id).
    private static readonly Regex NoOrganisation =
        new(@"^\s*[^,()]+,\s*[^,()]+,\s*null\s*[,)]", RegexOptions.Compiled);

    // Two shapes name an organisation without a positional argument the construction scan can read: a
    // `with` expression on an existing resource, and an object initializer on a construction that passed
    // a literal null positionally - which the construction scan clears as organisation-free. Tying the
    // assignment back to the construction it belongs to means matching an argument list and an
    // initializer body across arbitrary line breaks and nesting, which no regex does without holes, so
    // this matches the assignment alone wherever it appears. The property is init-only, so an object
    // initializer or a `with` is the only thing that can put it on the left of an `=`. Keying off the
    // property rather than the receiver's type over-matches any record with an `OrganisationId`;
    // nothing in the web project rebinds one today, and over-matching fails toward the offender list,
    // which is the direction this tripwire has to fail in.
    private static readonly Regex OrganisationRebind =
        new(@"\bOrganisationId\s*=(?![=>])", RegexOptions.Compiled);

    [Fact]
    public void OnlyTheRequestCacheNamesAnOrganisationOnAResource()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in WebSourceFiles().Where(f => Path.GetFileName(f) != AnchorFile))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Construction.Matches(source))
            {
                scanned++;
                if (!NoOrganisation.IsMatch(source[(match.Index + match.Length)..]))
                {
                    offenders.Add(Where(file, source, match.Index));
                }
            }

            foreach (Match match in OrganisationRebind.Matches(source))
            {
                offenders.Add(Where(file, source, match.Index));
            }
        }

        // Only the construction scan can be checked for staleness: a rebind is an offender wherever it
        // appears, so there is no permitted occurrence to count.
        Assert.True(scanned > 0, "no AuthzResource construction found: the scan pattern is stale.");
        Assert.True(
            offenders.Count == 0,
            $"build these through {AnchorFile} so the chain is organisation-bounded: {string.Join(", ", offenders)}");
    }

    private static string Where(string file, string source, int index)
        => $"{Path.GetFileName(file)}:{source.Take(index).Count(c => c == '\n') + 1}";

    private static IEnumerable<string> WebSourceFiles()
    {
        var root = Path.Join(RepoRoot(), "src", "Freeboard");
        Assert.True(Directory.Exists(root), $"web project not found: {root}");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
