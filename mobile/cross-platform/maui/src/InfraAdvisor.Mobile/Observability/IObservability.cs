namespace InfraAdvisor.Mobile.Observability;

public interface IObservability
{
    void IdentifyUser(string id, string email);

    void ClearUser();

    void StopSession();

    void Info(string message, IReadOnlyDictionary<string, object>? attributes = null);

    void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null);
}
