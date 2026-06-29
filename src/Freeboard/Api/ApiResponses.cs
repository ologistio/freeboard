using Freeboard.Persistence.Auth;

namespace Freeboard.Api;

/// <summary>
/// Shared response/error shapes for the Freeboard API. The user object and the 422
/// validation problem body are defined once so every auth endpoint serializes them identically.
/// </summary>
public static class ApiResponses
{
    /// <summary>
    /// The public Freeboard user JSON object. Field names are the API contract (snake_case,
    /// ULID id as a string). Never carries credentials.
    /// </summary>
    public static object UserObject(UserRow user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new
        {
            id = user.Id,
            name = user.Name,
            email = user.Email,
            global_role = user.GlobalRole,
            enabled = user.Enabled,
            force_password_reset = user.ForcePasswordReset,
            mfa_enabled = user.MfaEnabled,
            created_at = user.CreatedAt,
            updated_at = user.UpdatedAt,
        };
    }

    /// <summary>
    /// An RFC 7807 validation problem (HTTP 422). <paramref name="errors"/> maps a field name to
    /// its messages, matching ASP.NET Core's validation-problem shape.
    /// </summary>
    public static IResult ValidationProblem(IDictionary<string, string[]> errors, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Results.ValidationProblem(
            errors,
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Validation failed",
            type: "https://freeboard.io/problems/validation");
    }

    /// <summary>A 422 with a single field error.</summary>
    public static IResult ValidationProblem(string field, string message)
        => ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
