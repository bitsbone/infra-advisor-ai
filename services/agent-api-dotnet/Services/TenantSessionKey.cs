using System.Security.Cryptography;
using System.Text;

namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// Creates an opaque Redis/agent-session key bound to the authenticated JWT
/// subject. Client session and conversation identifiers are routing hints,
/// never tenant boundaries on their own.
/// </summary>
public static class TenantSessionKey
{
    public static string Create(string userId, string sessionOrConversationId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionOrConversationId))
            throw new ArgumentException("User and session identifiers are required.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\0{sessionOrConversationId}"));
        return Convert.ToHexStringLower(bytes);
    }
}
