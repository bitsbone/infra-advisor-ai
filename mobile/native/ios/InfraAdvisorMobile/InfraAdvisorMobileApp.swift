import SwiftUI
import DatadogCore
import DatadogRUM
import DatadogTrace

@main
struct InfraAdvisorMobileApp: App {
    private let api: APIClient
    @StateObject private var session: SessionStore

    init() {
        let config = AppConfig.load()

        // Core owns credentials and intake routing. A client token is intentionally used here:
        // it is safe to ship in a mobile binary, unlike a Datadog API or application key.
        let coreConfiguration: Datadog.Configuration
        // Constructing in each branch lets Swift infer the SDK's site type without making
        // the app import DatadogInternal. The bundle supplies app version automatically.
        switch config.datadogSite {
        case "us1": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .us1, service: config.service)
        case "us3": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .us3, service: config.service)
        case "us5": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .us5, service: config.service)
        case "eu1": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .eu1, service: config.service)
        case "ap1": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .ap1, service: config.service)
        case "ap2": coreConfiguration = .init(clientToken: config.clientToken, env: config.environment, site: .ap2, service: config.service)
        default: preconditionFailure("AppConfig validates DD_SITE")
        }
        Datadog.initialize(
            with: coreConfiguration,
            trackingConsent: .granted
        )
#if DEBUG
        Datadog.verbosityLevel = .debug
#endif
        // Automatic URLSession instrumentation creates RUM resources and injects trace
        // context only for the explicitly trusted first-party host.
        RUM.enable(
            with: RUM.Configuration(
                applicationID: config.rumApplicationID,
                sessionSampleRate: 100,
                swiftUIViewsPredicate: DefaultSwiftUIRUMViewsPredicate(),
                swiftUIActionsPredicate: DefaultSwiftUIRUMActionsPredicate(isLegacyDetectionEnabled: true),
                urlSessionTracking: RUM.Configuration.URLSessionTracking(
                    firstPartyHostsTracing: .trace(hosts: [config.firstPartyHost], sampleRate: config.traceSampleRate)
                )
            )
        )
        // Bundling traces with RUM is what makes a resource pivot to its mobile span and the
        // continued backend trace in Datadog.
        Trace.enable(with: Trace.Configuration(sampleRate: config.traceSampleRate, bundleWithRumEnabled: true, networkInfoEnabled: true))

        let api = APIClient(baseURL: config.apiBaseURL)
        self.api = api
        _session = StateObject(wrappedValue: SessionStore(api: api))
    }

    var body: some Scene {
        WindowGroup {
            Group {
                if session.login == nil { LoginView() } else { ChatView(api: api) }
            }
            .environmentObject(session)
        }
    }
}
