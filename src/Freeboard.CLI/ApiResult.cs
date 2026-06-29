namespace Freeboard.CLI;

/// <summary>
/// The outcome category of an API call, mapped from HTTP status so commands translate to the
/// CLI exit-code convention (0 success, 1 input/validation, 3 operational).
/// </summary>
internal enum ApiOutcome
{
    /// <summary>2xx: the call succeeded and a payload is present.</summary>
    Success,

    /// <summary>422: input/validation error. The API message is in <see cref="ApiResult{T}.Message"/>.</summary>
    Validation,

    /// <summary>409: a conflict (e.g. bootstrap already initialized).</summary>
    Conflict,

    /// <summary>401/403: the admin token is missing, wrong, or not authorized.</summary>
    Unauthorized,

    /// <summary>Any other failure: 5xx, an unexpected status, or a transport error (connection refused).</summary>
    Failure,
}

/// <summary>
/// A small result type carrying the call outcome, an optional success payload, and a
/// human-readable message for the non-success cases. Keeps HttpClient details out of the
/// command layer.
/// </summary>
internal sealed record ApiResult<T>(ApiOutcome Outcome, T? Payload, string? Message)
{
    public static ApiResult<T> Success(T payload) => new(ApiOutcome.Success, payload, null);

    public static ApiResult<T> Validation(string message) => new(ApiOutcome.Validation, default, message);

    public static ApiResult<T> Conflict(string message) => new(ApiOutcome.Conflict, default, message);

    public static ApiResult<T> Unauthorized(string message) => new(ApiOutcome.Unauthorized, default, message);

    public static ApiResult<T> Failure(string message) => new(ApiOutcome.Failure, default, message);
}
