using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

/// <summary>Marks a test that requires the CI-provided disposable PostgreSQL service.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DATABASE_URL")))
            Skip = "TEST_DATABASE_URL is not configured";
    }
}
