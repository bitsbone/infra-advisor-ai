namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// KAFKA_ENABLED=false turns off every Kafka touchpoint in this service: the
/// eval-loop consumer is not registered and contract-awards events go to a
/// no-op publisher. The dev namespace sets it — prod and dev share one Kafka
/// cluster, and a dev pod in the same consumer group would take partitions
/// (and synthetic queries) from prod. Mirrors agent-api's kafka_config.py.
/// </summary>
public static class KafkaSettings
{
    public static bool Enabled => IsEnabled(Environment.GetEnvironmentVariable("KAFKA_ENABLED"));

    public static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off");
}

/// <summary>Used when <see cref="KafkaSettings.Enabled"/> is false.</summary>
public sealed class NoOpContractAwardsEventPublisher : IContractAwardsEventPublisher
{
    public void Publish(string sessionId, string? toolCallId, object? queryInput, System.Text.Json.JsonElement items) { }
}
