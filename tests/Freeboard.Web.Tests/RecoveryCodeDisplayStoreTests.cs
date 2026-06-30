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
    [Fact]
    public void SecondSequentialTakeReturnsNothing()
    {
        // The store does not own the cache (it is injected and disposed by DI in the app), so the test
        // owns and disposes the MemoryCache it creates.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new RecoveryCodeDisplayStore(cache);
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
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new RecoveryCodeDisplayStore(cache);
        var nonce = store.Stash(new[] { "code-1", "code-2" });

        // Fire many concurrent reads of the same nonce; only one may observe the codes.
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.Take(nonce)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r is not null));
    }
}
