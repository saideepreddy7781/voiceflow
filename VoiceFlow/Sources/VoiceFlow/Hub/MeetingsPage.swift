import SwiftUI
import VoiceFlowCore

struct MeetingsPage: View {
    @Bindable var model: AppModel
    @Bindable var notetaker: Notetaker
    /// Which detail tab opens first. Screenshot mode asks for the transcript.
    var initialTab = 0
    @State private var selected: Meeting.ID?
    @State private var query = ""
    @State private var deleting: Meeting?

    private var meetings: [Meeting] {
        notetaker.meetings.filter { !(notetaker.isRecording && $0.id == notetaker.live?.id) }.filter {
            MeetingSearch.matches($0, query: query)
        }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: Spacing.sm) {
                Text("Meetings").font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                recordButton
                SearchField(prompt: "Search meetings", text: $query)
                if notetaker.meetings.isEmpty && !notetaker.isRecording {
                    Text("Start Notetaker when a call begins, or let VoiceFlow offer it when Zoom, Meet or Teams starts using your mic.")
                        .font(TypeScale.callout).foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                } else {
                    VerticalScroll {
                        VStack(alignment: .leading, spacing: 2) {
                            if notetaker.isRecording, let live = notetaker.live {
                                MeetingListRow(meeting: live, snippet: nil, selected: selected == nil, recording: true) { selected = nil }
                            }
                            ForEach(meetings) { meeting in
                                MeetingListRow(meeting: meeting, snippet: MeetingSearch.snippet(meeting, query: query),
                                               selected: selected == meeting.id, recording: false) { selected = meeting.id }
                            }
                        }
                    }
                }
                if let folder = notetaker.folderURL {
                    Label(folder.lastPathComponent, systemImage: "folder")
                        .font(TypeScale.caption).foregroundStyle(Palette.muted).lineLimit(1)
                        .help("Notes are saved here as Markdown")
                } else {
                    Button("Sync notes to a folder…") { notetaker.chooseFolder() }.buttonStyle(.vfLink)
                }
            }
            .padding(.horizontal, Spacing.lg).padding(.top, Spacing.xl).padding(.bottom, Spacing.md)
            .frame(width: Metrics.listColumnWidth + Spacing.lg)

            Rectangle().fill(Palette.hairline).frame(width: 1)

            Group {
                if selected == nil, notetaker.isRecording, let live = notetaker.live {
                    LiveMeetingView(meeting: live, notetaker: notetaker, since: notetaker.startedAt ?? Date()) { notetaker.stop() }
                } else if let id = selected, let meeting = notetaker.meetings.first(where: { $0.id == id }) {
                    MeetingDetail(meeting: meeting, notetaker: notetaker, initialTab: initialTab) { deleting = meeting }
                        .id(meeting.id)
                } else {
                    VStack {
                        EmptyState(symbol: "person.2", text: "Your meeting notes appear here.")
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .background(Palette.window)
        .onAppear { if selected == nil && !notetaker.isRecording { selected = notetaker.meetings.first?.id } }
        .onChange(of: notetaker.lastStoppedID) { _, id in if let id { selected = id } }
        .alert("Delete “\(deleting?.title ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Delete", role: .destructive) {
                if let d = deleting {
                    notetaker.delete(d.id)
                    if selected == d.id { selected = notetaker.meetings.first?.id }
                }
                deleting = nil
            }
            Button("Cancel", role: .cancel) { deleting = nil }
        } message: {
            Text("The notes and transcript are removed from VoiceFlow. A copy already saved to your notes folder stays there.")
        }
    }

    @ViewBuilder private var recordButton: some View {
        switch notetaker.state {
        case .idle:
            Button { notetaker.start() } label: {
                Label("Take notes", systemImage: "record.circle")
            }
            .buttonStyle(.vf(.primary, fullWidth: true))
        case .recording:
            Button { notetaker.stop() } label: {
                Label("Stop and write notes", systemImage: "stop.fill")
            }
            .buttonStyle(.vf(.signal, fullWidth: true))
        case .writingNotes:
            HStack(spacing: Spacing.xs) {
                ProgressView().controlSize(.small)
                Text("Writing notes…").font(TypeScale.callout).foregroundStyle(Palette.muted)
            }
            .frame(maxWidth: .infinity, minHeight: 34)
        }
    }
}

private struct MeetingListRow: View {
    let meeting: Meeting
    let snippet: String?
    let selected: Bool
    let recording: Bool
    let onSelect: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: onSelect) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    if recording { RecordingDot(size: 7) }
                    Text(meeting.title).font(TypeScale.calloutMedium).foregroundStyle(Palette.foreground).lineLimit(1)
                }
                HStack(spacing: Spacing.xxs) {
                    if recording {
                        Text("Recording now")
                    } else {
                        Text(meeting.start, format: .dateTime.month(.abbreviated).day().hour().minute())
                        Text("· \(max(1, Int((meeting.duration / 60).rounded()))) min")
                    }
                }
                .font(TypeScale.caption).foregroundStyle(Palette.muted).lineLimit(1)
                if meeting.status == .writingNotes {
                    Text("Writing notes").font(TypeScale.caption).foregroundStyle(Palette.muted)
                }
                if meeting.status == .failed {
                    Text("Needs attention").font(TypeScale.caption).foregroundStyle(Palette.signal)
                }
                if let snippet {
                    Text(snippet).font(TypeScale.caption).foregroundStyle(Palette.muted).lineLimit(2)
                }
            }
            .padding(.horizontal, Spacing.sm).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous)
                .fill(selected ? Palette.selected : (hovering ? Palette.hover : .clear)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.quiet(radius: Radius.overlay))
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

// MARK: Live

private struct LiveMeetingView: View {
    let meeting: Meeting
    let notetaker: Notetaker
    let since: Date
    let stop: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.md) {
            HStack(spacing: Spacing.xs) {
                RecordingDot(size: 10)
                Text(meeting.title).font(.system(size: 22, weight: .bold)).foregroundStyle(Palette.foreground).lineLimit(1)
                Spacer(minLength: Spacing.md)
                clock
                Button(action: stop) { Label("Stop", systemImage: "stop.fill") }.buttonStyle(.vfSignal)
            }
            if meeting.micOnly {
                AttentionLine(text: "Recording your microphone only. Allow system audio recording in Settings → Notetaker to capture the other people.")
            }
            micBanner
            HStack(alignment: .top, spacing: Spacing.md) {
                InputLevel(label: "You (microphone)", language: notetaker.micLanguage, level: notetaker.micLevel,
                           status: micStatus, tick: notetaker.levelTick)
                if !meeting.micOnly {
                    InputLevel(label: "Others (meeting audio)", language: notetaker.callLanguage, level: notetaker.systemLevel,
                               status: .plain("The transcript fills in at pauses"), tick: notetaker.levelTick)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            Text("Each side fills in after about 20 seconds of talking, at a pause. If a bar never moves, that side isn't being recorded.")
                .font(TypeScale.caption).foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
            SectionHeading(text: "Transcript").padding(.top, Spacing.xxs)
            VerticalScroll {
                TranscriptList(meeting: meeting).frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, Spacing.xl).padding(.top, Spacing.xl).padding(.bottom, Spacing.md)
    }

    @ViewBuilder private var clock: some View {
        if AppRuntime.isDemo {
            clockText(767)
        } else {
            TimelineView(.periodic(from: since, by: 1)) { ctx in
                clockText(ctx.date.timeIntervalSince(since))
            }
        }
    }

    private func clockText(_ seconds: TimeInterval) -> some View {
        Text(Meeting.clock(seconds)).font(.system(size: 18, weight: .semibold).monospacedDigit()).foregroundStyle(Palette.foreground)
    }

    private var micStatus: InputLevel.Status {
        switch notetaker.micHealth {
        case .healthy: .good("Microphone sounds good")
        case .restarting: .attention("Restarting the microphone")
        case .silent, .noAudio: .attention("Not hearing your microphone")
        }
    }

    @ViewBuilder private var micBanner: some View {
        switch notetaker.micHealth {
        case .healthy:
            EmptyView()
        case .restarting:
            AttentionLine(text: "Your microphone stopped sending audio. Restarting it…", symbol: "arrow.clockwise")
        case .silent:
            AttentionLine(text: "Your microphone is sending silence. Your side of the call isn't being recorded.", symbol: "mic.slash")
        case .noAudio:
            AttentionLine(text: "VoiceFlow can't get audio from your microphone. Your side of the call isn't being recorded.", symbol: "mic.slash")
        }
    }
}

private struct InputLevel: View {
    enum Status {
        case good(String), attention(String), plain(String)
    }

    let label: String
    let language: String?
    let level: Float
    let status: Status
    let tick: Int

    private var languageName: String? {
        language.map { code in Languages.options.first { $0.code == code }?.name ?? code }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: 6) {
                Text(label).font(TypeScale.callout).foregroundStyle(Palette.muted).lineLimit(1)
                if let languageName { Badge(text: languageName) }
            }
            .frame(minHeight: 20, alignment: .leading)
            SegmentedMeter(level: level).id(tick)
            statusLine
        }
        .padding(Spacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .cardSurface()
        .accessibilityElement(children: .combine)
        .accessibilityLabel(label)
    }

    @ViewBuilder private var statusLine: some View {
        switch status {
        case .good(let text):
            Label(text, systemImage: "checkmark.circle").font(TypeScale.caption).foregroundStyle(Palette.muted)
        case .attention(let text):
            Label(text, systemImage: "exclamationmark.triangle").font(TypeScale.caption).foregroundStyle(Palette.signal)
        case .plain(let text):
            Text(text).font(TypeScale.caption).foregroundStyle(Palette.muted)
        }
    }
}

// MARK: Detail

private enum MeetingTab: Int, CaseIterable, Identifiable {
    case notes, transcript
    var id: Int { rawValue }
    var title: String { self == .notes ? "Notes" : "Transcript" }
}

private struct MeetingDetail: View {
    let meeting: Meeting
    let notetaker: Notetaker
    let onDelete: () -> Void
    @State private var title = ""
    @State private var tab: MeetingTab
    @State private var othersName = ""

    init(meeting: Meeting, notetaker: Notetaker, initialTab: Int, onDelete: @escaping () -> Void) {
        self.meeting = meeting
        self.notetaker = notetaker
        self.onDelete = onDelete
        _tab = State(initialValue: MeetingTab(rawValue: initialTab) ?? .notes)
    }

    var body: some View {
        VerticalScroll {
            VStack(alignment: .leading, spacing: Spacing.md) {
                TextField("Title", text: $title)
                    .font(.system(size: 24, weight: .bold)).textFieldStyle(.plain).foregroundStyle(Palette.foreground)
                    .onSubmit { notetaker.rename(meeting.id, to: title) }
                    .accessibilityLabel("Meeting title")
                    .padding(.bottom, -Spacing.xs)
                metaLines
                toolbar

                switch meeting.status {
                case .writingNotes:
                    HStack(spacing: Spacing.xs) {
                        ProgressView().controlSize(.small)
                        Text("Writing notes. Long meetings take a minute or two.").font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                case .failed:
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        AttentionLine(text: meeting.error ?? "Notes could not be written.")
                        Button("Try again") { Task { await notetaker.writeNotes(for: meeting.id) } }.buttonStyle(.vfSecondary)
                    }
                default:
                    EmptyView()
                }

                if let note = meeting.note, !note.isEmpty { AttentionLine(text: note) }

                switch tab {
                case .notes:
                    if let notes = meeting.notes {
                        NotesView(meeting: meeting, notes: notes, notetaker: notetaker)
                    } else if meeting.status == .done {
                        Text("No notes for this meeting.").font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                case .transcript:
                    if !meeting.micOnly {
                        HStack(spacing: Spacing.xs) {
                            Text("Call the other side").font(TypeScale.callout).foregroundStyle(Palette.muted)
                            InputField(prompt: "Others", text: $othersName) { notetaker.renameOthers(meeting.id, to: othersName) }
                                .frame(width: 180)
                        }
                    }
                    TranscriptList(meeting: meeting)
                }
            }
            .padding(.horizontal, Spacing.xl).padding(.top, Spacing.xl).padding(.bottom, Metrics.pagePadding)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onAppear {
            title = meeting.title
            othersName = meeting.othersLabel ?? ""
        }
    }

    private var metaLines: some View {
        VStack(alignment: .leading, spacing: Spacing.xxs) {
            HStack(spacing: 0) {
                Text(([meeting.start.formatted(date: .long, time: .shortened), "\(max(1, Int((meeting.duration / 60).rounded()))) min"]
                      + (meeting.app.isEmpty ? [] : [meeting.app])).joined(separator: " · "))
                if meeting.micOnly { Text(" · your mic only").foregroundStyle(Palette.signal) }
                if let missing = meeting.micMissingSeconds, missing >= 30 {
                    Text(" · mic missing for \(max(1, missing / 60)) min").foregroundStyle(Palette.signal)
                }
            }
            .lineLimit(1)
            if !meeting.attendees.isEmpty {
                Text("With " + meeting.attendees.joined(separator: ", ")).lineLimit(2)
            }
        }
        .font(TypeScale.callout).foregroundStyle(Palette.muted)
    }

    private var toolbar: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: Spacing.xs) {
                TabStrip(items: MeetingTab.allCases, title: \.title, selection: $tab)
                Spacer(minLength: Spacing.sm)
                Button { TextInserter.copyToClipboard(MeetingMarkdown.render(meeting)) } label: { Text("Copy as Markdown") }
                    .buttonStyle(.vfSecondary)
                Button { notetaker.saveMarkdownAs(meeting) } label: { Label("Save as Markdown…", systemImage: "arrow.down.to.line") }
                    .buttonStyle(.vfSecondary)
                moreMenu
            }
            HStack(spacing: Spacing.xs) {
                TabStrip(items: MeetingTab.allCases, title: \.title, selection: $tab)
                Spacer(minLength: Spacing.xs)
                IconButton(symbol: "doc.on.doc", help: "Copy as Markdown") { TextInserter.copyToClipboard(MeetingMarkdown.render(meeting)) }
                IconButton(symbol: "arrow.down.to.line", help: "Save as Markdown…") { notetaker.saveMarkdownAs(meeting) }
                moreMenu
            }
        }
    }

    private var moreMenu: some View {
        Menu {
            Button("Rewrite notes") { Task { await notetaker.writeNotes(for: meeting.id) } }
            Divider()
            Button("Delete meeting", role: .destructive, action: onDelete)
        } label: { Image(systemName: "ellipsis") }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        .frame(width: 28, height: 28)
        .help("More actions")
        .accessibilityLabel("More actions")
    }
}

private struct NotesView: View {
    let meeting: Meeting
    let notes: MeetingNotes
    let notetaker: Notetaker

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.lg) {
            section("Summary", items: notes.summary)
            section("Decisions", items: notes.decisions)
            if !notes.actionItems.isEmpty {
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    SectionHeading(text: "Action items")
                    ForEach(notes.actionItems) { item in
                        HStack(alignment: .top, spacing: Spacing.xs) {
                            Button { notetaker.toggleAction(meeting.id, item: item.id) } label: {
                                CheckMark(isOn: item.done).padding(.top, 2)
                            }
                            .buttonStyle(.quiet(radius: 4))
                            .accessibilityLabel(item.task)
                            .accessibilityValue(item.done ? "Done" : "Not done")
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.task).font(TypeScale.body).foregroundStyle(item.done ? Palette.muted : Palette.foreground).strikethrough(item.done)
                                let meta = [item.owner, item.due.map { "due \($0)" }].compactMap { $0 }.joined(separator: " · ")
                                if !meta.isEmpty { Text(meta).font(TypeScale.caption).foregroundStyle(Palette.muted) }
                            }
                        }
                    }
                }
            }
            section("Open questions", items: notes.questions)
        }
        .textSelection(.enabled)
    }

    @ViewBuilder private func section(_ title: String, items: [String]) -> some View {
        if !items.isEmpty {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                SectionHeading(text: title)
                ForEach(items, id: \.self) { item in
                    HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                        Circle().fill(Palette.subtle).frame(width: 4, height: 4).alignmentGuide(.firstTextBaseline) { $0[.bottom] + 3 }
                            .accessibilityHidden(true)
                        Text(item).font(TypeScale.body).foregroundStyle(Palette.foreground).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
    }
}

private struct TranscriptList: View {
    let meeting: Meeting
    private var lines: [TranscriptLine] { meeting.lines }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            if lines.isEmpty {
                Text("No transcript yet.").font(TypeScale.callout).foregroundStyle(Palette.muted)
            }
            ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                HStack(alignment: .top, spacing: Spacing.sm) {
                    Text(Meeting.clock(line.time)).font(TypeScale.caption).monospacedDigit().foregroundStyle(Palette.muted)
                        .frame(width: 44, alignment: .leading).padding(.top, 2)
                    Badge(text: meeting.label(for: line.speaker), tone: line.speaker == .you ? .inverse : .neutral)
                        .frame(width: 84, alignment: .leading)
                    Text(line.text).font(TypeScale.body).foregroundStyle(Palette.foreground)
                        .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
            }
        }
    }
}
