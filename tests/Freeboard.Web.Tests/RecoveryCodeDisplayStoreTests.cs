using Freeboard.Web;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web.Tests;

/// <summary>
/// The one-time recovery-codes display store. A nonce's codes can be claimed exactly once: a second
/// Take (sequential or concurrent) returns nothing, so two same-nonce requests cannot both walk away
/// with the codes.
/// </summary>
public sealed class RecoveryCodeDisplayStoreTests
{
    private static RecoveryCodeDisplayStore NewStore()
        => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void SecondSequentialTakeReturnsNothing()
    {
        var store = NewStore();
        var nonce = store.Stash(new[] { "code-1", "code-2" });

        var first = store.Take(nonce);
        var second = store.Take(nonce);

        Assert.NotNull(first);
        Assert.Equal(2, first!.Count);
        Assert.Null(second);
    }

    [Fact]
    public async Task ConcurrentTakesYieldTheCodesToExactlyOneCaller()
    {
        var store = NewStore();
        var nonce = store.Stash(new[] { "code-1", "code-2" });

        // Fire many concurrent reads of the same nonce; only one may observe the codes.
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.Take(nonce)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r is not null));
    }
}
