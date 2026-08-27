namespace InfraAdvisor.Mobile.Observability;

public interface IObservability
{
    void IdentifyUser(string id, string email);

    void ClearUser();

    void StopSession();

    string StartOperation(string name, IReadOnlyDictionary<string, object>? attributes = null);

    void SucceedOperation(string name, string operationKey, IReadOnlyDictionary<string, object>? attributes = null);

    void FailOperation(string name, string operationKey, bool abandoned, IReadOnlyDictionary<string, object>? attributes = null);

    void Info(string message, IReadOnlyDictionary<string, object>? attributes = null);

    void Warning(string message, IReadOnlyDictionary<string, object>? attributes = null);

    void ErrorLog(string message, IReadOnlyDictionary<string, object>? attributes = null);

    void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null);
}
