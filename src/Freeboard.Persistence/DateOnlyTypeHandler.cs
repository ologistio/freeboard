using System.Data;
using Dapper;

namespace Freeboard.Persistence;

/// <summary>
/// Maps <see cref="DateOnly"/> onto a MySQL <c>DATE</c> column in both directions.
/// </summary>
/// <remarks>
/// Dapper has no built-in handler for <see cref="DateOnly"/>: passing one as a parameter throws
/// <c>NotSupportedException</c> at execute time, which no unit test sees because it needs a real
/// connection. Registering once here keeps the conversion out of every call site and off the
/// domain types, so a date column stays a date all the way from config to the page.
/// </remarks>
internal sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    internal static void Register() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    // MySqlConnector returns a DateTime for a DATE column. Anything else means the column is no
    // longer a DATE, which should fail loudly rather than be coerced into a plausible date.
    public override DateOnly Parse(object value) => value is DateTime timestamp
        ? DateOnly.FromDateTime(timestamp)
        : throw new InvalidOperationException(
            $"Expected a DATE column to read as DateTime, got {value?.GetType().Name ?? "null"}.");
}
