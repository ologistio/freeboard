using Freeboard.Web;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web.Tests;

/// <summary>
/// The one-time temp-password display store. A nonce's value can be claimed exactly once: a second
/// Take (sequential or concurrent) returns nothing, so two same-nonce requests cannot both walk away
/// with the temp password.
/// </summary>
public sealed class TempPasswordDisplayStoreTests
{
    [Fact]
    public void SecondSequentialTakeReturnsNothing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TempPasswordDisplayStore(cache);
        var nonce = store.Stash("ABCDE-FGHJK-MNPQR-STVWX");

        var first = store.Take(nonce);
        var second = store.Take(nonce);

        Assert.Equal("ABCDE-FGHJK-MNPQR-STVWX", first);
        Assert.Null(second);
    }

    [Fact]
    public void TakeWithNoNonceReturnsNothing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TempPasswordDisplayStore(cache);

        Assert.Null(store.Take(null));
        Assert.Null(store.Take(string.Empty));
        Assert.Null(store.Take("never-stashed"));
    }

    [Fact]
    public async Task ConcurrentTakesYieldTheValueToExactlyOneCaller()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TempPasswordDisplayStore(cache);
        var nonce = store.Stash("ABCDE-FGHJK-MNPQR-STVWX");

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.Take(nonce)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r is not null));
    }
}
