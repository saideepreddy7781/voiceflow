import Testing
@testable import VoiceFlowCore

struct GestureTests {
    typealias A = GestureRecognizer.Action

    /// Feeds events and returns all actions produced.
    private func run(_ events: [GestureRecognizer.Event]) -> [A] {
        var r = GestureRecognizer()
        return events.flatMap { r.handle($0) }
    }

    @Test func holdIsPushToTalk() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: false), .hotkeyUp(at: 1.2)])
                == [.begin(.dictation), .finish(.dictation)])
    }

    @Test func recordingStartsOnPress() {
        var r = GestureRecognizer()
        #expect(r.handle(.hotkeyDown(at: 0, controlHeld: false)) == [.begin(.dictation)])
    }

    @Test func singleQuickTapIsCancelledAfterTheWindow() {
        var r = GestureRecognizer()
        _ = r.handle(.hotkeyDown(at: 0, controlHeld: false))
        #expect(r.handle(.hotkeyUp(at: 0.1)) == [])
        #expect(r.deadline == 0.1 + r.doubleTapWindow)
        #expect(r.handle(.tick(at: 0.2)) == [])          // window not over yet
        #expect(r.handle(.tick(at: 0.7)) == [.cancel])
        #expect(!r.isActive)
    }

    @Test func fnSpaceLocksHandsFree() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .space(at: 0.1), .hotkeyUp(at: 0.2),
            .space(at: 3),                                   // typing a space later is ignored
            .hotkeyDown(at: 8, controlHeld: false), .hotkeyUp(at: 8.1),
        ]) == [.begin(.dictation), .lock, .finish(.dictation)])
    }

    @Test func doubleTapLocksAndNextPressFinishes() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 0.3, controlHeld: false), .hotkeyUp(at: 0.4),   // release after lock is ignored
            .hotkeyDown(at: 9.0, controlHeld: false), .hotkeyUp(at: 9.1),
        ]) == [.begin(.dictation), .lock, .finish(.dictation)])
    }

    @Test func secondPressAfterWindowStartsFresh() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 2.0, controlHeld: false),
        ]) == [.begin(.dictation), .cancel, .begin(.dictation)])
    }

    @Test func controlAtPressIsCommandMode() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: true), .hotkeyUp(at: 2)])
                == [.begin(.command), .finish(.command)])
    }

    @Test func controlDuringHoldSwitchesToCommandMode() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: false), .controlDown(at: 0.1), .hotkeyUp(at: 2)])
                == [.begin(.dictation), .becomeCommand, .finish(.command)])
    }

    @Test func commandDoublePressLocksHandsFree() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: true), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 0.4, controlHeld: true),
            .hotkeyDown(at: 6, controlHeld: false),
        ]) == [.begin(.command), .lock, .finish(.command)])
    }

    @Test func backspaceCancelsCommandMode() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: true), .backspace(at: 1)]) == [.begin(.command), .cancel])
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: true), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 0.3, controlHeld: true), .backspace(at: 4),
        ]) == [.begin(.command), .lock, .cancel])
    }

    @Test func backspaceDuringHandsFreeDictationIsTyping() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .space(at: 0.1),
            .backspace(at: 2), .hotkeyDown(at: 5, controlHeld: false),
        ]) == [.begin(.dictation), .lock, .finish(.dictation)])
    }

    @Test func escapeCancelsWhileHolding() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: false), .escape(at: 1), .hotkeyUp(at: 2)])
                == [.begin(.dictation), .cancel])
    }

    @Test func escapeCancelsHandsFree() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 0.3, controlHeld: false), .escape(at: 5),
        ]) == [.begin(.dictation), .lock, .cancel])
    }

    @Test func otherKeyWhileHoldingIsAnFnShortcut() {
        #expect(run([.hotkeyDown(at: 0, controlHeld: false), .otherKey(at: 0.2), .hotkeyUp(at: 0.3)])
                == [.begin(.dictation), .cancel])
    }

    @Test func typingDuringHandsFreeIsIgnored() {
        #expect(run([
            .hotkeyDown(at: 0, controlHeld: false), .hotkeyUp(at: 0.1),
            .hotkeyDown(at: 0.3, controlHeld: false), .otherKey(at: 3),
            .hotkeyDown(at: 6, controlHeld: false),
        ]) == [.begin(.dictation), .lock, .finish(.dictation)])
    }

    @Test func strayReleaseWhileIdleDoesNothing() {
        #expect(run([.hotkeyUp(at: 0), .tick(at: 1), .escape(at: 2), .space(at: 3)]) == [])
    }

    @Test func holdJustOverThresholdFinishes() {
        var r = GestureRecognizer()
        _ = r.handle(.hotkeyDown(at: 0, controlHeld: false))
        #expect(r.handle(.hotkeyUp(at: r.tapThreshold + 0.01)) == [.finish(.dictation)])
    }
}

struct HotkeyMapperTests {
    let fn = HotkeyEventMapper(hotkey: .fn)

    @Test func fnPressAndRelease() {
        #expect(fn.map(kind: .flagsChanged, keyCode: 63, flags: 0x80_0100, at: 1) == .hotkeyDown(at: 1, controlHeld: false))
        #expect(fn.map(kind: .flagsChanged, keyCode: 63, flags: 0x100, at: 2) == .hotkeyUp(at: 2))
        #expect(fn.isHotkeyModifierEvent(kind: .flagsChanged, keyCode: 63))
        #expect(!fn.isHotkeyModifierEvent(kind: .flagsChanged, keyCode: 59))
    }

    @Test func fnWithControlHeld() {
        #expect(fn.map(kind: .flagsChanged, keyCode: 63, flags: 0x80_0000 | 0x4_0000, at: 1)
                == .hotkeyDown(at: 1, controlHeld: true))
    }

    @Test func controlPressAndReleaseWhileFnHeld() {
        #expect(fn.map(kind: .flagsChanged, keyCode: 59, flags: 0x80_0000 | 0x4_0000, at: 1) == .controlDown(at: 1))
        #expect(fn.map(kind: .flagsChanged, keyCode: 59, flags: 0x80_0000, at: 2) == nil)
    }

    @Test func otherModifiersAreIgnored() {
        #expect(fn.map(kind: .flagsChanged, keyCode: 56, flags: 0x2_0000, at: 1) == nil) // Shift
    }

    @Test func keyDowns() {
        #expect(fn.map(kind: .keyDown, keyCode: 53, flags: 0, at: 1) == .escape(at: 1))
        #expect(fn.map(kind: .keyDown, keyCode: 49, flags: 0x80_0000, at: 1) == .space(at: 1))
        #expect(fn.map(kind: .keyDown, keyCode: 51, flags: 0, at: 1) == .backspace(at: 1))
        #expect(fn.map(kind: .keyDown, keyCode: 126, flags: 0x80_0000, at: 1) == .otherKey(at: 1)) // Fn+Up
    }

    @Test func pasteAndCopyLastShortcuts() {
        let cmdCtrl: UInt64 = 0x10_0000 | 0x4_0000
        #expect(fn.shortcut(keyCode: 9, flags: cmdCtrl) == .pasteLast)
        #expect(fn.shortcut(keyCode: 8, flags: cmdCtrl) == .copyLast)
        #expect(fn.shortcut(keyCode: 9, flags: 0x10_0000) == nil)                 // plain ⌘V
        #expect(fn.shortcut(keyCode: 9, flags: cmdCtrl | 0x2_0000) == nil)        // with Shift
        #expect(fn.shortcut(keyCode: 0, flags: cmdCtrl) == nil)                   // ⌘⌃A
    }

    @Test func rightOptionUsesDeviceBitNotGenericOption() {
        let ro = HotkeyEventMapper(hotkey: .rightOption)
        #expect(ro.map(kind: .flagsChanged, keyCode: 61, flags: 0x8_0000 | 0x40, at: 1) == .hotkeyDown(at: 1, controlHeld: false))
        #expect(ro.map(kind: .flagsChanged, keyCode: 61, flags: 0x8_0000 | 0x20, at: 2) == .hotkeyUp(at: 2)) // left Option still held
        #expect(ro.map(kind: .flagsChanged, keyCode: 58, flags: 0x8_0000 | 0x20, at: 3) == nil)              // left Option itself
    }

    @Test func rightCommand() {
        let rc = HotkeyEventMapper(hotkey: .rightCommand)
        #expect(rc.map(kind: .flagsChanged, keyCode: 54, flags: 0x10_0000 | 0x10, at: 1) == .hotkeyDown(at: 1, controlHeld: false))
        #expect(rc.map(kind: .flagsChanged, keyCode: 54, flags: 0, at: 2) == .hotkeyUp(at: 2))
    }
}

struct CleanupPromptTests {
    @Test func userMessageHasAllPartsInOrder() {
        let msg = CleanupPrompt.userMessage(transcript: "hi pria", style: "formal", app: "Mail", names: ["Priya", "Ramesh"])
        #expect(msg == "<app>Mail</app>\n<style>formal</style>\n<names>Priya, Ramesh</names>\n<transcript>hi pria</transcript>")
    }

    @Test func emptyPartsAreLeftOut() {
        #expect(CleanupPrompt.userMessage(transcript: "ok", style: "casual", app: nil, names: [])
                == "<style>casual</style>\n<transcript>ok</transcript>")
    }
}
