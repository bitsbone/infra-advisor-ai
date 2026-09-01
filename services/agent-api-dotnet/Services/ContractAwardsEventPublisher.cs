using System.Text.Json;
using Confluent.Kafka;

namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// Fire-and-forget event publishing for downstream analysis/modeling.
///
/// Separate from KafkaConsumerService, which owns the synthetic-load eval
/// loop's own consumer/producer lifecycle and topics. This is a lightweight,
/// singleton producer for one-off event publishes from the request path — it
/// must never throw into a chat request, and never blocks on network I/O.
/// </summary>
public interface IContractAwardsEventPublisher
{
    void Publish(string sessionId, string? toolCallId, object? queryInput, JsonElement items);
}

public sealed class ContractAwardsEventPublisher : IContractAwardsEventPublisher, IDisposable
{
    private const string Topic = "infra.contract-awards.raw";
    private readonly ILogger<ContractAwardsEventPublisher> _logger;
    private readonly IProducer<Null, string> _producer;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ContractAwardsEventPublisher(ILogger<ContractAwardsEventPublisher> logger)
    {
        _logger = logger;
        var bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
            ?? "kafka-cluster-kafka-bootstrap.kafka.svc.cluster.local:9092";
        _producer = new ProducerBuilder<Null, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build();
    }

    public void Publish(string sessionId, string? toolCallId, object? queryInput, JsonElement items)
    {
        try
        {
            var awards = items.ValueKind == JsonValueKind.Array ? items : default;
            var awardCount = awards.ValueKind == JsonValueKind.Array ? awards.GetArrayLength() : 0;
            var payload = new
            {
                event_type = "contract_awards.query",
                schema_version = "1.0",
                occurred_at = DateTimeOffset.UtcNow.ToString("O"),
                session_id = sessionId,
                tool_call_id = toolCallId,
                query_input = queryInput ?? new { },
                raw_awards = awards,
                raw_award_count = awardCount,
                deduped_award_count = awardCount,
            };
            var value = JsonSerializer.Serialize(payload, JsonOptions);
            _producer.Produce(Topic, new Message<Null, string> { Value = value }, DeliveryReportHandler);
            _producer.Poll(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to publish contract_awards event: {Error}", ex.Message);
        }
    }

    private void DeliveryReportHandler(DeliveryReport<Null, string> report)
    {
        if (report.Error.IsError)
            _logger.LogWarning("Kafka delivery failed for topic={Topic}: {Error}", report.Topic, report.Error.Reason);
    }

    public void Dispose() => _producer.Dispose();
}
