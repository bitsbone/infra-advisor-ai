namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// Converts internal failures into a stable client contract. Exception types
/// are safe diagnostic categories; messages and stack traces remain server-side
/// because they can contain provider URLs, credentials, queries, or database details.
/// </summary>
public sealed record PublicError(string Detail, string ErrorType)
{
    public static PublicError Unexpected(Exception exception, string detail = "The service encountered an unexpected error.") =>
        new(detail, exception.GetType().Name);
}
