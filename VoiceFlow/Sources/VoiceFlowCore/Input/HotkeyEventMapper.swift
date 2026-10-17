import Foundation

/// Maps raw keyboard events (type, key code, modifier flags) to `GestureRecognizer` events and
/// shortcut commands. Kept free of CoreGraphics types so it can be tested with plain numbers.
public struct HotkeyEventMapper: Sendable {
    public enum RawKind: Sendable {
        case keyDown
        case flagsChanged
    }

    /// Shortcuts that act on the last result instead of starting a dictation.
    public enum Shortcut: Equatable, Sendable {
        case pasteLast   // ⌘⌃V
        case copyLast    // ⌘⌃C
        case scratchpad  // ⌥S
    }

    // Virtual key codes (Carbon `kVK_*`).
    static let fnKey: UInt16 = 63
    static let rightOptionKey: UInt16 = 61
    static let rightCommandKey: UInt16 = 54
    static let leftControlKey: UInt16 = 59
    static let rightControlKey: UInt16 = 62
    static let escapeKey: UInt16 = 53
    static let spaceKey: UInt16 = 49
    static let backspaceKey: UInt16 = 51
    static let vKey: UInt16 = 9
    static let cKey: UInt16 = 8
    static let sKey: UInt16 = 1

    // Modifier flag bits (`CGEventFlags` / IOKit `NX_*` masks).
    static let secondaryFnMask: UInt64 = 0x80_0000      // maskSecondaryFn
    static let controlMask: UInt64 = 0x4_0000           // maskControl
    static let optionMask: UInt64 = 0x8_0000            // maskAlternate
    static let commandMask: UInt64 = 0x10_0000          // maskCommand
    static let shiftMask: UInt64 = 0x2_0000             // maskShift
    static let rightOptionDeviceMask: UInt64 = 0x40     // NX_DEVICERALTKEYMASK
    static let rightCommandDeviceMask: UInt64 = 0x10    // NX_DEVICERCMDKEYMASK

    public let hotkey: Hotkey

    public init(hotkey: Hotkey) {
        self.hotkey = hotkey
    }

    var hotkeyCode: UInt16 {
        switch hotkey {
        case .fn: Self.fnKey
        case .rightOption: Self.rightOptionKey
        case .rightCommand: Self.rightCommandKey
        }
    }

    /// Whether the hotkey is down according to these flags.
    func hotkeyIsDown(_ flags: UInt64) -> Bool {
        switch hotkey {
        case .fn: flags & Self.secondaryFnMask != 0
        case .rightOption: flags & Self.rightOptionDeviceMask != 0
        case .rightCommand: flags & Self.rightCommandDeviceMask != 0
        }
    }

    /// True for the hotkey's own modifier events. These are swallowed for Fn so macOS does not also
    /// open the emoji picker or start Apple dictation.
    public func isHotkeyModifierEvent(kind: RawKind, keyCode: UInt16) -> Bool {
        kind == .flagsChanged && keyCode == hotkeyCode
    }

    public func map(kind: RawKind, keyCode: UInt16, flags: UInt64, at time: TimeInterval) -> GestureRecognizer.Event? {
        switch kind {
        case .flagsChanged:
            if keyCode == hotkeyCode {
                return hotkeyIsDown(flags)
                    ? .hotkeyDown(at: time, controlHeld: flags & Self.controlMask != 0)
                    : .hotkeyUp(at: time)
            }
            if (keyCode == Self.leftControlKey || keyCode == Self.rightControlKey), flags & Self.controlMask != 0 {
                return .controlDown(at: time)
            }
            return nil
        case .keyDown:
            switch keyCode {
            case Self.escapeKey: return .escape(at: time)
            case Self.spaceKey: return .space(at: time)
            case Self.backspaceKey: return .backspace(at: time)
            default: return .otherKey(at: time)
            }
        }
    }

    /// ⌘⌃V, ⌘⌃C (no Option or Shift) and ⌥S (Option alone).
    public func shortcut(keyCode: UInt16, flags: UInt64) -> Shortcut? {
        if keyCode == Self.sKey, flags & Self.optionMask != 0,
           flags & (Self.commandMask | Self.controlMask | Self.shiftMask) == 0 {
            return .scratchpad
        }
        let wanted = Self.commandMask | Self.controlMask
        let others = Self.optionMask | Self.shiftMask
        guard flags & wanted == wanted, flags & others == 0 else { return nil }
        switch keyCode {
        case Self.vKey: return .pasteLast
        case Self.cKey: return .copyLast
        default: return nil
        }
    }
}
