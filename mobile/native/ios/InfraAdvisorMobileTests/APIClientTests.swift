import XCTest
@testable import InfraAdvisorMobile

final class URLProtocolStub: URLProtocol {
    static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let (response, data) = try Self.handler!(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}

final class APIClientTests: XCTestCase {
    private var client: APIClient!
    override func setUp() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [URLProtocolStub.self]
        client = APIClient(baseURL: URL(string: "https://example.test")!, session: URLSession(configuration: configuration))
    }

    private func bodyData(from request: URLRequest) throws -> Data {
        if let body = request.httpBody { return body }
        let stream = try XCTUnwrap(request.httpBodyStream)
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 1_024)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            if count < 0 { throw try XCTUnwrap(stream.streamError) }
            if count == 0 { break }
            data.append(contentsOf: buffer.prefix(count))
        }
        return data
    }

    func testLoginSerializesCredentialsAndDecodesUser() async throws {
        URLProtocolStub.handler = { [self] request in
            XCTAssertEqual(request.url?.path, "/auth/login")
            // URLSession may surface uploads as a stream after protocol instrumentation.
            let body = try bodyData(from: request)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: String])
            XCTAssertEqual(json["email"], "demo@datadoghq.com")
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            let data = #"{"token":"jwt","user":{"id":"1","email":"demo@datadoghq.com","is_admin":false,"is_service_account":false,"created_at":"2026-01-01"}}"#.data(using: .utf8)!
            return (response, data)
        }
        let result = try await client.login(email: "demo@datadoghq.com", password: "secret")
        XCTAssertEqual(result.token, "jwt")
    }

    func testQueryAddsBearerHeaderAndDecodesTraceMetadata() async throws {
        URLProtocolStub.handler = { [self] request in
            XCTAssertEqual(request.url?.path, "/api-dotnet/query")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer jwt")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-Session-ID"), "session")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-User-ID"), "user-1")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-Conversation-ID"), "conversation-1")
            let body = try bodyData(from: request)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: String])
            XCTAssertEqual(json["model"], "gpt-4.1-mini")
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            let data = #"{"answer":"ok","sources":[],"trace_id":"42","span_id":"7","session_id":"session","model":"gpt"}"#.data(using: .utf8)!
            return (response, data)
        }
        let result = try await client.query(token: "jwt", prompt: "hello", sessionID: "session", model: "gpt-4.1-mini", backend: .dotnet, userID: "user-1", conversationID: "conversation-1")
        XCTAssertEqual(result.traceID, "42")
    }

    func testConversationListDecodesSavedBackendAndModel() async throws {
        URLProtocolStub.handler = { request in
            XCTAssertEqual(request.url?.path, "/api/conversations")
            XCTAssertEqual(request.httpMethod, "GET")
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            let data = #"{"conversations":[{"id":"c1","user_id":"u1","title":"Flood plan","model":"gpt-4.1-mini","backend":"python","created_at":null,"updated_at":null,"message_count":2}]}"#.data(using: .utf8)!
            return (response, data)
        }

        let result = try await client.listConversations(token: "jwt")
        XCTAssertEqual(result.first?.backend, .python)
        XCTAssertEqual(result.first?.model, "gpt-4.1-mini")
    }

    func testInstrumentedSessionInjectsDatadogAndW3CTraceHeaders() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [URLProtocolStub.self]
        let instrumentedSession = URLSession(
            configuration: configuration,
            delegate: InfraAdvisorURLSessionDelegate(),
            delegateQueue: nil
        )
        client = APIClient(
            baseURL: URL(string: "https://infra-advisor-ai.bitsbone.com")!,
            session: instrumentedSession
        )
        URLProtocolStub.handler = { request in
            // Assert only header presence. Trace IDs are observability context and should not
            // be printed or copied into test output.
            XCTAssertNotNil(request.value(forHTTPHeaderField: "x-datadog-trace-id"))
            XCTAssertNotNil(request.value(forHTTPHeaderField: "x-datadog-parent-id"))
            XCTAssertNotNil(request.value(forHTTPHeaderField: "traceparent"))
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            let data = #"{"answer":"ok","sources":[],"trace_id":"42","span_id":"7","session_id":"session","model":"gpt"}"#.data(using: .utf8)!
            return (response, data)
        }

        _ = try await client.query(token: "jwt", prompt: "hello", sessionID: "session", model: "gpt-4.1-mini", backend: .python, userID: "user-1", conversationID: "conversation-1")
    }

    func testServerDetailBecomesReadableError() async throws {
        URLProtocolStub.handler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            return (response, #"{"detail":"Invalid email or password"}"#.data(using: .utf8)!)
        }
        do {
            _ = try await client.login(email: "x", password: "y")
            XCTFail("Expected an error")
        } catch let error as APIClientError {
            XCTAssertEqual(error, .http(status: 401, message: "Invalid email or password"))
        }
    }

    func testErrorLabUsesBearerHeaderAndIntentionalMissingRoute() async throws {
        URLProtocolStub.handler = { request in
            XCTAssertEqual(request.url?.path, "/api/error-lab/not-found")
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer jwt")
            let response = HTTPURLResponse(url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!
            return (response, #"{"detail":"Not Found"}"#.data(using: .utf8)!)
        }
        do {
            try await client.simulateAPIError(token: "jwt")
            XCTFail("Expected a 404")
        } catch let error as APIClientError {
            XCTAssertEqual(error, .http(status: 404, message: "Not Found"))
        }
    }
}
