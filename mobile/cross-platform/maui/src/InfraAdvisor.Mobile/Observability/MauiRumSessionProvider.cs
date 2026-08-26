using Datadog.Maui;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Observability;

public sealed class MauiRumSessionProvider : IRumSessionProvider
{
    public string? CurrentSessionId => DdRum.GetCurrentSessionId();
}
