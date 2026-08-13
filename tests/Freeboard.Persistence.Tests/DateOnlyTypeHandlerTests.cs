using System.Data;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// The handler is the only thing that lets a <see cref="DateOnly"/> reach a MySQL DATE column, so it
/// is exercised here as well as through the gated sync tests: those skip without a database, and a
/// regression here would otherwise pass an unattended run.
/// </summary>
public sealed class DateOnlyTypeHandlerTests
{
    private static readonly DateOnly Expiry = new(2027, 3, 27);

    [Fact]
    public void SetValueSendsADateWithNoTimeComponent()
    {
        var parameter = new MySqlParameter();

        new DateOnlyTypeHandler().SetValue(parameter, Expiry);

        Assert.Equal(DbType.Date, parameter.DbType);
        Assert.Equal(new DateTime(2027, 3, 27, 0, 0, 0, DateTimeKind.Unspecified), parameter.Value);
    }

    [Fact]
    public void ParseReadsTheDateTimeAConnectorReturnsForADateColumn()
        => Assert.Equal(Expiry, new DateOnlyTypeHandler().Parse(new DateTime(2027, 3, 27)));

    [Fact]
    public void ParseThrowsRatherThanCoercingSomethingThatIsNotADate()
    {
        var handler = new DateOnlyTypeHandler();

        // A string would parse into a plausible date, which would hide a column whose type had changed.
        Assert.Throws<InvalidOperationException>(() => handler.Parse("2027-03-27"));
    }
}
