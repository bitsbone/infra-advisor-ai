import Foundation

protocol APIClientProtocol {
    func login(email: String, password: String) async throws -> LoginResponse
    func query(token: String, prompt: String, sessionID: String) async throws -> QueryResponse
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

    init(baseURL: URL, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.session = session
    }

    func login(email: String, password: String) async throws -> LoginResponse {
        try await post(path: "/auth/login", body: ["email": email, "password": password], token: nil)
    }

    func query(token: String, prompt: String, sessionID: String) async throws -> QueryResponse {
        try await post(path: "/api/query", body: ["query": prompt, "session_id": sessionID], token: token)
    }

    private func post<Response: Decodable>(path: String, body: [String: String], token: String?) async throws -> Response {
        // Use URLSession directly. Datadog's URLSession instrumentation, configured at app
        // startup, observes this request and injects trace headers for the first-party host.
        var request = URLRequest(url: baseURL.appending(path: path))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        request.httpBody = try encoder.encode(body)

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
