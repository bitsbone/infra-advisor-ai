import Foundation

struct AppConfig {
    let apiBaseURL: URL
    let datadogSite: String
    let environment: String
    let service: String
    let rumApplicationID: String
    let clientToken: String
    let traceSampleRate: Float

    var firstPartyHost: String { apiBaseURL.host ?? "" }

    static func load(bundle: Bundle = .main) -> AppConfig {
        func required(_ key: String) -> String {
            guard let value = bundle.object(forInfoDictionaryKey: key) as? String, !value.isEmpty else {
                fatalError("Missing required build setting: \(key)")
            }
            return value
        }

        let baseURLString = required("API_BASE_URL")
        guard let baseURL = URL(string: baseURLString) else {
            fatalError("Invalid API_BASE_URL: \(baseURLString)")
        }

        let site = required("DD_SITE").lowercased()
        guard ["us1", "us3", "us5", "eu1", "ap1", "ap2"].contains(site) else {
            fatalError("Unsupported DD_SITE")
        }

        return AppConfig(
            apiBaseURL: baseURL,
            datadogSite: site,
            environment: required("DD_ENV"),
            service: required("DD_SERVICE"),
            rumApplicationID: required("DD_RUM_APPLICATION_ID"),
            clientToken: required("DD_CLIENT_TOKEN"),
            traceSampleRate: Float(required("DD_TRACE_SAMPLE_RATE")) ?? 100
        )
    }
}
