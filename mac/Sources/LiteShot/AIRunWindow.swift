import AppKit
import LiteShotCore
import SwiftUI

/// Edit with AI and Make a video: describe the change (or the motion), watch it run, then keep the result.
/// Edits can be refined again and again; videos are saved to Movies/LiteShot and shown in Finder.
@MainActor
final class AIRun: ObservableObject {
    enum Kind { case edit, video }
    enum Phase: Equatable {
        case prompt
        case running(String)
        case edited(CGImage)
        case video(URL)
        case failed(String)

        static func == (a: Phase, b: Phase) -> Bool {
            switch (a, b) {
            case (.prompt, .prompt): return true
            case let (.running(x), .running(y)), let (.failed(x), .failed(y)): return x == y
            case let (.edited(x), .edited(y)): return x === y
            case let (.video(x), .video(y)): return x == y
            default: return false
            }
        }
    }

    let kind: Kind
    @Published var phase: Phase = .prompt
    @Published var prompt = ""
    /// What the next edit starts from: the screenshot, then the latest result.
    private(set) var source: CGImage
    let original: CGImage
    let scale: CGFloat
    private var task: Task<Void, Never>?
    private let client = SyncNodeClient()

    init(kind: Kind, image: CGImage, scale: CGFloat) {
        self.kind = kind
        source = image
        original = image
        self.scale = scale
    }

    var model: AIModel { kind == .edit ? LiteShot.Settings.aiEditModel : LiteShot.Settings.aiVideoModel }
    var suggestions: [String] { kind == .edit ? AICatalog.editSuggestions : AICatalog.videoSuggestions }

    func run() {
        let text = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        if kind == .edit, text.isEmpty { return }
        guard let apiKey = Keychain.syncNodeKey else {
            phase = .failed("Add your SyncNode API key in Settings first.")
            return
        }
        let model = self.model, source = self.source, kind = self.kind
        let input = AICatalog.input(for: model, imageURI: AICatalog.dataURI(source, maxSide: kind == .edit ? 1600 : 1280),
                                    prompt: text.isEmpty ? AICatalog.defaultVideoPrompt : text,
                                    duration: kind == .video ? LiteShot.Settings.aiVideoDuration : nil)
        phase = .running("Sending to \(model.label)…")
        task = Task {
            do {
                let job = try await client.submit(apiKey: apiKey, model: model.id, input: input)
                let url = try await client.wait(apiKey: apiKey, job: job, timeout: kind == .edit ? 300 : 900) { step in
                    Task { @MainActor in if case .running = self.phase { self.phase = .running(step) } }
                }
                phase = .running(kind == .edit ? "Downloading the result…" : "Downloading the video…")
                let data = try await client.download(url)
                if kind == .edit {
                    guard let image = NSBitmapImageRep(data: data)?.cgImage else { throw SyncNodeError("The model's picture couldn't be read.") }
                    self.source = image
                    phase = .edited(image)
                } else {
                    phase = .video(try save(video: data, from: url))
                }
            } catch is CancellationError {
                phase = .prompt
            } catch {
                phase = .failed(SyncNodeClient.friendly(error))
            }
        }
    }

    func cancel() { task?.cancel() }

    /// Movies/LiteShot/LiteShot <date>.mp4, copied to the clipboard as a file.
    private func save(video data: Data, from url: String) throws -> URL {
        let folder = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("LiteShot", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let ext = URL(string: url).map { $0.pathExtension }.flatMap { (2...5).contains($0.count) ? $0 : nil } ?? "mp4"
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let file = folder.appendingPathComponent("LiteShot \(stamp.string(from: Date())).\(ext)")
        try data.write(to: file)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([file as NSURL])
        return file
    }

    static func open(_ kind: Kind, image: CGImage, scale: CGFloat) {
        guard Keychain.hasKey else {
            SettingsView.show()
            HUD.show("Add your SyncNode API key to use AI")
            return
        }
        let run = AIRun(kind: kind, image: image, scale: scale)
        Windows.show(kind == .edit ? "Edit with AI" : "Make a video", size: CGSize(width: 620, height: 640)) { AIRunView(run: run) }
    }
}

struct AIRunView: View {
    @ObservedObject var run: AIRun

    var body: some View {
        VStack(spacing: 14) {
            picture
            switch run.phase {
            case .prompt, .edited, .failed:
                if case let .failed(message) = run.phase {
                    Text(message).foregroundStyle(.red).frame(maxWidth: .infinity, alignment: .leading)
                }
                if case .edited = run.phase {
                    HStack {
                        Button("Copy") { if case let .edited(image) = run.phase { ImageActions.copy(image, scale: run.scale); HUD.show("Copied to clipboard") } }
                        Button("Save…") { if case let .edited(image) = run.phase { ImageActions.save(image, scale: run.scale) } }
                        Button("Pin") {
                            if case let .edited(image) = run.phase {
                                let size = CGSize(width: CGFloat(image.width) / run.scale, height: CGFloat(image.height) / run.scale)
                                let screen = NSScreen.main?.visibleFrame ?? .zero
                                PinWindow.open(image, frame: CGRect(x: screen.midX - size.width / 2, y: screen.midY - size.height / 2, width: size.width, height: size.height))
                            }
                        }
                        Spacer()
                        Text("Describe another change to keep going").font(.caption).foregroundStyle(.secondary)
                    }
                }
                promptRow
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), spacing: 8)], spacing: 8) {
                    ForEach(run.suggestions, id: \.self) { s in
                        Button(s) { run.prompt = s }.buttonStyle(.bordered)
                    }
                }
                Text("\(run.model.label) through SyncNode · $0.02 plus the model's price on your Replicate account · change it in Settings")
                    .font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
            case let .running(step):
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text(step)
                    Spacer()
                    Button("Cancel") { run.cancel() }
                }
            case let .video(file):
                VStack(alignment: .leading, spacing: 8) {
                    Text("The video is saved and copied, ready to paste.").font(.headline)
                    Text(file.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    HStack {
                        Button("Play") { NSWorkspace.shared.open(file) }.buttonStyle(.borderedProminent)
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([file]) }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(18)
        .frame(minWidth: 520, minHeight: 520)
    }

    private var picture: some View {
        let shown: CGImage = { if case let .edited(image) = run.phase { return image }; return run.original }()
        return Image(nsImage: NSImage(cgImage: shown, size: .zero))
            .resizable().scaledToFit()
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(alignment: .topLeading) {
                if case .edited = run.phase {
                    Text("Edited").font(.caption.bold()).padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(.ultraThinMaterial)).padding(8)
                }
            }
    }

    private var promptRow: some View {
        HStack(spacing: 8) {
            TextField(run.kind == .edit ? "Describe the change, e.g. “remove the background”" : "Describe the motion (optional)", text: $run.prompt)
                .textFieldStyle(.roundedBorder).controlSize(.large)
                .onSubmit { run.run() }
            Button(run.kind == .edit ? "Generate" : "Make video") { run.run() }
                .buttonStyle(.borderedProminent).controlSize(.large)
                .disabled(run.kind == .edit && run.prompt.trimmingCharacters(in: .whitespaces).isEmpty)
        }
    }
}
