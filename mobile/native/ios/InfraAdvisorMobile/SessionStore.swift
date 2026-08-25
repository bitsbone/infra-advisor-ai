import Foundation
import DatadogCore

@MainActor
final class SessionStore: ObservableObject {
    @Published private(set) var login: LoginResponse?
    @Published var isLoading = false
    @Published var errorMessage: String?

    private let api: APIClientProtocol

    init(api: APIClientProtocol) { self.api = api }

    func signIn(email: String, password: String) async {
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }
        do {
            let result = try await api.login(email: email, password: password)
            login = result
            // Identify all subsequent RUM events without attaching credentials or the JWT.
            // The backend's stable user ID is preferred over email as the primary identity.
            Datadog.setUserInfo(id: result.user.id, name: nil, email: result.user.email)
        } catch { errorMessage = error.localizedDescription }
    }

    func signOut() {
        login = nil
        errorMessage = nil
        // Clearing the Datadog identity prevents the next login on this device from being
        // attributed to the previous user. The JWT is also released with `login` above.
        Datadog.clearUserInfo()
    }
}
