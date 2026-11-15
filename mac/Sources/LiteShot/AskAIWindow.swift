import AppKit
import LiteShotCore
import SwiftUI

/// Ask AI: a chat about one screenshot, through SyncNode → OpenRouter with the model chosen in Settings. Every message can
/// be copied, the latest question edited (↑ in an empty box does it too) and the latest answer asked for again; while it's
/// thinking, Ask becomes Stop.
@MainActor
final class AskAIChat: ObservableObject {
    struct Message: Identifiable {
        let id = UUID()
        let mine: Bool
        let text: String
        var error = false
        /// For a question: the model answered it, so it's part of the conversation the model remembers.
        var answered = false
    }

    @Published var messages: [Message] = []
    @Published var input = ""
    @Published var busy = false
    @Published var status: String
    @Published var copied: UUID?
    let image: CGImage
    private let conversation: AskAIConversation
    private let client: SyncNodeClient
    private let key: () -> String?
    private var task: Task<Void, Never>?

    init(image: CGImage, client: SyncNodeClient = SyncNodeClient(), key: @escaping () -> String? = { Keychain.syncNodeKey }) {
        self.image = image
        self.client = client
        self.key = key
        conversation = AskAIConversation(imageURI: AICatalog.dataURI(image, maxSide: 1600))
        status = "\(AICatalog.chatLabel(LiteShot.Settings.aiChatModel)) through SyncNode · change it in Settings"
    }

    /// The index of the latest question, if there is one.
    var lastQuestion: Int? { messages.lastIndex { $0.mine } }

    func ask(_ text: String) {
        let question = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, !busy else { return }
        guard let apiKey = key() else {
            status = "Add your SyncNode API key in Settings first."
            return
        }
        input = ""
        let asked = Message(mine: true, text: question)
        messages.append(asked)
        busy = true
        let model = LiteShot.Settings.aiChatModel
        task = Task {
            do {
                let answer = try await conversation.ask(question) { try await self.client.chat(apiKey: apiKey, model: model, messages: $0) }
                try Task.checkCancellation()
                if let i = messages.firstIndex(where: { $0.id == asked.id }) { messages[i].answered = true }
                messages.append(Message(mine: false, text: AskAIConversation.plain(answer)))
                status = "\(AICatalog.chatLabel(model)) through SyncNode · each message costs $0.02 plus the model's price"
            } catch let error where Task.isCancelled || error is CancellationError || (error as? URLError)?.code == .cancelled {
                // Stopped: the question comes off the chat and goes back in the box.
                if let i = messages.firstIndex(where: { $0.id == asked.id }) { removeFrom(i) }
                input = question
                status = "Stopped. Your question is back in the box to change or send again."
            } catch {
                messages.append(Message(mine: false, text: SyncNodeClient.friendly(error), error: true))
            }
            busy = false
            task = nil
        }
    }

    /// Stops waiting for an answer.
    func stop() { task?.cancel() }

    /// Takes the last question back into the box to change it; its answer is forgotten.
    func editLast() {
        guard !busy, let i = lastQuestion else { return }
        input = messages[i].text
        removeFrom(i)
        status = "Change your question and press Return to send it again."
    }

    /// Asks the last question again, for a fresh answer.
    func retryLast() {
        guard !busy, let i = lastQuestion else { return }
        let question = messages[i].text
        removeFrom(i)
        ask(question)
    }

    func copy(_ m: Message) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(m.text, forType: .string)
        copied = m.id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.4) { [weak self] in if self?.copied == m.id { self?.copied = nil } }
    }

    /// Takes messages from `index` on off the chat and out of what the model remembers.
    private func removeFrom(_ index: Int) {
        conversation.rewind(to: messages[..<index].filter { $0.mine && $0.answered }.count)
        messages.removeSubrange(index...)
    }

    /// For the self-test: a conversation to draw, with no network.
    func show(_ question: String, answer: String) {
        messages.append(Message(mine: true, text: question, answered: true))
        messages.append(Message(mine: false, text: AskAIConversation.plain(answer)))
    }

    static func open(_ image: CGImage) {
        guard Keychain.hasKey else {
            SettingsView.show()
            HUD.show("Add your SyncNode API key to ask AI")
            return
        }
        let chat = AskAIChat(image: image)
        let window = Windows.show("Ask AI", size: CGSize(width: 640, height: 760)) { AskAIView(chat: chat) }
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { _ in
            MainActor.assumeIsolated {
                chat.stop()
                ImageViewer.close(image)
            }
        }
    }
}

struct AskAIView: View {
    @ObservedObject var chat: AskAIChat
    @State private var hoverPicture = false

    var body: some View {
        VStack(spacing: 0) {
            picture
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(Array(chat.messages.enumerated()), id: \.element.id) { i, m in bubble(m, at: i).id(m.id) }
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
            if chat.messages.isEmpty && !chat.busy {
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
                    .disabled(chat.busy)
                    .onSubmit { chat.ask(chat.input) }
                    // Like most chats: ↑ in an empty box takes the last question back to edit it.
                    .onKeyPress(.upArrow) {
                        guard chat.input.isEmpty, !chat.busy, chat.lastQuestion != nil else { return .ignored }
                        chat.editLast()
                        return .handled
                    }
                if chat.busy {
                    Button("Stop") { chat.stop() }
                        .buttonStyle(.bordered).controlSize(.large)
                        .keyboardShortcut(.cancelAction)
                } else {
                    Button("Ask") { chat.ask(chat.input) }
                        .buttonStyle(.borderedProminent).controlSize(.large)
                        .disabled(chat.input.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .padding(.horizontal, 16)
            Text(chat.status)
                .font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 16).padding(.vertical, 10)
        }
        .frame(minWidth: 520, minHeight: 560)
    }

    /// The screenshot, as big as fits (more of the window before the first question), with "Click to zoom".
    private var picture: some View {
        let scale = NSScreen.main?.backingScaleFactor ?? 2
        return Image(nsImage: NSImage(cgImage: chat.image, size: .zero))
            .resizable().interpolation(.high).scaledToFit()
            // Small selections get up to twice their size, never blown up further.
            .frame(maxWidth: CGFloat(chat.image.width) * 2 / scale, maxHeight: chat.messages.isEmpty ? 420 : 240)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(hoverPicture ? Color.accentColor : Color.primary.opacity(0.12), lineWidth: hoverPicture ? 2 : 1))
            .overlay(alignment: .bottomTrailing) {
                Label("Click to zoom", systemImage: "magnifyingglass")
                    .font(.callout.weight(.semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(Capsule().fill(Color.black.opacity(hoverPicture ? 0.85 : 0.65)))
                    .padding(8)
            }
            .contentShape(Rectangle())
            .onTapGesture { ImageViewer.show(chat.image, title: "Ask AI screenshot") }
            .onHover { inside in
                hoverPicture = inside
                if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
            }
            .help("Click to see the screenshot big and zoom in")
            .accessibilityAddTraits(.isButton)
            .accessibilityLabel("Your screenshot. Click to zoom in.")
            .padding(16)
            .animation(.easeInOut(duration: 0.2), value: chat.messages.isEmpty)
    }

    private func bubble(_ m: AskAIChat.Message, at index: Int) -> some View {
        let last = !chat.busy && (index == chat.lastQuestion || (index > (chat.lastQuestion ?? .max) && index == chat.messages.count - 1))
        return VStack(alignment: m.mine ? .trailing : .leading, spacing: 4) {
            HStack {
                if m.mine { Spacer(minLength: 60) }
                Text(m.text)
                    .textSelection(.enabled)
                    .padding(12)
                    .background(RoundedRectangle(cornerRadius: 14).fill(
                        m.mine ? Color.accentColor.opacity(0.18) : m.error ? Color.red.opacity(0.12) : Color(nsColor: .controlBackgroundColor)))
                    .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.primary.opacity(m.mine ? 0 : 0.08)))
                    .contextMenu {
                        Button("Copy") { chat.copy(m) }
                        if last && m.mine { Button("Edit") { chat.editLast() } }
                        if last && !m.mine { Button(m.error ? "Try Again" : "Retry") { chat.retryLast() } }
                    }
                if !m.mine { Spacer(minLength: 60) }
            }
            HStack(spacing: 14) {
                if !m.error { Button(chat.copied == m.id ? "Copied ✓" : "Copy") { chat.copy(m) } }
                if last && m.mine { Button("Edit") { chat.editLast() } }
                if last && !m.mine { Button(m.error ? "Try again" : "Retry") { chat.retryLast() } }
            }
            .buttonStyle(.link)
            .font(.callout)
            .padding(.horizontal, 6)
        }
        .frame(maxWidth: .infinity, alignment: m.mine ? .trailing : .leading)
    }
}
