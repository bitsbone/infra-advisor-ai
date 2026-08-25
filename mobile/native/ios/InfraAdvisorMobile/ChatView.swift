import SwiftUI
import DatadogRUM
import Darwin

extension Color {
    static let infraAdvisorPurple = Color(red: 0.39, green: 0.17, blue: 0.65)
    static let infraAdvisorSurface = Color(red: 0.97, green: 0.95, blue: 0.99)
}

@MainActor
final class ChatViewModel: ObservableObject {
    static let suggestions = [
        "What infrastructure risks should a Texas city review before hurricane season?",
        "Summarize FEMA flood disaster declarations in Texas since 2015 by county.",
        "What should a city evaluate before replacing an aging bridge?",
        "What current federal procurement opportunities exist related to operational resilience or emergency management enhancements in Texas infrastructure systems?"
    ]

    @Published var prompt = ""
    @Published private(set) var conversations: [ConversationSummary] = []
    @Published private(set) var messages: [ConversationMessage] = []
    @Published private(set) var availableModels = ["gpt-4.1-mini"]
    @Published var selectedConversationID: String?
    @Published var selectedBackend: Backend = .python
    @Published var selectedModel = "gpt-4.1-mini"
    @Published private(set) var lastResponse: QueryResponse?
    @Published private(set) var isLoading = false
    @Published var errorMessage: String?

    private let api: APIClientProtocol
    private var sessionID = UUID().uuidString

    init(api: APIClientProtocol) { self.api = api }

    func load(token: String) async {
        isLoading = true
        defer { isLoading = false }
        await loadModels()
        await refreshConversations(token: token)
    }

    func chooseSuggestion(_ value: String) { prompt = value }

    func changeBackend(_ backend: Backend) async {
        selectedBackend = backend
        newConversation()
        await loadModels()
    }

    func selectConversation(_ id: String?, token: String) async {
        guard let id else { newConversation(); return }
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }
        do {
            let detail = try await api.conversation(token: token, id: id)
            selectedConversationID = detail.id
            selectedBackend = detail.backend ?? .python
            sessionID = detail.id
            messages = detail.messages
            lastResponse = nil
            await loadModels()
            if let savedModel = detail.model, availableModels.contains(savedModel) { selectedModel = savedModel }
        } catch { errorMessage = error.localizedDescription }
    }

    func newConversation() {
        selectedConversationID = nil
        sessionID = UUID().uuidString
        messages = []
        lastResponse = nil
        errorMessage = nil
    }

    func submit(token: String, userID: String) async {
        let question = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty else { return }
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }
        do {
            let conversationID: String
            if let selectedConversationID {
                conversationID = selectedConversationID
            } else {
                let title = String(question.prefix(72))
                let created = try await api.createConversation(token: token, title: title, model: selectedModel, backend: selectedBackend)
                selectedConversationID = created.id
                sessionID = created.id
                conversationID = created.id
                conversations.insert(created, at: 0)
            }

            messages.append(ConversationMessage(id: UUID().uuidString, conversationID: conversationID, role: "user", content: question, sources: [], traceID: nil, spanID: nil, createdAt: nil))
            prompt = ""
            let response = try await api.query(token: token, prompt: question, sessionID: sessionID, model: selectedModel, backend: selectedBackend, userID: userID, conversationID: conversationID)
            lastResponse = response
            messages.append(ConversationMessage(id: UUID().uuidString, conversationID: conversationID, role: "assistant", content: response.answer, sources: response.sources, traceID: response.traceID, spanID: response.spanID, createdAt: nil))
            await refreshConversations(token: token)
        } catch {
            errorMessage = error.localizedDescription
            if prompt.isEmpty { prompt = question }
        }
    }

    private func loadModels() async {
        do {
            let response = try await api.models(backend: selectedBackend)
            availableModels = response.models.isEmpty ? ["gpt-4.1-mini"] : response.models
            if !availableModels.contains(selectedModel) { selectedModel = response.defaultModel }
        } catch {
            availableModels = ["gpt-4.1-mini"]
            selectedModel = availableModels[0]
            errorMessage = "Could not load models; using gpt-4.1-mini. \(error.localizedDescription)"
        }
    }

    private func refreshConversations(token: String) async {
        do { conversations = try await api.listConversations(token: token) }
        catch { errorMessage = error.localizedDescription }
    }
}

struct AuthenticatedRootView: View {
    let api: APIClientProtocol
    let config: AppConfig

    var body: some View {
        TabView {
            ChatView(api: api)
                .tabItem { Label("Chat", systemImage: "bubble.left.and.bubble.right") }
            ErrorLabView(api: api)
                .tabItem { Label("Errors", systemImage: "exclamationmark.triangle") }
            InfoView(config: config)
                .tabItem { Label("Info", systemImage: "person.crop.circle") }
        }
        .tint(Color.infraAdvisorPurple)
    }
}

private struct IntentionalDemoError: LocalizedError {
    var errorDescription: String? { "Intentional handled error created by the Error Lab." }
}

/// LLDB intercepts fatal signals before Datadog or iOS can process them, which makes the
/// simulator look frozen instead of terminated. Detect that state so the demo can explain
/// the correct debugger-free launch procedure instead of creating a misleading hang.
private enum DebuggerDetector {
    static var isAttached: Bool {
        var processInfo = kinfo_proc()
        var query = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        var size = MemoryLayout<kinfo_proc>.stride
        guard sysctl(&query, u_int(query.count), &processInfo, &size, nil, 0) == 0 else { return false }
        return (processInfo.kp_proc.p_flag & P_TRACED) != 0
    }
}

struct ErrorLabView: View {
    @EnvironmentObject private var session: SessionStore
    let api: APIClientProtocol
    @State private var status = "Choose a signal to generate."
    @State private var isCallingAPI = false
    @State private var confirmsCrash = false
    @State private var showsDebuggerInstructions = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("Generate safe, intentional telemetry and then inspect it in Datadog. No credentials, prompts, or payload bodies are attached to these events.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                Section("Handled mobile error") {
                    Button("Record handled error") {
                        let error = IntentionalDemoError()
                        RUMMonitor.shared().addError(error: error, attributes: ["demo.intentional": true, "demo.error.kind": "handled"])
                        DemoLogReporter.handledError(error)
                        status = "Handled RUM error and correlated error log recorded."
                    }
                }
                Section("API response error") {
                    Button(isCallingAPI ? "Requesting…" : "Request missing route") {
                        guard let token = session.login?.token else { return }
                        isCallingAPI = true
                        Task {
                            do {
                                try await api.simulateAPIError(token: token)
                                status = "The route unexpectedly succeeded."
                            } catch let error as APIClientError {
                                if case let .http(code, _) = error { DemoLogReporter.apiFailure(status: code) }
                                else { DemoLogReporter.apiFailure(status: nil) }
                                status = "Expected API failure captured: \(error.localizedDescription)"
                            } catch {
                                DemoLogReporter.apiFailure(status: nil)
                                status = "Expected transport failure captured: \(error.localizedDescription)"
                            }
                            isCallingAPI = false
                        }
                    }
                    .disabled(isCallingAPI)
                }
                Section("Logs") {
                    Button("Send info, warning, and error logs") {
                        DemoLogReporter.sendExamples()
                        status = "Three correlated sample logs queued for upload."
                    }
                }
#if DEBUG
                Section("App crash") {
                    Button("Trigger test crash", role: .destructive) {
                        if DebuggerDetector.isAttached { showsDebuggerInstructions = true }
                        else { confirmsCrash = true }
                    }
                    Text("First build the app, press Stop in Xcode, and launch Infra Advisor directly from the Simulator home screen. After the crash closes the app, tap its icon again to return to Login and upload the report.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
#endif
                Section("Latest result") { Text(status).textSelection(.enabled) }
            }
            .navigationTitle("Error Lab")
            .confirmationDialog("Crash the demo app?", isPresented: $confirmsCrash, titleVisibility: .visible) {
                Button("Crash now", role: .destructive) { fatalError("Intentional Infra Advisor iOS demo crash") }
                Button("Cancel", role: .cancel) {}
            } message: {
                Text("Unsaved in-memory session state will be lost. This control is available only in Debug builds.")
            }
            .alert("Xcode debugger is attached", isPresented: $showsDebuggerInstructions) {
                Button("OK", role: .cancel) {}
            } message: {
                Text("Xcode pauses on fatalError, which looks like a frozen or blank app. Press Stop in Xcode, launch Infra Advisor from the Simulator home screen, sign in again, and then trigger the crash.")
            }
        }
        .trackRUMView(name: "Error Lab")
    }
}

struct ChatView: View {
    @EnvironmentObject private var session: SessionStore
    @StateObject private var model: ChatViewModel
    @State private var suggestionsExpanded = false

    init(api: APIClientProtocol) { _model = StateObject(wrappedValue: ChatViewModel(api: api)) }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                ScrollView {
                    LazyVStack(spacing: 16) {
                        controls
                        suggestions
                        history
                        if let error = model.errorMessage { Text(error).foregroundStyle(.red).font(.footnote).frame(maxWidth: .infinity, alignment: .leading) }
                        if let result = model.lastResponse { metadata(result) }
                    }
                    .padding()
                }
                .scrollDismissesKeyboard(.interactively)

                Divider()
                composer
                    .padding(.horizontal)
                    .padding(.vertical, 12)
                    .background(.ultraThinMaterial)
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle("Infra Advisor")
            .toolbar { ToolbarItem(placement: .topBarTrailing) { Button("Logout", action: session.signOut) } }
        }
        .task {
            guard let token = session.login?.token else { return }
            await model.load(token: token)
        }
        .trackRUMView(name: "Chat")
    }

    private var controls: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("Conversation", systemImage: "clock.arrow.circlepath")
                    .font(.headline)
                Spacer()
                Button {
                    model.newConversation()
                } label: {
                    Label("New", systemImage: "plus")
                }
                .buttonStyle(.bordered)
                .disabled(model.isLoading)
            }

            Picker("Conversation", selection: Binding(
                get: { model.selectedConversationID },
                set: { value in
                    guard let token = session.login?.token else { return }
                    Task { await model.selectConversation(value, token: token) }
                }
            )) {
                Text("New conversation").tag(Optional<String>.none)
                ForEach(model.conversations) { conversation in
                    Text("\(conversation.title) · \(conversation.backend?.displayName ?? "Python")").tag(Optional(conversation.id))
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 12)
            .frame(minHeight: 48)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 10))

            VStack(alignment: .leading, spacing: 6) {
                Text("Backend").font(.caption).foregroundStyle(.secondary)
                Picker("Backend", selection: Binding(
                    get: { model.selectedBackend },
                    set: { backend in Task { await model.changeBackend(backend) } }
                )) {
                    ForEach(Backend.allCases) { Text($0.displayName).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .disabled(model.selectedConversationID != nil || model.isLoading)
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Model").font(.caption).foregroundStyle(.secondary)
                Picker("Model", selection: $model.selectedModel) {
                    ForEach(model.availableModels, id: \.self) { Text($0).tag($0) }
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 12)
                .frame(minHeight: 44)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 10))
                .disabled(model.isLoading)
            }
        }
        .padding(16)
        .background(.white, in: RoundedRectangle(cornerRadius: 12))
    }

    private var suggestions: some View {
        DisclosureGroup(isExpanded: $suggestionsExpanded) {
            VStack(spacing: 8) {
                ForEach(ChatViewModel.suggestions, id: \.self) { suggestion in
                    Button {
                        model.chooseSuggestion(suggestion)
                        suggestionsExpanded = false
                    } label: {
                        Text(suggestion)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                        .buttonStyle(.bordered)
                        .disabled(model.isLoading)
                }
            }
            .padding(.top, 10)
        } label: {
            Label("Prompt suggestions", systemImage: "sparkles")
                .font(.headline)
        }
        .tint(Color.infraAdvisorPurple)
        .padding(16)
        .background(.white, in: RoundedRectangle(cornerRadius: 12))
    }

    private var history: some View {
        LazyVStack(spacing: 10) {
            if model.messages.isEmpty {
                Text("Choose a prompt suggestion or enter a question below to start a chat.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120, alignment: .topLeading)
            }
            ForEach(model.messages) { message in MessageCard(message: message) }
            if model.isLoading { HStack { ProgressView(); Text("Consulting the agent…") }.frame(maxWidth: .infinity, alignment: .leading) }
        }
        .frame(maxWidth: .infinity)
    }

    private var composer: some View {
        VStack(spacing: 8) {
            TextEditor(text: $model.prompt)
                .frame(minHeight: 88, maxHeight: 130)
                .padding(8)
                .background(.white, in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.infraAdvisorPurple.opacity(0.45)))
            Button("Ask Infra Advisor") {
                guard let login = session.login else { return }
                Task { await model.submit(token: login.token, userID: login.user.id) }
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.infraAdvisorPurple)
            .frame(maxWidth: .infinity)
            .disabled(model.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.isLoading)
        }
    }

    private func metadata(_ result: QueryResponse) -> some View {
        Text("Trace \(result.traceID ?? "unavailable") · Session \(result.sessionID) · \(result.model)")
            .font(.caption.monospaced())
            .foregroundStyle(.secondary)
            .lineLimit(2)
    }
}

private struct MessageCard: View {
    let message: ConversationMessage

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(message.role == "user" ? "You" : "Infra Advisor").font(.caption.bold()).foregroundStyle(Color.infraAdvisorPurple)
            Text(message.content).textSelection(.enabled)
            if !message.sources.isEmpty {
                Divider()
                Text("Sources: \(message.sources.joined(separator: ", "))").font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(message.role == "user" ? Color.blue.opacity(0.08) : Color.infraAdvisorSurface, in: RoundedRectangle(cornerRadius: 12))
    }
}

struct InfoView: View {
    @EnvironmentObject private var session: SessionStore
    let config: AppConfig

    var body: some View {
        NavigationStack {
            Form {
                if let user = session.login?.user {
                    Section("Profile") {
                        LabeledContent("Email", value: user.email)
                        LabeledContent("User ID", value: user.id)
                        LabeledContent("Admin", value: user.isAdmin ? "Yes" : "No")
                    }
                }
                Section("Datadog") {
                    LabeledContent("Site", value: config.datadogSite.uppercased())
                    LabeledContent("Environment", value: config.environment)
                    LabeledContent("Service", value: config.service)
                    LabeledContent("RUM application", value: config.rumApplicationID)
                    LabeledContent("RUM sampling", value: "100%")
                    LabeledContent("Replay sampling", value: "100%")
                    LabeledContent("Replay privacy", value: "Mask sensitive inputs")
                    LabeledContent("Trace sampling", value: "\(Int(config.traceSampleRate))%")
                    LabeledContent("Logs", value: "Enabled · 100% sampling")
                    LabeledContent("Crash reporting", value: "Enabled")
                    LabeledContent("Crash symbols", value: "Release dSYM upload")
                }
                Section("API") { LabeledContent("Base URL", value: config.apiBaseURL.absoluteString) }
                Section { Button("Logout", role: .destructive, action: session.signOut) }
            }
            .navigationTitle("Info")
        }
        .trackRUMView(name: "Info")
    }
}
