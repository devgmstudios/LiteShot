import Foundation

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

    static var keywords: [String] {
        get { defaults.stringArray(forKey: "Keywords") ?? [] }
        set { defaults.set(newValue, forKey: "Keywords") }
    }
}
