using System.Text.Json;

namespace InfraAdvisor.AgentApi.Models;

public record AgentResult(
    string Answer,
    List<string> Sources,
    List<string> ToolsCalled,
    string QueryDomain,
    List<JsonElement>? Artifacts = null,
    bool Blocked = false,
    string? BlockReason = null
);
