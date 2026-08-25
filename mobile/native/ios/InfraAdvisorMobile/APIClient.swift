import Foundation

/// Concrete delegate required by Datadog's URLSession swizzling. The delegate does not need
/// application callbacks; Datadog injects the lifecycle methods it observes at runtime.
final class InfraAdvisorURLSessionDelegate: NSObject, URLSessionDataDelegate {}

protocol APIClientProtocol {
    func login(email: String, password: String) async throws -> LoginResponse
    func models(backend: Backend) async throws -> ModelsResponse
    func listConversations(token: String) async throws -> [ConversationSummary]
    func conversation(token: String, id: String) async throws -> ConversationDetail
    func createConversation(token: String, title: String, model: String, backend: Backend) async throws -> ConversationSummary
    func query(token: String, prompt: String, sessionID: String, model: String, backend: Backend, userID: String, conversationID: String) async throws -> QueryResponse
}

enum APIClientError: LocalizedError, Equatable {
    case invalidResponse
    case http(status: Int, message: String)
    case decoding

    var errorDescription: String? {
        switch self {
        case .invalidResponse: return "The server returned an invalid response."
        case let .http(_, message): return message
        case .decoding: return "The server response could not be read."
        }
    }
}

final class APIClient: APIClientProtocol {
    private let baseURL: URL
    private let session: URLSession
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    init(baseURL: URL, session: URLSession) {
        self.baseURL = baseURL
        self.session = session
    }

    func login(email: String, password: String) async throws -> LoginResponse {
        try await post(path: "/auth/login", body: ["email": email, "password": password], token: nil)
    }

    func models(backend: Backend) async throws -> ModelsResponse {
        try await get(path: backend.apiPrefix + "/models", token: nil)
    }

    func listConversations(token: String) async throws -> [ConversationSummary] {
        let response: ConversationListResponse = try await get(path: "/api/conversations", token: token)
        return response.conversations
    }

    func conversation(token: String, id: String) async throws -> ConversationDetail {
        try await get(path: "/api/conversations/\(id)", token: token)
    }

    func createConversation(token: String, title: String, model: String, backend: Backend) async throws -> ConversationSummary {
        try await post(path: "/api/conversations", body: ConversationCreateRequest(title: title, model: model, backend: backend), token: token)
    }

    func query(token: String, prompt: String, sessionID: String, model: String, backend: Backend, userID: String, conversationID: String) async throws -> QueryResponse {
        var headers = ["Content-Type": "application/json", "X-Session-ID": sessionID, "X-User-ID": userID, "X-Conversation-ID": conversationID]
        headers["Authorization"] = "Bearer \(token)"
        return try await request(path: backend.apiPrefix + "/query", method: "POST", body: encoder.encode(QueryRequest(query: prompt, sessionID: sessionID, model: model)), headers: headers, timeout: 90)
    }

    private func get<Response: Decodable>(path: String, token: String?) async throws -> Response {
        var headers: [String: String] = [:]
        if let token { headers["Authorization"] = "Bearer \(token)" }
        return try await request(path: path, method: "GET", body: nil, headers: headers)
    }

    private func post<Response: Decodable, Body: Encodable>(path: String, body: Body, token: String?) async throws -> Response {
        var headers = ["Content-Type": "application/json"]
        if let token { headers["Authorization"] = "Bearer \(token)" }
        return try await request(path: path, method: "POST", body: encoder.encode(body), headers: headers)
    }

    private func request<Response: Decodable>(path: String, method: String, body: Data?, headers: [String: String], timeout: TimeInterval = 60) async throws -> Response {
        // This request must run through the delegate-backed session created after
        // URLSessionInstrumentation.enable. URLSession.shared is not used because Datadog
        // requires a concrete delegate class to observe resources and inject trace context.
        var request = URLRequest(url: baseURL.appending(path: path))
        request.httpMethod = method
        request.timeoutInterval = timeout
        headers.forEach { request.setValue($0.value, forHTTPHeaderField: $0.key) }
        request.httpBody = body

        // Privacy boundary: request/response bodies and Authorization are used only by the
        // HTTP client. They are never copied into RUM attributes or span tags.
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIClientError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            let body = try? decoder.decode(APIErrorBody.self, from: data)
            let fallback = HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
            throw APIClientError.http(status: http.statusCode, message: body?.detail ?? body?.message ?? "Request failed: \(fallback) (\(http.statusCode))")
        }
        do { return try decoder.decode(Response.self, from: data) }
        catch { throw APIClientError.decoding }
    }
}
