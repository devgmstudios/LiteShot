import Foundation

public struct SyncNodeError: LocalizedError, Equatable {
    public let message: String
    public let status: Int
    public init(_ message: String, status: Int = 0) {
        self.message = message
        self.status = status
    }
    public var errorDescription: String? { message }
}

/// SyncNode (run.syncnode.ai): one key that routes to Replicate (edits, videos) and OpenRouter (chat) with the user's
/// own provider keys. Chat answers at once; generation is submitted, then polled. The same calls as the Windows app.
public final class SyncNodeClient {
    /// SyncNode's address; tests point it at a stand-in so they never spend real credit.
    public static var base = URL(string: "https://run.syncnode.ai")!
    let session: URLSession

    public init(session: URLSession = .shared) { self.session = session }

    /// One chat turn: the whole conversation goes up, the model's reply comes back.
    public func chat(apiKey: String, model: String, messages: [[String: Any]]) async throws -> String {
        let json = try await post("chat-completion", ["apiKey": apiKey, "model": model, "messages": messages, "max_tokens": 1500])
        if let answer = SyncNodeClient.answer(in: json) { return answer }
        if let prediction = (json as? [String: Any])?["prediction"], let failed = SyncNodeClient.errorText(prediction, onlyErrorFields: true) {
            throw SyncNodeError(failed)
        }
        throw SyncNodeError("The model didn't answer. Try again, or pick another model in Settings.")
    }

    /// Starts a Replicate model run and returns SyncNode's job id.
    public func submit(apiKey: String, model: String, input: [String: Any]) async throws -> String {
        let json = try await post("generate", ["apiKey": apiKey, "model": model, "input": input])
        guard let id = (json as? [String: Any])?["job_id"] else { throw SyncNodeError("SyncNode didn't return a job id.") }
        return id as? String ?? "\(id)"
    }

    /// Polls a job every 2–4 s until it succeeds, and returns the first output URL (or data URI).
    public func wait(apiKey: String, job: String, timeout: TimeInterval, progress: @escaping (String) -> Void) async throws -> String {
        let deadline = Date().addingTimeInterval(timeout)
        var delay: UInt64 = 2_000_000_000
        while true {
            try Task.checkCancellation()
            var parts = URLComponents(url: SyncNodeClient.base.appendingPathComponent("prediction-status"), resolvingAgainstBaseURL: false)!
            parts.queryItems = [URLQueryItem(name: "apiKey", value: apiKey), URLQueryItem(name: "job_id", value: job)]
            let json = try await get(parts.url!)
            let root = json as? [String: Any] ?? [:]
            let status = ((root["replicate_status"] as? String) ?? (root["status"] as? String) ?? "").lowercased()
            switch status {
            case "starting": progress("Starting the model…")
            case "processing": progress("Generating…")
            case "", "in_progress": progress("Queued…")
            default: progress(status)
            }
            if status == "succeeded" || status == "completed" {
                if let url = SyncNodeClient.firstURL(root["output"]) { return url }
                throw SyncNodeError("The model finished but returned no file.")
            }
            if ["failed", "canceled", "cancelled"].contains(status) {
                throw SyncNodeError(SyncNodeClient.errorText(root) ?? "The model run failed.")
            }
            if Date() > deadline { throw SyncNodeError("The model is taking too long. Try again in a moment.") }
            try await Task.sleep(nanoseconds: delay)
            delay = min(4_000_000_000, delay + 500_000_000)
        }
    }

    public func balance(apiKey: String) async throws -> Double? {
        var parts = URLComponents(url: SyncNodeClient.base.appendingPathComponent("balance"), resolvingAgainstBaseURL: false)!
        parts.queryItems = [URLQueryItem(name: "apiKey", value: apiKey)]
        let value = (try await get(parts.url!) as? [String: Any])?["balance"]
        if let n = value as? NSNumber { return n.doubleValue }
        if let s = value as? String { return Double(s) }
        return nil
    }

    public func download(_ url: String) async throws -> Data {
        if url.hasPrefix("data:"), let comma = url.firstIndex(of: ",") {
            return Data(base64Encoded: String(url[url.index(after: comma)...])) ?? Data()
        }
        guard let u = URL(string: url) else { throw SyncNodeError("The model returned an address that can't be opened.") }
        return try await session.data(from: u).0
    }

    // MARK: - plumbing

    private func post(_ path: String, _ body: [String: Any]) async throws -> Any {
        var request = URLRequest(url: SyncNodeClient.base.appendingPathComponent(path))
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("LiteShot-Mac/1.0", forHTTPHeaderField: "User-Agent")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await read(session.data(for: request))
    }

    private func get(_ url: URL) async throws -> Any {
        var request = URLRequest(url: url)
        request.timeoutInterval = 60
        request.setValue("LiteShot-Mac/1.0", forHTTPHeaderField: "User-Agent")
        return try await read(session.data(for: request))
    }

    private func read(_ result: (Data, URLResponse)) throws -> Any {
        let (data, response) = result
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        let json = try? JSONSerialization.jsonObject(with: data)
        let error = json.flatMap { SyncNodeClient.errorText($0, onlyErrorFields: true) }
        if (200..<300).contains(status), let json, error == nil { return json }
        let text = String(data: data, encoding: .utf8) ?? ""
        throw SyncNodeError(error ?? (text.count > 0 && text.count < 300 ? text : "SyncNode answered HTTP \(status)."), status: status)
    }

    /// The first choice's text in an OpenRouter reply, looking inside SyncNode's wrapper:
    /// {"prediction": {"output": {"choices": [{"message": {"content": …}}]}}}. Output may also be a JSON string.
    public static func answer(in json: Any, depth: Int = 0) -> String? {
        guard depth <= 3 else { return nil }
        if let s = json as? String, s.trimmingCharacters(in: .whitespaces).hasPrefix("{"),
           let data = s.data(using: .utf8), let inner = try? JSONSerialization.jsonObject(with: data) {
            return answer(in: inner, depth: depth + 1)
        }
        guard let object = json as? [String: Any] else { return nil }
        if let choices = object["choices"] as? [[String: Any]], let message = choices.first?["message"] as? [String: Any] {
            let content = message["content"]
            let text: String
            if let s = content as? String { text = s }
            else if let parts = content as? [[String: Any]] { text = parts.compactMap { $0["text"] as? String }.joined() }
            else { text = "" }
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        for name in ["prediction", "output", "data", "result"] {
            if let child = object[name], let found = answer(in: child, depth: depth + 1) { return found }
        }
        return nil
    }

    static func errorText(_ json: Any, onlyErrorFields: Bool = false) -> String? {
        guard let object = json as? [String: Any] else { return nil }
        if let s = object["error"] as? String, !s.trimmingCharacters(in: .whitespaces).isEmpty { return s }
        if let e = object["error"] as? [String: Any] { return (e["message"] as? String) ?? "\(e)" }
        if onlyErrorFields { return nil }
        return (object["message"] as? String) ?? (object["detail"] as? String) ?? (object["output"] as? String)
    }

    /// Outputs come as a URL, a list of URLs, a JSON string of either, or an object holding one.
    static func firstURL(_ value: Any?) -> String? {
        switch value {
        case let s as String:
            if s.hasPrefix("http") || s.hasPrefix("data:") { return s }
            if s.hasPrefix("[") || s.hasPrefix("{"), let data = s.data(using: .utf8), let inner = try? JSONSerialization.jsonObject(with: data) {
                return firstURL(inner)
            }
            return nil
        case let list as [Any]:
            return list.lazy.compactMap { firstURL($0) }.first
        case let object as [String: Any]:
            for name in ["url", "video", "image", "output", "uri"] { if let found = firstURL(object[name]) { return found } }
            return nil
        default:
            return nil
        }
    }

    /// SyncNode's errors as a next step the user can take.
    public static func friendly(_ error: Error) -> String {
        let message = (error as? SyncNodeError)?.message ?? error.localizedDescription
        let status = (error as? SyncNodeError)?.status ?? 0
        let lower = message.lowercased()
        if status == 402 || lower.contains("insufficient balance") { return "Your SyncNode balance is empty. Top up at syncnode.ai/billing." }
        if lower.contains("replicate"), lower.contains("key") { return "Add your Replicate token under Connected Provider Credentials at syncnode.ai/api_keys." }
        if lower.contains("openrouter"), lower.contains("key") { return "Add your OpenRouter key under Connected Provider Credentials at syncnode.ai/api_keys." }
        if lower.contains("not a valid model") || lower.contains("no endpoints found") {
            return "OpenRouter doesn't know that model, or it can't read images. Pick another in Settings."
        }
        if status == 401 || lower.contains("unauthorized") || lower.contains("invalid api key") {
            return "SyncNode didn't accept the API key. Check it in Settings."
        }
        if error is URLError { return "Couldn't reach SyncNode. Check your internet connection." }
        return message
    }
}
