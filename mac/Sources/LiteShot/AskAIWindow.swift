import AppKit
import LiteShotCore
import SwiftUI

/// Ask AI: a chat about one screenshot, through SyncNode → OpenRouter with the model chosen in Settings.
@MainActor
final class AskAIChat: ObservableObject {
    struct Message: Identifiable {
        let id = UUID()
        let mine: Bool
        let text: String
        var error = false
    }

    @Published var messages: [Message] = []
    @Published var input = ""
    @Published var busy = false
    @Published var status: String
    let image: CGImage
    private let conversation: AskAIConversation
    private let client: SyncNodeClient
    private let key: () -> String?

    init(image: CGImage, client: SyncNodeClient = SyncNodeClient(), key: @escaping () -> String? = { Keychain.syncNodeKey }) {
        self.image = image
        self.client = client
        self.key = key
        conversation = AskAIConversation(imageURI: AICatalog.dataURI(image, maxSide: 1600))
        status = "\(AICatalog.chatLabel(LiteShot.Settings.aiChatModel)) through SyncNode · change it in Settings"
    }

    func ask(_ text: String) {
        let question = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, !busy else { return }
        guard let apiKey = key() else {
            status = "Add your SyncNode API key in Settings first."
            return
        }
        input = ""
        messages.append(Message(mine: true, text: question))
        busy = true
        let model = LiteShot.Settings.aiChatModel
        Task {
            do {
                let answer = try await conversation.ask(question) { try await self.client.chat(apiKey: apiKey, model: model, messages: $0) }
                messages.append(Message(mine: false, text: AskAIConversation.plain(answer)))
                status = "\(AICatalog.chatLabel(model)) through SyncNode · each message costs $0.02 plus the model's price"
            } catch {
                messages.append(Message(mine: false, text: SyncNodeClient.friendly(error), error: true))
                input = question
            }
            busy = false
        }
    }

    /// For the self-test: a conversation to draw, with no network.
    func show(_ question: String, answer: String) {
        messages.append(Message(mine: true, text: question))
        messages.append(Message(mine: false, text: AskAIConversation.plain(answer)))
    }

    static func open(_ image: CGImage) {
        guard Keychain.hasKey else {
            SettingsView.show()
            HUD.show("Add your SyncNode API key to ask AI")
            return
        }
        let chat = AskAIChat(image: image)
        Windows.show("Ask AI", size: CGSize(width: 640, height: 720)) { AskAIView(chat: chat) }
    }
}

struct AskAIView: View {
    @ObservedObject var chat: AskAIChat

    var body: some View {
        VStack(spacing: 0) {
            Image(nsImage: NSImage(cgImage: chat.image, size: .zero))
                .resizable().scaledToFit()
                .frame(maxHeight: 190)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
                .padding(16)
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        ForEach(chat.messages) { bubble($0).id($0.id) }
                        if chat.busy {
                            Text("Thinking…").foregroundStyle(.secondary).padding(12).id("thinking")
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.bottom, 8)
                }
                .onChange(of: chat.messages.count) {
                    if let last = chat.messages.last?.id { withAnimation { proxy.scrollTo(last, anchor: .bottom) } }
                }
            }
            if chat.messages.isEmpty {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 190), spacing: 8)], spacing: 8) {
                    ForEach(AICatalog.askSuggestions, id: \.self) { s in
                        Button(s) { chat.ask(s) }.buttonStyle(.bordered).controlSize(.large)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 8)
            }
            HStack(spacing: 8) {
                TextField("Ask anything about this screenshot…", text: $chat.input)
                    .textFieldStyle(.roundedBorder).controlSize(.large)
                    .onSubmit { chat.ask(chat.input) }
                Button("Ask") { chat.ask(chat.input) }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                    .disabled(chat.busy || chat.input.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(.horizontal, 16)
            Text(chat.status)
                .font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 16).padding(.vertical, 10)
        }
        .frame(minWidth: 520, minHeight: 560)
    }

    private func bubble(_ m: AskAIChat.Message) -> some View {
        HStack {
            if m.mine { Spacer(minLength: 60) }
            Text(m.text)
                .textSelection(.enabled)
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 14).fill(
                    m.mine ? Color.accentColor.opacity(0.18) : m.error ? Color.red.opacity(0.12) : Color(nsColor: .controlBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.primary.opacity(m.mine ? 0 : 0.08)))
            if !m.mine { Spacer(minLength: 60) }
        }
    }
}
