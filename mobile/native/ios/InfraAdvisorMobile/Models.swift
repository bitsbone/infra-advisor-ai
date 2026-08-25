import Foundation

struct User: Codable, Equatable {
    let id: String
    let email: String
    let isAdmin: Bool
    let isServiceAccount: Bool
    let createdAt: String

    enum CodingKeys: String, CodingKey {
        case id, email
        case isAdmin = "is_admin"
        case isServiceAccount = "is_service_account"
        case createdAt = "created_at"
    }
}

struct LoginResponse: Codable, Equatable {
    let token: String
    let user: User
}

struct QueryResponse: Codable, Equatable {
    let answer: String
    let sources: [String]
    let traceID: String?
    let spanID: String?
    let sessionID: String
    let model: String

    enum CodingKeys: String, CodingKey {
        case answer, sources, model
        case traceID = "trace_id"
        case spanID = "span_id"
        case sessionID = "session_id"
    }
}

struct APIErrorBody: Decodable {
    let detail: String?
    let message: String?
}
