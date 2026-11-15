import AppKit

/// A chat model on OpenRouter that reads images, or a Replicate model for edits and videos.
public struct AIModel: Hashable, Identifiable {
    public let id: String
    public let label: String
    public let note: String
    /// Replicate only: the name of the image input, whether it takes a list, and the model's extra settings.
    public let imageField: String
    public let imageAsArray: Bool
    public let supportsDuration: Bool
    public let extras: [String: AnyHashable]

    public init(_ id: String, _ label: String, _ note: String = "", imageField: String = "image", imageAsArray: Bool = false,
                supportsDuration: Bool = false, extras: [String: AnyHashable] = [:]) {
        self.id = id
        self.label = label
        self.note = note
        self.imageField = imageField
        self.imageAsArray = imageAsArray
        self.supportsDuration = supportsDuration
        self.extras = extras
    }
}

/// The models and words the AI features offer, the same as the Windows app's.
public enum AICatalog {
    public static let defaultChatModel = "anthropic/claude-sonnet-5.5"

    public static let chatModels: [AIModel] = [
        AIModel(defaultChatModel, "Claude Sonnet 5.5", "Anthropic · sharp and careful, the default"),
        AIModel("anthropic/claude-haiku-5.5", "Claude Haiku 5.5", "Anthropic · fast and inexpensive"),
        AIModel("anthropic/claude-fable-5.2", "Claude Fable 5.2", "Anthropic · the most capable, costs more"),
        AIModel("openai/gpt-6.1-terra", "GPT-6.1 Terra", "OpenAI"),
        AIModel("google/gemini-4-hydrogen", "Gemini 4 Hydrogen", "Google"),
        AIModel("x-ai/grok-4.7", "Grok 4.7", "xAI"),
    ]

    public static let editModels: [AIModel] = [
        AIModel("black-forest-labs/flux-kontext-pro", "FLUX Kontext Pro", "Precise edits", imageField: "input_image",
                extras: ["aspect_ratio": "match_input_image", "output_format": "png", "safety_tolerance": 2]),
        AIModel("google/nano-banana", "Nano Banana", "Bigger changes", imageField: "image_input", imageAsArray: true, extras: ["output_format": "png"]),
    ]

    public static let videoModels: [AIModel] = [
        AIModel("bytedance/seedance-1-lite", "Seedance 1 Lite", imageField: "image", supportsDuration: true, extras: ["resolution": "720p"]),
        AIModel("kwaivgi/kling-v2.1", "Kling 2.1", imageField: "start_image", supportsDuration: true, extras: ["mode": "standard"]),
        AIModel("wan-video/wan-2.2-i2v-fast", "Wan 2.2 Fast", imageField: "image"),
    ]

    public static let askSuggestions = [
        "What's in this screenshot?", "Summarize the text", "Explain this error and how to fix it", "Translate it to English",
        "Write a reply to this", "What should I do next?",
    ]
    public static let editSuggestions = [
        "Remove the background", "Blur faces and names", "Turn it into a hand-drawn sketch", "Make it dark mode", "Upscale and sharpen",
        "Translate the text to Spanish",
    ]
    public static let videoSuggestions = ["Slow cinematic zoom in", "Gentle parallax pan", "Bring it to life with subtle motion", "Animate the cursor clicking"]
    public static let defaultVideoPrompt = "Bring this image to life with subtle, natural motion and a slow camera push-in"

    public static func chatLabel(_ id: String) -> String { chatModels.first { $0.id == id }?.label ?? id }

    /// The screenshot as a JPEG data URI, scaled down to `maxSide`, so it travels inside the request and is never posted publicly.
    public static func dataURI(_ image: CGImage, maxSide: CGFloat) -> String {
        let scale = min(1, maxSide / CGFloat(max(image.width, image.height)))
        let w = max(16, Int(CGFloat(image.width) * scale)), h = max(16, Int(CGFloat(image.height) * scale))
        // 32-bit RGB with the alpha byte skipped: a format every Core Graphics context accepts. White under any transparency.
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return "" }
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let flat = ctx.makeImage(),
              let jpeg = NSBitmapImageRep(cgImage: flat).representation(using: .jpeg, properties: [.compressionFactor: 0.92]) else { return "" }
        return "data:image/jpeg;base64," + jpeg.base64EncodedString()
    }

    /// A Replicate model's input: the prompt, the screenshot in the field the model expects, and its extras.
    public static func input(for model: AIModel, imageURI: String, prompt: String, duration: Int? = nil) -> [String: Any] {
        var input: [String: Any] = ["prompt": prompt]
        input[model.imageField] = model.imageAsArray ? [imageURI] : imageURI
        for (k, v) in model.extras { input[k] = v.base }
        if model.supportsDuration, let duration { input["duration"] = duration >= 10 ? 10 : 5 }
        return input
    }
}

/// Ask AI: a conversation about one screenshot. The picture goes with the first question only; a failed turn leaves no trace.
public final class AskAIConversation {
    static let systemPrompt =
        "You are the assistant inside LiteShot, a screenshot app. The user took a screenshot and is asking about it. " +
        "Answer from what the screenshot shows; say so when something can't be seen or read. Be clear and concise. " +
        "Write plain text with short paragraphs or simple '-' lists; don't use tables or headings."

    let imageURI: String
    public private(set) var messages: [[String: Any]] = [["role": "system", "content": AskAIConversation.systemPrompt]]

    public init(imageURI: String) { self.imageURI = imageURI }

    /// Asks one question with the conversation so far, through `send`, and keeps the turn only if it answered.
    public func ask(_ question: String, send: ([[String: Any]]) async throws -> String) async throws -> String {
        let user: [String: Any] = messages.count == 1
            ? ["role": "user", "content": [["type": "text", "text": question], ["type": "image_url", "image_url": ["url": imageURI]]]]
            : ["role": "user", "content": question]
        let answer = try await send(messages + [user])
        messages.append(user)
        messages.append(["role": "assistant", "content": answer])
        return answer
    }

    /// Forgets the conversation from question `turn` (0 = the first) on, so it can be edited or asked again. Going back
    /// to the start means the next question carries the screenshot again.
    public func rewind(to turn: Int) {
        var seen = 0
        for i in messages.indices.dropFirst() where messages[i]["role"] as? String == "user" {
            if seen == turn { messages.removeSubrange(i...); return }
            seen += 1
        }
    }

    /// Models answer in Markdown; the chat shows plain text, so the marks come off and bullets become dots.
    public static func plain(_ markdown: String) -> String {
        markdown.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).compactMap { raw -> String? in
            let line = String(raw).replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
            var t = line.drop { $0 == " " }
            let indent = line.count - t.count
            if t.hasPrefix("```") { return nil }
            if t.hasPrefix("#") { t = t.drop { $0 == "#" }.drop { $0 == " " } }
            var s = String(t)
            if s.hasPrefix("- ") || s.hasPrefix("* ") || s.hasPrefix("+ ") { s = "• " + s.dropFirst(2) }
            s = s.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "__", with: "").replacingOccurrences(of: "`", with: "")
            return String(repeating: " ", count: min(indent, 6)) + s
        }.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
