import Foundation

enum Backend: String, Codable, CaseIterable, Identifiable {
    case python
    case dotnet

    var id: String { rawValue }
    var displayName: String { self == .python ? "Python" : ".NET" }
    var apiPrefix: String { self == .python ? "/api" : "/api-dotnet" }
}

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

struct ModelsResponse: Codable, Equatable {
    let models: [String]
    let defaultModel: String

    enum CodingKeys: String, CodingKey {
        case models
        case defaultModel = "default"
    }
}

struct ConversationSummary: Codable, Equatable, Identifiable {
    let id: String
    let userID: String
    let title: String
    let model: String?
    let backend: Backend?
    let createdAt: String?
    let updatedAt: String?
    let messageCount: Int

    enum CodingKeys: String, CodingKey {
        case id, title, model, backend
        case userID = "user_id"
        case createdAt = "created_at"
        case updatedAt = "updated_at"
        case messageCount = "message_count"
    }
}

struct ConversationMessage: Codable, Equatable, Identifiable {
    let id: String
    let conversationID: String
    let role: String
    let content: String
    let sources: [String]
    let traceID: String?
    let spanID: String?
    let createdAt: String?

    enum CodingKeys: String, CodingKey {
        case id, role, content, sources
        case conversationID = "conversation_id"
        case traceID = "trace_id"
        case spanID = "span_id"
        case createdAt = "created_at"
    }
}

struct ConversationDetail: Codable, Equatable, Identifiable {
    let id: String
    let userID: String
    let title: String
    let model: String?
    let backend: Backend?
    let createdAt: String?
    let updatedAt: String?
    let messageCount: Int
    let messages: [ConversationMessage]

    enum CodingKeys: String, CodingKey {
        case id, title, model, backend, messages
        case userID = "user_id"
        case createdAt = "created_at"
        case updatedAt = "updated_at"
        case messageCount = "message_count"
    }
}

struct ConversationListResponse: Decodable {
    let conversations: [ConversationSummary]
}

struct ConversationCreateRequest: Encodable {
    let title: String
    let model: String
    let backend: Backend
}

struct QueryRequest: Encodable {
    let query: String
    let sessionID: String
    let model: String

    enum CodingKeys: String, CodingKey {
        case query, model
        case sessionID = "session_id"
    }
}

struct APIErrorBody: Decodable {
    let detail: String?
    let message: String?
}
