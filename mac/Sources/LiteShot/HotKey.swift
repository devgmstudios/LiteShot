import Carbon.HIToolbox

/// A system-wide shortcut through Carbon's hot key API, which needs no Accessibility permission.
final class HotKey {
    private static var action: (() -> Void)?
    private var ref: EventHotKeyRef?

    /// ⌘⇧9 by default, Lightshot's Mac shortcut, so the muscle memory carries over.
    init(keyCode: UInt32 = UInt32(kVK_ANSI_9), modifiers: UInt32 = UInt32(cmdKey | shiftKey), action: @escaping () -> Void) {
        HotKey.action = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotKey.action?() }
            return noErr
        }, 1, &spec, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x4C53_4854), id: 1) // "LSHT"
        RegisterEventHotKey(keyCode, modifiers, id, GetApplicationEventTarget(), 0, &ref)
    }

    deinit {
        if let ref { UnregisterEventHotKey(ref) }
    }
}
