using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Freeboard.Core.GitOps;

namespace Freeboard.CLI.Tests;

/// <summary>
/// Structural proof that the gitops load/validate code path cannot make a network
/// call: the Freeboard.Core assembly (which holds the loader and validator)
/// references no HTTP or socket APIs. This is deterministic and does not depend on
/// network state.
/// </summary>
public sealed class NoNetworkStructuralTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "System.Net.Http",
        "System.Net.Sockets",
    ];

    [Fact]
    public void CoreAssemblyReferencesNoHttpOrSocketTypes()
    {
        var corePath = typeof(ConfigLoader).Assembly.Location;

        using var stream = File.OpenRead(corePath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var offenders = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Where(typeRef =>
            {
                var ns = reader.GetString(typeRef.Namespace);
                return ForbiddenNamespaces.Any(forbidden => ns == forbidden || ns.StartsWith(forbidden + ".", StringComparison.Ordinal));
            })
            .Select(typeRef => $"{reader.GetString(typeRef.Namespace)}.{reader.GetString(typeRef.Name)}")
            .ToList();

        Assert.True(offenders.Count == 0, "Forbidden network type references found: " + string.Join(", ", offenders));
    }
}
