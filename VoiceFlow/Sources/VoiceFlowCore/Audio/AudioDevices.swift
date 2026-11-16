import CoreAudio
import Foundation

public struct AudioInputDevice: Identifiable, Hashable, Sendable {
    public let id: AudioDeviceID
    public let uid: String
    public let name: String

    public init(id: AudioDeviceID = 0, uid: String, name: String) {
        self.id = id
        self.uid = uid
        self.name = name
    }
}

/// Input device listing and the system output mute used while dictating.
public enum AudioDevices {
    public static func inputDevices() -> [AudioInputDevice] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard inputChannelCount(id) > 0, let uid = stringProperty(id, kAudioDevicePropertyDeviceUID),
                  let name = stringProperty(id, kAudioObjectPropertyName) else { return nil }
            return AudioInputDevice(id: id, uid: uid, name: name)
        }
    }

    /// The system's default input and output device names (for the debug report).
    public static func defaultInputName() -> String? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr, id != 0 else { return nil }
        return stringProperty(id, kAudioObjectPropertyName)
    }

    public static func defaultOutputName() -> String? {
        defaultOutputDevice().flatMap { stringProperty($0, kAudioObjectPropertyName) }
    }

    static func uid(of id: AudioObjectID) -> String? { stringProperty(id, kAudioDevicePropertyDeviceUID) }

    /// The default devices as numbers, for diagnostics. No names: device names often contain a person's name.
    public struct Snapshot: Sendable, Codable, Equatable {
        public var defaultInputID: UInt32 = 0
        public var defaultOutputID: UInt32 = 0
        public var inputSampleRate = 0.0
        public var outputSampleRate = 0.0
        /// Core Audio transport type, as its four-character code ("bltn", "grup" for an aggregate, "blue" for Bluetooth).
        public var inputTransport = ""
        public var outputTransport = ""
        public var deviceCount = 0
        public init() {}
    }

    public static func snapshot() -> Snapshot {
        var s = Snapshot()
        s.defaultInputID = defaultDevice(kAudioHardwarePropertyDefaultInputDevice) ?? 0
        s.defaultOutputID = defaultDevice(kAudioHardwarePropertyDefaultOutputDevice) ?? 0
        s.inputSampleRate = nominalRate(s.defaultInputID)
        s.outputSampleRate = nominalRate(s.defaultOutputID)
        s.inputTransport = transport(s.defaultInputID)
        s.outputTransport = transport(s.defaultOutputID)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        if AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr {
            s.deviceCount = Int(size) / MemoryLayout<AudioDeviceID>.size
        }
        return s
    }

    static func defaultDevice(_ selector: AudioObjectPropertySelector) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr, id != 0 else { return nil }
        return id
    }

    static func nominalRate(_ id: AudioDeviceID) -> Double {
        guard id != 0 else { return 0 }
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var rate = Float64(0)
        var size = UInt32(MemoryLayout<Float64>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &rate) == noErr ? rate : 0
    }

    static func transport(_ id: AudioDeviceID) -> String {
        guard id != 0 else { return "" }
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyTransportType, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var value = UInt32(0)
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return "" }
        let bytes = [24, 16, 8, 0].map { UInt8((value >> $0) & 0xff) }
        return String(decoding: bytes, as: UTF8.self).trimmingCharacters(in: .whitespaces)
    }

    /// True while any app is using the default microphone (a call is likely in progress).
    public static func defaultInputInUse() -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr, id != 0 else { return false }
        var running: UInt32 = 0
        size = UInt32(MemoryLayout<UInt32>.size)
        address.mSelector = kAudioDevicePropertyDeviceIsRunningSomewhere
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &running) == noErr else { return false }
        return running != 0
    }

    /// Whether a process other than VoiceFlow is recording from any input, such as a call app holding the microphone.
    /// Nil before macOS 14.2, where Core Audio does not report it per process.
    public static func anotherAppIsRecording() -> Bool? {
        guard #available(macOS 14.2, *) else { return nil }
        let system = AudioObjectID(kAudioObjectSystemObject)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return nil }
        var processes = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &processes) == noErr else { return nil }
        let me = ProcessInfo.processInfo.processIdentifier
        return processes.contains { process in
            var pid = pid_t(0)
            var pidSize = UInt32(MemoryLayout<pid_t>.size)
            var a = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyPID, mScope: kAudioObjectPropertyScopeGlobal,
                                               mElement: kAudioObjectPropertyElementMain)
            guard AudioObjectGetPropertyData(process, &a, 0, nil, &pidSize, &pid) == noErr, pid != me else { return false }
            var running = UInt32(0)
            var runningSize = UInt32(MemoryLayout<UInt32>.size)
            a.mSelector = kAudioProcessPropertyIsRunningInput
            return AudioObjectGetPropertyData(process, &a, 0, nil, &runningSize, &running) == noErr && running != 0
        }
    }

    public static func device(uid: String) -> AudioInputDevice? {
        inputDevices().first { $0.uid == uid }
    }

    private static func inputChannelCount(_ id: AudioDeviceID) -> Int {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    private static func stringProperty(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    // MARK: Output mute (other audio is muted while you dictate, then restored)

    static func defaultOutputDevice() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr, id != 0 else { return nil }
        return id
    }

    static func outputMuted(_ id: AudioDeviceID) -> Bool? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyMute, mScope: kAudioDevicePropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
        var muted: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &muted) == noErr else { return nil }
        return muted != 0
    }

    @discardableResult
    static func setOutputMuted(_ id: AudioDeviceID, _ muted: Bool) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyMute, mScope: kAudioDevicePropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = muted ? 1 : 0
        return AudioObjectSetPropertyData(id, &address, 0, nil, UInt32(MemoryLayout<UInt32>.size), &value) == noErr
    }
}

/// Mutes system output during a dictation and restores the previous state afterwards.
/// Leaves output alone if it was already muted, so it never unmutes something the user muted.
@MainActor
public final class OutputMuter {
    private var mutedDevice: AudioDeviceID?
    /// Set while VoiceFlow holds the output muted, so a crash cannot leave the Mac silent.
    static let pendingKey = "outputMutedByVoiceFlow"

    public init() {}

    public func mute() {
        guard mutedDevice == nil, let id = AudioDevices.defaultOutputDevice(),
              AudioDevices.outputMuted(id) == false, AudioDevices.setOutputMuted(id, true) else { return }
        mutedDevice = id
        UserDefaults.standard.set(Int(id), forKey: Self.pendingKey)
    }

    public func restore() {
        guard let id = mutedDevice else { return }
        AudioDevices.setOutputMuted(id, false)
        mutedDevice = nil
        UserDefaults.standard.removeObject(forKey: Self.pendingKey)
    }

    /// Call at launch: unmutes output if the previous run quit while it was muted.
    public static func recoverAfterCrash() {
        guard let stored = UserDefaults.standard.object(forKey: pendingKey) as? Int else { return }
        AudioDevices.setOutputMuted(AudioDeviceID(stored), false)
        UserDefaults.standard.removeObject(forKey: pendingKey)
    }
}
