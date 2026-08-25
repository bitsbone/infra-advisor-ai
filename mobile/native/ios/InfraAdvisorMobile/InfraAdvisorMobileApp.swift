import SwiftUI
import DatadogCore
import DatadogCrashReporting
import DatadogLogs
import DatadogRUM
import DatadogSessionReplay
import DatadogTrace

@main
struct InfraAdvisorMobileApp: App {
    private let api: APIClient
    private let config: AppConfig
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
        // Crash Reporting persists native crash data on-device and sends it after the next launch.
        // Symbol files are uploaded separately during authorized Release device builds; an API key
        // is never part of the application configuration or binary.
        CrashReporting.enable()
        // Logs are enabled separately from Core. The logger below keeps RUM and trace
        // correlation on, samples every demo log, and never receives user-entered data.
        Logs.enable()
        DemoLogReporter.appStarted()
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
        // Record every demo RUM session. SwiftUI support is explicitly enabled because
        // Session Replay treats it as an opt-in feature in Datadog SDK 3.x.
        SessionReplay.enable(
            with: SessionReplay.Configuration(
                replaySampleRate: 100,
                textAndInputPrivacyLevel: .maskSensitiveInputs,
                featureFlags: [.swiftui: true]
            )
        )
        // Bundling traces with RUM is what makes a resource pivot to its mobile span and the
        // continued backend trace in Datadog.
        Trace.enable(with: Trace.Configuration(sampleRate: config.traceSampleRate, bundleWithRumEnabled: true, networkInfoEnabled: true))

        // RUM's urlSessionTracking defines what to collect; this call activates swizzling for
        // the concrete delegate used below. Both steps are required by Datadog SDK 3.x.
        URLSessionInstrumentation.enable(
            with: .init(delegateClass: InfraAdvisorURLSessionDelegate.self)
        )
        let networkSession = URLSession(
            configuration: .default,
            delegate: InfraAdvisorURLSessionDelegate(),
            delegateQueue: nil
        )
        let api = APIClient(baseURL: config.apiBaseURL, session: networkSession)
        self.api = api
        self.config = config
        _session = StateObject(wrappedValue: SessionStore(api: api))
    }

    var body: some Scene {
        WindowGroup {
            Group {
                if session.login == nil { LoginView() } else { AuthenticatedRootView(api: api, config: config) }
            }
            .environmentObject(session)
        }
    }
}

/// A deliberately small logging facade that documents the privacy boundary for this demo.
/// Callers pass only fixed event names and safe diagnostic attributes—never credentials,
/// identity fields, prompts, Authorization headers, or API payloads.
enum DemoLogReporter {
    private static let logger = Logger.create(
        with: Logger.Configuration(
            name: "infra-advisor-demo",
            networkInfoEnabled: true,
            bundleWithRumEnabled: true,
            bundleWithTraceEnabled: true,
            remoteSampleRate: 100,
            remoteLogThreshold: .info
        )
    )

    static func appStarted() {
        logger.info("Infra Advisor mobile observability initialized", attributes: common("app_started"))
    }

    static func sendExamples() {
        logger.info("Intentional demo information log", attributes: common("sample_info"))
        logger.warn("Intentional demo warning log", attributes: common("sample_warning"))
        logger.error("Intentional demo error log", attributes: common("sample_error"))
    }

    static func handledError(_ error: Error) {
        logger.error("Intentional handled mobile error", error: error, attributes: common("handled_mobile_error"))
    }

    static func apiFailure(status: Int?) {
        var attributes = common("expected_api_failure")
        if let status { attributes["http.status_code"] = status }
        logger.warn("Intentional API response error observed", attributes: attributes)
    }

    private static func common(_ signal: String) -> [String: any Encodable] {
        ["demo.signal": signal, "demo.platform": "ios", "demo.intentional": true]
    }
}
