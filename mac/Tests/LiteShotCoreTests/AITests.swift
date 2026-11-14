import AppKit
import XCTest
@testable import LiteShotCore

/// Answers SyncNode's endpoints from memory, the way the real service does (its chat reply wrapped in a prediction),
/// and remembers what was sent. No real key or credit is involved.
final class StubSyncNode: URLProtocol {
    nonisolated(unsafe) static var requests: [[String: Any]] = []
    nonisolated(unsafe) static var polls = 0

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        var body: [String: Any] = [:]
        if let stream = request.httpBodyStream {
            stream.open()
            var data = Data()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable { let n = stream.read(&buffer, maxLength: buffer.count); if n <= 0 { break }; data.append(buffer, count: n) }
            stream.close()
            body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        } else if let data = request.httpBody {
            body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        }
        StubSyncNode.requests.append(body)
        let path = request.url?.path ?? ""
        var status = 200
        var reply: Any
        switch path {
        case "/chat-completion":
            if (body["model"] as? String) == "nobody/no-such-model" {
                status = 404
                reply = ["error": ["message": "No endpoints found for nobody/no-such-model."]] as [String: Any]
            } else {
                let message: [String: Any] = ["role": "assistant", "content": "**Revenue** grew 24%.\n- Customers up 9%"]
                let choice: [String: Any] = ["index": 0, "message": message]
                let openRouter: [String: Any] = ["id": "gen-1", "choices": [choice]]
                let prediction: [String: Any] = ["id": "p1", "uid": "hidden", "status": "completed", "output": openRouter, "error": NSNull()]
                reply = ["prediction": prediction] as [String: Any]
            }
        case "/generate":
            reply = ["success": true, "job_id": "job-7", "status": "in_progress"] as [String: Any]
        case "/prediction-status":
            StubSyncNode.polls += 1
            let done: [String: Any] = ["replicate_status": "succeeded", "output": ["https://example.com/out.png"]]
            reply = StubSyncNode.polls < 2 ? ["replicate_status": "processing"] as [String: Any] : done
        case "/balance":
            reply = ["balance": "4.75"] as [String: Any]
        default:
            status = 404
            reply = ["error": "not found"] as [String: Any]
        }
        let data = try! JSONSerialization.data(withJSONObject: reply)
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    static func client() -> SyncNodeClient {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubSyncNode.self]
        return SyncNodeClient(session: URLSession(configuration: config))
    }
}

final class SyncNodeTests: XCTestCase {
    override func setUp() {
        StubSyncNode.requests = []
        StubSyncNode.polls = 0
    }

    func image() -> CGImage {
        let ctx = CGContext(data: nil, width: 40, height: 30, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(NSColor.white.cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: 40, height: 30))
        return ctx.makeImage()!
    }

    func testChatReadsSyncNodesWrappedReplyAndKeepsTheConversation() async throws {
        let client = StubSyncNode.client()
        let chat = AskAIConversation(imageURI: AICatalog.dataURI(image(), maxSide: 1600))
        let first = try await chat.ask("What's this?") { try await client.chat(apiKey: "sn_test", model: AICatalog.defaultChatModel, messages: $0) }
        XCTAssertEqual(first, "**Revenue** grew 24%.\n- Customers up 9%")
        let sent = try XCTUnwrap(StubSyncNode.requests.last)
        XCTAssertEqual(sent["apiKey"] as? String, "sn_test")
        XCTAssertEqual(sent["model"] as? String, AICatalog.defaultChatModel)
        let messages = try XCTUnwrap(sent["messages"] as? [[String: Any]])
        XCTAssertEqual(messages.count, 2)
        let parts = try XCTUnwrap(messages[1]["content"] as? [[String: Any]])
        XCTAssertTrue(((parts[1]["image_url"] as? [String: Any])?["url"] as? String)?.hasPrefix("data:image/jpeg;base64,") == true)

        _ = try await chat.ask("And customers?") { try await client.chat(apiKey: "sn_test", model: AICatalog.defaultChatModel, messages: $0) }
        let followUp = try XCTUnwrap(StubSyncNode.requests.last?["messages"] as? [[String: Any]])
        XCTAssertEqual(followUp.count, 4, "system, first question with the picture, answer, follow-up")
        XCTAssertTrue(followUp[3]["content"] is String, "the picture goes only once")
    }

    func testAFailedTurnLeavesNoTraceAndSaysWhy() async throws {
        let client = StubSyncNode.client()
        let chat = AskAIConversation(imageURI: "data:image/jpeg;base64,AA==")
        do {
            _ = try await chat.ask("Hi") { try await client.chat(apiKey: "sn_test", model: "nobody/no-such-model", messages: $0) }
            XCTFail("expected an error")
        } catch {
            XCTAssertTrue(SyncNodeClient.friendly(error).contains("doesn't know that model"))
        }
        XCTAssertEqual(chat.messages.count, 1)
        XCTAssertTrue(SyncNodeClient.friendly(SyncNodeError("No OpenRouter API key found", status: 400)).contains("OpenRouter key"))
    }

    func testReplicateRunsAreSubmittedAndPolled() async throws {
        let client = StubSyncNode.client()
        let model = AICatalog.editModels[1]
        let input = AICatalog.input(for: model, imageURI: "data:image/jpeg;base64,AA==", prompt: "Remove the background")
        XCTAssertEqual((input["image_input"] as? [String])?.count, 1, "Nano Banana takes its image in a list")
        let job = try await client.submit(apiKey: "sn_test", model: model.id, input: input)
        XCTAssertEqual(job, "job-7")
        var steps: [String] = []
        let url = try await client.wait(apiKey: "sn_test", job: job, timeout: 30) { steps.append($0) }
        XCTAssertEqual(url, "https://example.com/out.png")
        XCTAssertEqual(steps.first, "Generating…")
        let balance = try await client.balance(apiKey: "sn_test")
        XCTAssertEqual(balance, 4.75)
    }

    func testAnswerIsFoundBareOrWrappedOrAsAString() {
        let bare: [String: Any] = ["choices": [["message": ["content": "hi"]]]]
        XCTAssertEqual(SyncNodeClient.answer(in: bare), "hi")
        XCTAssertEqual(SyncNodeClient.answer(in: ["prediction": ["output": bare]]), "hi")
        let asString = String(data: try! JSONSerialization.data(withJSONObject: bare), encoding: .utf8)!
        XCTAssertEqual(SyncNodeClient.answer(in: ["prediction": ["output": asString]]), "hi")
        XCTAssertNil(SyncNodeClient.answer(in: ["prediction": ["output": NSNull()]]))
    }

    func testMarkdownBecomesPlainText() {
        XCTAssertEqual(AskAIConversation.plain("## Summary\n**Revenue** grew `24%`.\n- one\n* two\n```\ncode\n```"), "Summary\nRevenue grew 24%.\n• one\n• two\ncode")
    }
}
