using System.Text.Json;
using InfraAdvisor.AgentApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class ConversationServicePostgresTests
{
    [PostgresFact]
    public async Task ArtifactsRoundTripUnchangedThroughPostgres()
    {
        var priorDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        Environment.SetEnvironmentVariable("DATABASE_URL", Environment.GetEnvironmentVariable("TEST_DATABASE_URL"));
        var userId = $"artifact-test-{Guid.NewGuid():N}";
        ConversationService? service = null;
        ConversationSummary? conversation = null;
        try
        {
            service = new ConversationService(NullLogger<ConversationService>.Instance);
            await service.InitializeAsync();
            conversation = await service.CreateConversationAsync(userId, "Artifact persistence test", "test-model");
            Assert.NotNull(conversation);
            Assert.Equal(ConversationAccess.Owned, await service.CheckOwnershipAsync(conversation!.Id, userId));
            Assert.Equal(ConversationAccess.NotFound, await service.CheckOwnershipAsync(conversation.Id, "another-user"));
            var attachmentOnly = await service.CreateConversationAsync(userId, "   ", "test-model");
            Assert.NotNull(attachmentOnly);
            Assert.Equal("New Conversation", attachmentOnly.Title);
            Assert.True(await service.DeleteConversationAsync(attachmentOnly.Id, userId));

            var artifact = ChatArtifactParser.TryExtract("""
                {"kind":"procurement_opportunities","schema_version":"1.0","tool_name":"get_procurement_opportunities","tool_call_id":null,"status":"ok","generated_at":"2026-01-15T12:00:00Z","items":[{"id":"sam.gov:SAMPLE-1","provider":"sam.gov","provider_id":"SAMPLE-1","opportunity_type":"contract","title":"Sanitized sample","agency":{"name":"Example Agency","code":null},"summary":"","status":"posted","posted_at":"2026-01-01","deadline_at":"2026-03-01","location":{"state_code":"TX","state_name":"Texas","city":null},"classifications":{"naics":["237110"],"assistance_listing":[],"set_aside":null},"funding":{"currency":"USD","minimum":null,"maximum":null,"total":null,"expected_awards":null},"source":{"url":"https://sam.gov/opp/SAMPLE-1","retrieved_at":"2026-01-15T12:00:00Z"},"data_quality":{"missing_fields":[]}}],"meta":{"returned_count":1,"provider_counts":{"sam.gov":1,"grants.gov":0},"truncated":false,"partial_errors":[]}}
                """, "get_procurement_opportunities", "call-db-1");
            Assert.NotNull(artifact);

            Assert.False(await service.SaveMessagesAsync(conversation.Id, "another-user", "must not persist", "must not persist", [], null, null));
            Assert.True(await service.SaveMessagesAsync(conversation.Id, userId, "sample question", "sample answer", ["get_procurement_opportunities"], "trace-1", "span-1", artifacts: [artifact!.Value]));
            var restored = await service.GetConversationAsync(conversation.Id, userId);

            Assert.NotNull(restored);
            var assistant = Assert.Single(restored!.Messages, message => message.Role == "assistant");
            var restoredArtifact = Assert.Single(assistant.Artifacts);
            Assert.True(JsonElement.DeepEquals(artifact.Value, restoredArtifact));
            Assert.Equal("call-db-1", restoredArtifact.GetProperty("tool_call_id").GetString());
        }
        finally
        {
            if (service is not null && conversation is not null)
                await service.DeleteConversationAsync(conversation.Id, userId);
            Environment.SetEnvironmentVariable("DATABASE_URL", priorDatabaseUrl);
        }
    }
}
