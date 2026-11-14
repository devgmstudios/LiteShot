import Foundation
import LiteShotCore

/// The few choices LiteShot remembers, in the app's user defaults.
enum Settings {
    private static let defaults = UserDefaults.standard

    /// Solid black toolbars with bigger white icons, and yellow for what's turned on.
    static var brightMode: Bool {
        get { defaults.bool(forKey: "BrightMode") }
        set { defaults.set(newValue, forKey: "BrightMode") }
    }

    /// A short name on every toolbar button.
    static var buttonNames: Bool {
        get { defaults.bool(forKey: "ButtonNames") }
        set { defaults.set(newValue, forKey: "ButtonNames") }
    }

    /// Hide personal info on every capture until it's switched off (H in the editor).
    static var hidePersonalInfo: Bool {
        get { defaults.bool(forKey: "HidePersonalInfo") }
        set { defaults.set(newValue, forKey: "HidePersonalInfo") }
    }

    /// The OpenRouter model Ask AI talks to; any OpenRouter ID that reads images.
    static var aiChatModel: String {
        get { defaults.string(forKey: "AiChatModel").flatMap { $0.trimmingCharacters(in: .whitespaces).isEmpty ? nil : $0 } ?? AICatalog.defaultChatModel }
        set { defaults.set(newValue, forKey: "AiChatModel") }
    }

    static var aiEditModel: AIModel {
        let id = defaults.string(forKey: "AiEditModel")
        return AICatalog.editModels.first { $0.id == id } ?? AICatalog.editModels[0]
    }

    static var aiVideoModel: AIModel {
        let id = defaults.string(forKey: "AiVideoModel")
        return AICatalog.videoModels.first { $0.id == id } ?? AICatalog.videoModels[0]
    }

    static var aiVideoDuration: Int { defaults.integer(forKey: "AiVideoDuration") >= 10 ? 10 : 5 }

    static var keywords: [String] {
        get { defaults.stringArray(forKey: "Keywords") ?? [] }
        set { defaults.set(newValue, forKey: "Keywords") }
    }
}
