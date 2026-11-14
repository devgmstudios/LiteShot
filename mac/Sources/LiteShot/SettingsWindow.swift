import AppKit
import LiteShotCore
import SwiftUI

/// LiteShot's settings: easy to see, privacy, the SyncNode key and the AI models. Opens from the menu bar (⌘,).
struct SettingsView: View {
    @AppStorage("BrightMode") private var bright = false
    @AppStorage("ButtonNames") private var names = false
    @AppStorage("HidePersonalInfo") private var hide = false
    @AppStorage("ShowMagnifier") private var magnifier = false
    @AppStorage("UploadLifetime") private var lifetime = "24h"
    @AppStorage("AiChatModel") private var chatModel = AICatalog.defaultChatModel
    @AppStorage("AiEditModel") private var editModel = AICatalog.editModels[0].id
    @AppStorage("AiVideoModel") private var videoModel = AICatalog.videoModels[0].id
    @AppStorage("AiVideoDuration") private var duration = 5
    @State private var keywords = LiteShot.Settings.keywords.joined(separator: ", ")
    @State private var key = Keychain.syncNodeKey ?? ""
    @State private var keyStatus = Keychain.hasKey ? "Saved in your Keychain." : "No key saved yet."

    private var chatPreset: Binding<String> {
        Binding(get: { AICatalog.chatModels.contains { $0.id == chatModel } ? chatModel : "custom" },
                set: { if $0 != "custom" { chatModel = $0 } })
    }

    var body: some View {
        Form {
            Section("Easy to see") {
                Toggle("Bright mode", isOn: Binding(get: { bright }, set: { bright = $0; if $0 { names = true } }))
                Text("Solid black toolbars with bigger white icons, and yellow for whatever is turned on.").font(.caption).foregroundStyle(.secondary)
                Toggle("Button names", isOn: $names)
            }
            Section("While selecting") {
                Toggle("Magnifier", isOn: $magnifier)
                Text("A pixel loupe with coordinates and the color under the pointer. Press C to copy the color.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Privacy") {
                Toggle("Hide personal info on every capture", isOn: $hide)
                TextField("Keywords to hide", text: $keywords, prompt: Text("e.g. Acme Corp, Project Falcon"))
                    .onChange(of: keywords) {
                        LiteShot.Settings.keywords = keywords.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { $0.count >= 2 }
                    }
            }
            Section("Uploads") {
                Picker("Delete uploaded links after", selection: $lifetime) {
                    ForEach(Fortefile.lifetimes, id: \.key) { Text($0.label).tag($0.key) }
                }
                Text("Uploads go to fortefile.com, encrypted on your Mac first. Delete one sooner from Recent Uploads in the menu bar.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("SyncNode") {
                SecureField("API key", text: $key, prompt: Text("SyncNode API key"))
                    .onChange(of: key) {
                        Keychain.syncNodeKey = key
                        keyStatus = Keychain.hasKey ? "Saved in your Keychain." : "No key saved yet."
                    }
                HStack {
                    Button("Check balance") { check() }.disabled(key.isEmpty)
                    Button("Get a key") { NSWorkspace.shared.open(URL(string: "https://syncnode.ai/api_keys")!) }
                    Spacer()
                    Text(keyStatus).font(.caption).foregroundStyle(.secondary)
                }
                Text("Save your OpenRouter key (Ask AI) and Replicate token (edits, videos) under Connected Provider Credentials on SyncNode too.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Ask AI") {
                Picker("Model", selection: chatPreset) {
                    ForEach(AICatalog.chatModels) { Text($0.label).tag($0.id) }
                    Text("Other OpenRouter model").tag("custom")
                }
                TextField("Model ID", text: $chatModel, prompt: Text(AICatalog.defaultChatModel))
                Text(AICatalog.chatModels.first { $0.id == chatModel }.map { "\($0.note). Any OpenRouter model that reads images works." }
                     ?? "A custom OpenRouter model; it has to read images to see the screenshot.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Edit with AI and video") {
                Picker("Edit model", selection: $editModel) { ForEach(AICatalog.editModels) { Text($0.label).tag($0.id) } }
                Picker("Video model", selection: $videoModel) { ForEach(AICatalog.videoModels) { Text($0.label).tag($0.id) } }
                Picker("Video length", selection: $duration) { Text("5 seconds").tag(5); Text("10 seconds").tag(10) }
                Text("Each Ask AI message, edit or video costs $0.02 on SyncNode plus the model's price on your OpenRouter or Replicate account. The screenshot is sent inside the request; it's never posted publicly.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 520, minHeight: 560)
    }

    private func check() {
        let apiKey = key
        keyStatus = "Checking…"
        Task {
            do {
                let balance = try await SyncNodeClient().balance(apiKey: apiKey)
                keyStatus = balance.map { String(format: "Connected · balance $%.2f", $0) } ?? "Connected."
            } catch {
                keyStatus = SyncNodeClient.friendly(error)
            }
        }
    }

    @MainActor
    static func show() {
        Windows.show("LiteShot Settings", size: CGSize(width: 560, height: 680), id: "settings") { SettingsView() }
    }
}
