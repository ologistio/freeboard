using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence.Tests.Auth;

public sealed class UlidFactoryTests
{
    [Fact]
    public void NewIdIs26CharCrockfordBase32()
    {
        var factory = new UlidFactory();

        var id = factory.NewId();

        Assert.Equal(26, id.Length);
        // Round-trips through Parse unchanged.
        Assert.Equal(id, factory.Parse(id));
    }

    [Fact]
    public void IdsGeneratedOverTimeSortInCreationOrder()
    {
        // ULIDs are time-ordered, so the lexical (ordinal) sort of the CHAR(26) form
        // matches creation order - the property the binary-collated PK relies on.
        var factory = new UlidFactory();
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(factory.NewId());
            Thread.Sleep(2);
        }

        var sorted = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(ids, sorted);
    }

    [Fact]
    public void ParseRejectsAnInvalidUlid()
    {
        var factory = new UlidFactory();

        Assert.ThrowsAny<Exception>(() => factory.Parse("not-a-ulid"));
    }
}
