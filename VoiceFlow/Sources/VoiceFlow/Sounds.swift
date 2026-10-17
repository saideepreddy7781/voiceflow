import AppKit

/// Short system sounds for start, stop, done and error.
@MainActor
enum Sounds {
    static func start(_ enabled: Bool) { play("Tink", enabled, volume: 0.35) }
    /// No stop sound: the text appearing is the confirmation.
    static func stop(_ enabled: Bool) { _ = enabled }
    static func done(_ enabled: Bool) { _ = enabled }
    static func error(_ enabled: Bool) { play("Funk", enabled, volume: 0.35) }

    private static func play(_ name: String, _ enabled: Bool, volume: Float) {
        guard enabled, let sound = NSSound(named: NSSound.Name(name))?.copy() as? NSSound else { return }
        sound.volume = volume
        sound.play()
    }
}
