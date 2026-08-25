import SwiftUI
import DatadogRUM

struct LoginView: View {
    @EnvironmentObject private var session: SessionStore
    @State private var email = ""
    @State private var password = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Infra Advisor AI") {
                    Text("Sign in to associate this RUM session with a backend user.").foregroundStyle(.secondary)
                    TextField("Email", text: $email)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.emailAddress)
                        .textContentType(.username)
                    SecureField("Password", text: $password).textContentType(.password)
                }
                if let error = session.errorMessage {
                    Section { Text(error).foregroundStyle(.red) }
                }
                Button {
                    Task { await session.signIn(email: email, password: password) }
                } label: {
                    HStack {
                        Spacer()
                        if session.isLoading { ProgressView() } else { Text("Sign in") }
                        Spacer()
                    }
                }
                .disabled(email.isEmpty || password.isEmpty || session.isLoading)
            }
            .navigationTitle("Login")
        }
        .tint(Color.infraAdvisorPurple)
        .trackRUMView(name: "Login")
    }
}
