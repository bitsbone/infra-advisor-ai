namespace InfraAdvisor.Mobile.Services;

public sealed class ApiException : Exception
{
    public ApiException(string message, int? statusCode = null, string? category = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Category = category;
    }

    public int? StatusCode { get; }

    public string? Category { get; }
}
