import AppKit
import ApplicationServices
import Foundation
import os
import VoiceFlowCore

/// Runs one dictation or command from hotkey press to inserted text.
///
/// Fallback chain, so speech is never lost: cleaned text → raw transcript → clipboard + Flow bar.
///
/// A dictation during a meeting shares the meeting's microphone, so its words are recorded in the meeting
/// too. That keeps one uninterrupted mic stream and is the simplest honest behaviour: what you say out loud
/// during a call is in the call's audio.
@MainActor
final class DictationController {
    private unowned let model: AppModel
    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "dictation")
    private let recorder = AudioRecorder()
    private let muter = OutputMuter()
    private var cutter = SegmentCutter()
    private var segments: [Task<String, Error>] = []
    private var levelTimer: Timer?
    private var segmentTimer: Timer?
    private var startedAt = Date()
    private var kind: SessionKind = .dictation
    private var handsFree = false
    private var context = TypingContext()
    private var contextTask: Task<TypingContext, Never>?
    /// Set once the context is known to be a password field or excluded app: nothing is uploaded.
    private var blockedField = false
    private var whisperPrompt: String?
    private var processing: Task<Void, Never>?
    private var warnedLong = false

    private(set) var lastResult: String?

    /// Long sessions: a warning sound at 19 minutes, automatic finish at 20.
    private let warnAfter: TimeInterval = 19 * 60
    private let stopAfter: TimeInterval = 20 * 60

    init(model: AppModel) {
        self.model = model
    }

    var isRecording: Bool { recorder.isRecording }
    var isBusy: Bool { recorder.isRecording || processing != nil }

    // MARK: Session control

    func begin(_ kind: SessionKind) {
        guard !recorder.isRecording else { return }
        if processing != nil {
            model.pill.show("Still finishing the last dictation…", seconds: 1.5)
            model.resetHotkeySession()
            return
        }
        context = TypingContext()
        guard model.hasGroqKey else {
            fail("Add your Groq key to start dictating.", action: .init(title: "Open Settings") { WindowManager.shared.showSettings(.keys) })
            return
        }
        switch AudioRecorder.microphoneStatus {
        case .authorized: break
        case .notDetermined:
            model.requestMicrophone()
            fail("Allow microphone access, then try again.")
            report(.micFailed, kind: kind)
            return
        default:
            report(.micFailed, kind: kind)
            fail("Microphone access is off.", action: .init(title: "Open Settings") { self.model.openPrivacyPane("Privacy_Microphone") })
            return
        }

        if OutputSafety.secureInputActive() {
            fail("Secure input is on (a password prompt or Secure Keyboard Entry), so VoiceFlow won't listen.")
            report(.blocked, kind: kind)
            return
        }
        let prefs = model.preferences
        do {
            try recorder.start(deviceUID: prefs.inputDeviceUID.isEmpty ? nil : prefs.inputDeviceUID)
        } catch {
            fail("\(error)")
            report(.micFailed, kind: kind)
            return
        }
        self.kind = kind
        handsFree = false
        warnedLong = false
        startedAt = Date()
        cutter = SegmentCutter()
        segments = []
        blockedField = false
        whisperPrompt = NameExtractor.whisperPrompt(dictionary: model.dictionaryTerms, names: [])
        Sounds.start(prefs.playSounds)
        if prefs.muteAudioWhileDictating && !model.notetaker.isRecording {
            // After the start cue has played, so the cue itself is not muted.
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, self.recorder.isRecording, !self.model.notetaker.isRecording else { return }
                self.muter.mute()
            }
        }
        model.pill.set(kind == .command ? .command : .listening)

        // Read the field while the user talks: names on screen sharpen spelling.
        contextTask = Task.detached(priority: .userInitiated) { ContextReader.read() }
        Task { [weak self] in
            guard let self, let ctx = await self.contextTask?.value else { return }
            self.context = ctx
            if ctx.isSecure {
                // Password field or password manager: stop listening and drop everything.
                self.blockedField = true
                self.segments.forEach { $0.cancel() }
                self.segments = []
                if self.recorder.isRecording {
                    self.stopCapture()
                    self.model.resetHotkeySession()
                    self.model.pill.show("VoiceFlow doesn't listen in password fields", seconds: 3)
                    self.report(.blocked)
                }
                return
            }
            let names = NameExtractor.extract(from: [ctx.excerpt, ctx.windowTitle])
            self.whisperPrompt = NameExtractor.whisperPrompt(dictionary: self.model.dictionaryTerms, names: names)
        }

        levelTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tickLevel() }
        }
        segmentTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tickSegments() }
        }
        log.info("Session started: \(String(describing: kind), privacy: .public)")
    }

    func becomeCommand() {
        guard recorder.isRecording else { return }
        kind = .command
        model.pill.set(.command)
    }

    func lock() {
        guard recorder.isRecording else { return }
        handsFree = true
        if kind == .dictation { model.pill.set(.handsFree) }
    }

    func cancel() {
        guard recorder.isRecording else { return }
        let restarts = recorder.restarts
        stopCapture()
        segments.forEach { $0.cancel() }
        segments = []
        model.pill.set(.idle)
        report(.cancelled, audioSeconds: Date().timeIntervalSince(startedAt), restarts: restarts)
        log.info("Session cancelled")
    }

    func finish() {
        guard recorder.isRecording else { return }
        let restarts = recorder.restarts
        let heldSeconds = Date().timeIntervalSince(startedAt)
        let samples = stopCapture()
        let audioSeconds = Double(samples.count) / AudioMath.sampleRate
        let voiced = AudioMath.voicedSeconds(samples)
        let verdict = AudioMath.captureVerdict(samples, heldSeconds: heldSeconds)
        if verdict != .speech {
            segments.forEach { $0.cancel() }
            segments = []
            switch verdict {
            case .tooShort:
                // Too short to be speech: drop it quietly.
                model.pill.set(.idle)
                report(.tooShort, audioSeconds: audioSeconds, restarts: restarts)
            case .noAudio, .digitalSilence:
                Sounds.error(model.preferences.playSounds)
                model.pill.show("Your microphone sent no audio", detail: "Check the input in Settings → General.",
                                action: .init(title: "Open") { WindowManager.shared.showSettings(.general) }, seconds: 6)
                report(.micFailed, audioSeconds: audioSeconds, restarts: restarts)
            default:
                model.pill.show("No speech heard", detail: audioSeconds > 1 ? "Check your microphone in Settings → General." : "", seconds: 1.5)
                report(.noSpeech, audioSeconds: audioSeconds, restarts: restarts)
            }
            return
        }
        let kind = kind
        model.pill.set(.processing)
        let finalRange = cutter.finish(totalSamples: samples.count)

        processing = Task { [weak self] in
            guard let self else { return }
            defer { self.processing = nil }
            let began = Date()
            // The field must be known before the last audio leaves the Mac.
            let context = await self.contextTask?.value ?? self.context
            if context.isSecure || self.blockedField || OutputSafety.secureInputActive() {
                self.segments.forEach { $0.cancel() }
                self.segments = []
                self.model.pill.show("VoiceFlow doesn't type into password fields", seconds: 3)
                self.report(.blocked, audioSeconds: audioSeconds, restarts: restarts)
                return
            }
            if let finalRange { self.startSegment(Array(samples[finalRange])) }
            let tasks = self.segments
            self.segments = []
            do {
                var parts: [String] = []
                for task in tasks { parts.append(try await task.value) }
                let transcript = SegmentCutter.join(parts)
                if HallucinationFilter.isLikelyHallucination(transcript, voicedSeconds: voiced) {
                    self.model.pill.show("No speech heard", seconds: 1.5)
                    self.report(.noSpeech, audioSeconds: audioSeconds, restarts: restarts)
                    return
                }
                switch kind {
                case .dictation:
                    await self.deliverDictation(transcript, context: context, audioSeconds: audioSeconds, began: began, restarts: restarts)
                case .command:
                    await self.runCommand(transcript, context: context, audioSeconds: audioSeconds, began: began, restarts: restarts)
                }
            } catch {
                self.log.error("Transcription failed: \(String(describing: error), privacy: .private)")
                Sounds.error(self.model.preferences.playSounds)
                self.model.pill.show("Transcription failed", detail: "\(error)", seconds: 6)
                self.model.record(HistoryEntry(kind: kind == .command ? .command : .dictation, appName: self.context.appName,
                                               raw: "", final: "", audioSeconds: audioSeconds, latencyMs: 0, outcome: "failed",
                                               reason: .transcriptionFailed))
                self.report(.transcriptionFailed, audioSeconds: audioSeconds, restarts: restarts)
            }
        }
    }

    // MARK: Delivery

    private func deliverDictation(_ transcript: String, context: TypingContext, audioSeconds: Double, began: Date, restarts: Int) async {
        let prefs = model.preferences
        var final: String
        var note: String?
        var dictionaryFixes = 0
        let usage = AppClassifier.usage(bundleID: context.bundleID, windowTitle: context.windowTitle)

        if let snippet = SnippetMatcher.exactMatch(transcript, snippets: model.snippets) {
            final = snippet.expansion
            dictionaryFixes = 1
        } else {
            var cleaned = transcript
            if prefs.cleanupLevel != .none, let key = await model.keyAsync() {
                var files: [String] = []
                if AppClassifier.isCode(bundleID: context.bundleID),
                   let folder = WorkspaceFiles.projectFolder(bundleID: context.bundleID, windowTitle: context.windowTitle,
                                                             documentPath: context.documentPath) {
                    let all = await Task.detached { WorkspaceFiles.files(in: folder) }.value
                    files = WorkspaceFiles.mentioned(in: transcript, files: all)
                }
                // Terminal contents can be anyone's output (a README you cat'ed, a remote server), so none of it is sent.
                let terminal = OutputSafety.isTerminal(bundleID: context.bundleID)
                let screenNames = terminal ? [] : NameExtractor.extract(from: [context.excerpt, context.windowTitle], limit: 20)
                let names = model.dictionaryTerms + screenNames
                let style = AppClassifier.style(bundleID: context.bundleID, windowTitle: context.windowTitle, preferences: prefs)
                let request = CleanupRequest(transcript: transcript, style: style, appName: context.appName,
                                             names: Array(names.prefix(40)), beforeCursor: terminal ? "" : context.beforeCursor,
                                             files: files, tagFiles: AppClassifier.tagsFiles(bundleID: context.bundleID),
                                             level: prefs.cleanupLevel,
                                             telugu: prefs.speaksTelugu ? prefs.teluguScript : nil)
                let outcome = await TextCleaner(chat: GroqChat(apiKey: key), model: prefs.cleanupModel).clean(request)
                cleaned = outcome.text
                note = outcome.note
            }
            let corrected = DictionaryCorrector.apply(cleaned, entries: model.dictionary)
            let expanded = SnippetMatcher.expandInlineCounting(corrected.text, snippets: model.snippets)
            final = expanded.text
            dictionaryFixes = corrected.fixes + expanded.count
        }

        lastResult = final
        let result = await TextInserter.insert(final, context: context)
        let latency = Int(Date().timeIntervalSince(began) * 1000)
        Sounds.done(prefs.playSounds)
        if let note { log.notice("Cleanup skipped: \(note, privacy: .private)") }
        switch result {
        case .accessibility, .pastedVerified:
            model.pill.set(.idle)
            scheduleAutoLearn(inserted: final, context: context)
        case .pastedUnverified:
            offerPaste("Pasted, but couldn't confirm it", text: final, seconds: 3)
            scheduleAutoLearn(inserted: final, context: context)
        case .pasteNotLanded:
            offerPaste("Didn't reach that field", detail: String(final.prefix(140)), text: final, seconds: 6)
        case .copiedOnly:
            offerPaste("No text field. Copied to clipboard", detail: String(final.prefix(140)), text: final, seconds: 5)
        case .focusChanged:
            offerPaste("You switched apps, so it was copied instead", detail: String(final.prefix(140)), text: final, seconds: 5)
        case .blocked:
            model.pill.show("Not typed: password field or secure input is active", seconds: 3)
            report(.blocked, audioSeconds: audioSeconds, latencyMs: latency, restarts: restarts)
            return
        }
        let outcome = switch result {
        case .accessibility: "inserted"
        case .pastedVerified, .pastedUnverified, .pasteNotLanded: "pasted"
        default: "copied"
        }
        report(result.reason, audioSeconds: audioSeconds, latencyMs: latency, restarts: restarts)
        var entry = HistoryEntry(kind: .dictation, appName: context.appName, raw: transcript, final: final,
                                 audioSeconds: audioSeconds, latencyMs: latency, outcome: outcome, reason: result.reason)
        entry.usage = usage
        entry.wordsCorrected = HistoryEntry.wordsChanged(raw: transcript, final: final)
        entry.dictionaryFixes = dictionaryFixes
        model.record(entry)
        log.info("Dictation delivered in \(latency) ms via \(String(describing: result), privacy: .public)")
    }

    private func runCommand(_ instruction: String, context: TypingContext, audioSeconds: Double, began: Date, restarts: Int) async {
        guard let key = await model.keyAsync() else {
            model.pill.show("Add your Groq key to use Command Mode.", seconds: 4)
            return
        }
        if context.isSecure {
            model.pill.show("VoiceFlow doesn't type into password fields", seconds: 3)
            return
        }
        var ctx = context
        if OutputSafety.isTerminal(bundleID: ctx.bundleID) { ctx.fullText = "" }   // scrollback is untrusted
        if ctx.selectedText.isEmpty && !ctx.isSecure { ctx.selectedText = await Self.copySelection() ?? "" }
        let runner = CommandRunner(chat: GroqChat(apiKey: key), model: model.preferences.commandModel)
        do {
            let result = try await runner.run(app: ctx.appName, fullText: ctx.fullText, selectedText: ctx.selectedText, instruction: instruction)
            let latency = Int(Date().timeIntervalSince(began) * 1000)
            var outcome = "inserted"
            var reason: DeliveryReason?
            var output = ""
            switch result {
            case .replace(let text):
                output = text
                let r: InsertResult
                if ctx.hasSelection {
                    r = await TextInserter.insert(text, context: TypingContext.forReplacingSelection(ctx))
                } else if !ctx.fullText.isEmpty {
                    r = await TextInserter.replaceAll(text, context: ctx)
                } else {
                    model.pill.show("Select some text first", detail: "Command Mode edits the selected text or the current field.", seconds: 4)
                    r = .copiedOnly
                    outcome = "failed"
                }
                if outcome != "failed" {
                    outcome = handleCommandInsert(r, text: text)
                    reason = r.reason
                }
            case .insert(let text):
                output = text
                let r = await TextInserter.insert(text, context: ctx)
                outcome = handleCommandInsert(r, text: text)
                reason = r.reason
            case .search(let query):
                output = query
                NSWorkspace.shared.open(CommandPrompt.searchURL(query))
                model.pill.set(.idle)
                outcome = "search"
            case .answer(let text):
                output = text
                model.pill.show("Answer", detail: text, action: .init(title: "Copy") { TextInserter.copyToClipboard(text) }, seconds: 12)
                outcome = "answer"
            }
            lastResult = output
            Sounds.done(model.preferences.playSounds)
            if let reason { report(reason, audioSeconds: audioSeconds, latencyMs: latency, restarts: restarts) }
            model.record(HistoryEntry(kind: .command, appName: ctx.appName, raw: instruction, final: output,
                                      audioSeconds: audioSeconds, latencyMs: latency, outcome: outcome, reason: reason))
        } catch {
            Sounds.error(model.preferences.playSounds)
            model.pill.show("Command failed", detail: "\(error)", seconds: 6)
            report(.transcriptionFailed, audioSeconds: audioSeconds, restarts: restarts)
        }
    }

    /// Reports a Command Mode insertion and returns the history outcome.
    private func handleCommandInsert(_ result: InsertResult, text: String) -> String {
        switch result {
        case .accessibility, .pastedVerified:
            offerUndo()
            return "inserted"
        case .pastedUnverified:
            offerUndo()
            return "pasted"
        case .pasteNotLanded:
            offerPaste("Didn't reach that field", detail: String(text.prefix(140)), text: text, seconds: 6)
            return "copied"
        case .copiedOnly:
            offerPaste("No text field. Copied to clipboard", detail: String(text.prefix(140)), text: text, seconds: 5)
            return "copied"
        case .focusChanged:
            offerPaste("You switched apps, so it was copied instead", detail: String(text.prefix(140)), text: text, seconds: 5)
            return "copied"
        case .blocked:
            model.pill.show("VoiceFlow doesn't type into password fields", seconds: 3)
            return "failed"
        }
    }

    /// Offers one click to type `text` into whatever has focus now.
    private func offerPaste(_ message: String, detail: String = "", text: String, seconds: Double) {
        model.pill.show(message, detail: detail, action: .init(title: "Paste") { self.paste(text) }, seconds: seconds)
    }

    private func paste(_ text: String) {
        model.pill.dismissMessage()
        Task {
            if await !TextInserter.pasteIfAllowed(text) {
                model.pill.show("VoiceFlow doesn't type into password fields", seconds: 3)
            }
        }
    }

    private func offerUndo() {
        model.pill.show("Done", action: .init(title: "Undo") {
            TextInserterUndo.send()
            self.model.pill.set(.idle)
        }, seconds: 6)
    }

    /// Called when the app quits: never leave the Mac muted or the microphone running.
    func shutdown() {
        if recorder.isRecording { stopCapture() }
        muter.restore()
    }

    /// A meeting has started while this dictation was muting the Mac: the call's audio must be heard.
    func unmuteForMeeting() {
        muter.restore()
    }

    // MARK: Shortcuts

    func pasteLast() {
        guard let text = lastResult ?? model.history.first(where: { !$0.final.isEmpty })?.final else {
            model.pill.show("Nothing to paste yet", seconds: 1.5)
            return
        }
        paste(text)
    }

    func copyLast() {
        guard let text = lastResult ?? model.history.first(where: { !$0.final.isEmpty })?.final else {
            model.pill.show("Nothing to copy yet", seconds: 1.5)
            return
        }
        TextInserter.copyToClipboard(text)
        model.pill.show("Copied last transcript", seconds: 1.5)
    }

    /// Clicking the idle Flow bar starts a hands-free dictation.
    func startHandsFreeFromBar() {
        begin(.dictation)
        guard recorder.isRecording else { return }
        lock()
        model.enterHandsFreeGesture()
    }

    // MARK: Capture internals

    @discardableResult
    private func stopCapture() -> [Float] {
        levelTimer?.invalidate()
        segmentTimer?.invalidate()
        levelTimer = nil
        segmentTimer = nil
        let samples = recorder.stop()
        muter.restore()
        Sounds.stop(model.preferences.playSounds)
        return samples
    }

    private func tickLevel() {
        model.pill.push(level: recorder.currentLevel())
        let elapsed = Date().timeIntervalSince(startedAt)
        if elapsed > stopAfter {
            finish()
            model.resetHotkeySession()
        } else if elapsed > warnAfter && !warnedLong {
            warnedLong = true
            Sounds.error(model.preferences.playSounds)
        }
    }

    /// Cuts and uploads finished segments while the user is still talking.
    private func tickSegments() {
        // Only the open segment is copied, not the whole (up to 20-minute) recording.
        let start = cutter.openStart
        let tail = recorder.samples(from: start)
        if let range = cutter.nextCut(inTail: tail, startingAt: start) {
            startSegment(Array(tail[(range.lowerBound - start)..<(range.upperBound - start)]))
        }
    }

    private func startSegment(_ samples: [Float]) {
        guard !blockedField else { return }
        let prefs = model.preferences
        let audio = prefs.whisperMode ? AudioMath.normalized(samples) : samples
        let language = prefs.whisperLanguage
        let prompt = whisperPrompt
        let model = self.model
        segments.append(Task.detached(priority: .userInitiated) {
            if AudioMath.isSilent(audio) { return "" }
            guard let key = await model.keyAsync() else { throw ProviderError.missingKey }
            let transcriber = GroqTranscriber(apiKey: key, model: prefs.transcriptionModel)
            return try await transcriber.transcribeResolving(wav: AudioMath.wav(audio), language: language,
                                                             parallelLanguage: prefs.parallelLanguage,
                                                             languages: prefs.languages, prompt: prompt).text
        })
    }

    /// Adds this session to the debug log. Metadata only: nothing said, typed or shown on screen.
    private func report(_ reason: DeliveryReason, kind: SessionKind? = nil, audioSeconds: Double = 0, latencyMs: Int = 0, restarts: Int = 0) {
        let bundleID = context.bundleID.isEmpty ? (NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "") : context.bundleID
        model.diagnostics.record(DiagnosticEvent(session: (kind ?? self.kind) == .command ? .command : .dictation, reason: reason, bundleID: bundleID,
                                                 audioSeconds: audioSeconds, latencyMs: latencyMs, micRestarts: restarts,
                                                 duringMeeting: model.notetaker.isRecording))
    }

    private func fail(_ message: String, action: PillState.Action? = nil) {
        Sounds.error(model.preferences.playSounds)
        model.pill.show(message, action: action, seconds: 5)
        model.resetHotkeySession()
    }

    // MARK: Auto-learn

    private func scheduleAutoLearn(inserted: String, context: TypingContext) {
        guard model.preferences.autoLearnWords, !context.isSecure, !OutputSafety.isTerminal(bundleID: context.bundleID),
              let element = ContextReader.focusedElement() else { return }
        let before = context.fullText
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(15))
            guard let self, let after = ContextReader.value(of: element) else { return }
            let known = Set(self.model.dictionaryTerms.map { $0.lowercased() })
            let terms = AutoLearner.learnedTerms(inserted: inserted, fieldBefore: before, fieldAfter: after, known: known)
            guard !terms.isEmpty, self.model.pill.phase == .idle else { return }
            for term in terms { self.model.addDictionaryTerm(term, source: .autoLearned) }
            self.model.pill.show("Added to dictionary: \(terms.joined(separator: ", "))",
                                 action: .init(title: "Undo") { terms.forEach { self.model.removeDictionaryTerm($0) }; self.model.pill.set(.idle) },
                                 seconds: 6)
        }
    }

    // MARK: Selection fallback

    /// Copies the selection with ⌘C when Accessibility cannot read it (Electron apps), then restores the clipboard.
    private static func copySelection() async -> String? {
        let pb = NSPasteboard.general
        let saved = TextInserter.saveClipboard()
        let before = pb.changeCount
        TextInserterUndo.copy()
        try? await Task.sleep(for: .milliseconds(150))
        guard pb.changeCount != before else { return nil }
        let copied = pb.string(forType: .string)
        TextInserter.restoreClipboard(saved)
        return copied
    }
}

/// Key presses sent to the frontmost app.
enum TextInserterUndo {
    static func send() { post(6) }   // ⌘Z
    static func copy() { post(8) }   // ⌘C

    private static func post(_ key: CGKeyCode) {
        let source = CGEventSource(stateID: .combinedSessionState)
        for down in [true, false] {
            let e = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: down)
            e?.flags = .maskCommand
            e?.post(tap: .cghidEventTap)
        }
    }
}

extension TypingContext {
    /// Writing over a selection: no leading space, the selected text is what gets replaced.
    static func forReplacingSelection(_ ctx: TypingContext) -> TypingContext {
        var c = ctx
        c.beforeCursor = ""
        c.isEditable = true
        return c
    }
}
