import SwiftUI
import DatadogRUM

@MainActor
final class ChatViewModel: ObservableObject {
    @Published var prompt = "What infrastructure risks should a Texas city review before hurricane season?"
    @Published private(set) var response: QueryResponse?
    @Published private(set) var isLoading = false
    @Published var errorMessage: String?
    private let api: APIClientProtocol

    init(api: APIClientProtocol) { self.api = api }

    func submit(token: String) async {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        isLoading = true
        errorMessage = nil
        response = nil
        defer { isLoading = false }
        do {
            response = try await api.query(token: token, prompt: trimmed, sessionID: UUID().uuidString)
        } catch { errorMessage = error.localizedDescription }
    }
}

struct ChatView: View {
    @EnvironmentObject private var session: SessionStore
    @StateObject private var model: ChatViewModel

    init(api: APIClientProtocol) { _model = StateObject(wrappedValue: ChatViewModel(api: api)) }

    var body: some View {
        NavigationStack {
            Form {
                Section("Prompt") {
                    TextEditor(text: $model.prompt).frame(minHeight: 110)
                    Button("Ask Infra Advisor") {
                        guard let token = session.login?.token else { return }
                        Task { await model.submit(token: token) }
                    }
                    .disabled(model.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.isLoading)
                }
                if model.isLoading { Section { HStack { ProgressView(); Text("Consulting the agent…") } } }
                if let error = model.errorMessage { Section("Error") { Text(error).foregroundStyle(.red) } }
                if let result = model.response {
                    Section("Answer") { Text(result.answer) }
                    if !result.sources.isEmpty {
                        Section("Sources") { ForEach(result.sources, id: \.self) { Text($0) } }
                    }
                    Section("Trace metadata") {
                        LabeledContent("Backend trace", value: result.traceID ?? "Unavailable")
                        LabeledContent("Session", value: result.sessionID)
                        LabeledContent("Model", value: result.model)
                    }
                }
            }
            .navigationTitle("Infra Advisor")
            .toolbar { ToolbarItem(placement: .topBarTrailing) { Button("Logout", action: session.signOut) } }
        }
        .trackRUMView(name: "Chat")
    }
}
