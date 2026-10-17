import SwiftUI
import VoiceFlowCore

struct MeetingsPage: View {
    @Bindable var model: AppModel
    @Bindable var notetaker: Notetaker
    @State private var selected: Meeting.ID?
    @State private var query = ""
    @State private var deleting: Meeting?

    private var meetings: [Meeting] {
        notetaker.meetings.filter { !(notetaker.isRecording && $0.id == notetaker.live?.id) }.filter {
            query.isEmpty || $0.title.localizedCaseInsensitiveContains(query) || $0.transcriptText.localizedCaseInsensitiveContains(query)
        }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                Text("Meetings").font(.system(size: 22, weight: .bold))
                recordButton
                TextField("Search meetings", text: $query).textFieldStyle(.roundedBorder)
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        if notetaker.isRecording, let live = notetaker.live {
                            MeetingListRow(meeting: live, selected: selected == nil, recording: true) { selected = nil }
                        }
                        ForEach(meetings) { meeting in
                            MeetingListRow(meeting: meeting, selected: selected == meeting.id, recording: false) { selected = meeting.id }
                        }
                    }
                }
                if notetaker.meetings.isEmpty && !notetaker.isRecording {
                    Text("Start Notetaker when a call begins, or let VoiceFlow offer it when Zoom, Meet or Teams starts using your mic.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
                if notetaker.folderURL == nil {
                    Button("Sync notes to a folder…") { notetaker.chooseFolder() }.buttonStyle(.link)
                } else {
                    Label(notetaker.folderURL!.lastPathComponent, systemImage: "folder").font(.caption).foregroundStyle(.secondary)
                        .help("Notes are saved here as Markdown")
                }
            }
            .padding(16)
            .frame(width: 280)
            .background(Color.primary.opacity(0.03))

            Divider()

            Group {
                if selected == nil, notetaker.isRecording, let live = notetaker.live {
                    LiveMeetingView(meeting: live, notetaker: notetaker, since: notetaker.startedAt ?? Date()) { notetaker.stop() }
                } else if let id = selected, let meeting = notetaker.meetings.first(where: { $0.id == id }) {
                    MeetingDetail(meeting: meeting, notetaker: notetaker) { deleting = meeting }
                        .id(meeting.id)
                } else {
                    VStack(spacing: 10) {
                        Image(systemName: "person.2.wave.2").font(.system(size: 32)).foregroundStyle(.secondary)
                        Text("Your meeting notes appear here.").foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
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
                Label("Take notes", systemImage: "record.circle").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).controlSize(.large)
        case .recording:
            Button { notetaker.stop() } label: {
                Label("Stop and write notes", systemImage: "stop.circle.fill").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).tint(.red).controlSize(.large)
        case .writingNotes:
            HStack { ProgressView().controlSize(.small); Text("Writing notes…") }.frame(maxWidth: .infinity)
        }
    }
}

private struct MeetingListRow: View {
    let meeting: Meeting
    let selected: Bool
    let recording: Bool
    let onSelect: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 5) {
                if recording { Circle().fill(Color.red).frame(width: 7, height: 7) }
                Text(meeting.title).font(.callout.weight(.medium)).lineLimit(1)
            }
            HStack(spacing: 6) {
                Text(meeting.start, format: .dateTime.month(.abbreviated).day().hour().minute())
                if !recording { Text("· \(max(1, Int((meeting.duration / 60).rounded()))) min") }
                if meeting.status == .writingNotes { Text("· writing notes") }
                if meeting.status == .failed { Text("· needs attention").foregroundStyle(.orange) }
            }
            .font(.caption).foregroundStyle(.secondary)
        }
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.accentColor.opacity(0.15) : .clear))
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
    }
}

private struct LiveMeetingView: View {
    let meeting: Meeting
    let notetaker: Notetaker
    let since: Date
    let stop: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Circle().fill(Color.red).frame(width: 10, height: 10)
                Text(meeting.title).font(.title2.bold())
                Spacer()
                TimelineView(.periodic(from: since, by: 1)) { ctx in
                    Text(Meeting.clock(ctx.date.timeIntervalSince(since))).font(.title3.monospacedDigit())
                }
                Button("Stop", action: stop).buttonStyle(.borderedProminent).tint(.red)
            }
            if meeting.micOnly {
                Label("Recording your microphone only. Allow system audio recording in Settings → Notetaker to capture the other people.",
                      systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.orange)
            }
            HStack(spacing: 24) {
                InputLevel(label: "You (microphone)", level: notetaker.micLevel, tick: notetaker.levelTick)
                if !meeting.micOnly {
                    InputLevel(label: "Others (meeting audio)", level: notetaker.systemLevel, tick: notetaker.levelTick)
                }
            }
            Text("The transcript fills in every 20–45 seconds, at pauses. If a bar never moves, that side isn't being recorded.")
                .font(.callout).foregroundStyle(.secondary)
            TranscriptList(lines: meeting.lines)
        }
        .padding(24)
    }
}

private struct MeetingDetail: View {
    let meeting: Meeting
    let notetaker: Notetaker
    let onDelete: () -> Void
    @State private var title = ""
    @State private var tab = 0

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                TextField("Title", text: $title)
                    .font(.system(size: 24, weight: .bold)).textFieldStyle(.plain)
                    .onSubmit { notetaker.rename(meeting.id, to: title) }
                HStack(spacing: 8) {
                    Text(meeting.start.formatted(date: .complete, time: .shortened))
                    Text("· \(max(1, Int((meeting.duration / 60).rounded()))) min")
                    if !meeting.app.isEmpty { Text("· \(meeting.app)") }
                    if meeting.micOnly { Text("· your mic only").foregroundStyle(.orange) }
                }
                .font(.callout).foregroundStyle(.secondary)
                if !meeting.attendees.isEmpty {
                    Text("With " + meeting.attendees.joined(separator: ", ")).font(.callout).foregroundStyle(.secondary)
                }
                HStack {
                    Picker("", selection: $tab) {
                        Text("Notes").tag(0)
                        Text("Transcript").tag(1)
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 220)
                    Spacer()
                    Button("Copy") { TextInserter.copyToClipboard(MeetingMarkdown.render(meeting)) }
                    Button("Save as Markdown…") { notetaker.saveMarkdownAs(meeting) }
                    Menu {
                        Button("Rewrite notes") { Task { await notetaker.writeNotes(for: meeting.id) } }
                        Divider()
                        Button("Delete meeting", role: .destructive, action: onDelete)
                    } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                }

                switch meeting.status {
                case .writingNotes:
                    HStack { ProgressView().controlSize(.small); Text("Writing notes. Long meetings take a minute or two.") }
                case .failed:
                    VStack(alignment: .leading, spacing: 6) {
                        Text(meeting.error ?? "Notes could not be written.").foregroundStyle(.orange)
                        Button("Try again") { Task { await notetaker.writeNotes(for: meeting.id) } }
                    }
                default:
                    EmptyView()
                }

                if tab == 0 {
                    if let notes = meeting.notes { NotesView(meeting: meeting, notes: notes, notetaker: notetaker) }
                } else {
                    TranscriptList(lines: meeting.lines)
                }
            }
            .padding(24)
            .frame(maxWidth: 820, alignment: .leading)
        }
        .onAppear { title = meeting.title }
    }
}

private struct NotesView: View {
    let meeting: Meeting
    let notes: MeetingNotes
    let notetaker: Notetaker

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            section("Summary", items: notes.summary)
            section("Decisions", items: notes.decisions)
            if !notes.actionItems.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Action items").font(.headline)
                    ForEach(notes.actionItems) { item in
                        HStack(alignment: .top, spacing: 8) {
                            Button { notetaker.toggleAction(meeting.id, item: item.id) } label: {
                                Image(systemName: item.done ? "checkmark.square.fill" : "square")
                            }
                            .buttonStyle(.plain)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.task).strikethrough(item.done)
                                let meta = [item.owner, item.due.map { "due \($0)" }].compactMap { $0 }.joined(separator: " · ")
                                if !meta.isEmpty { Text(meta).font(.caption).foregroundStyle(.secondary) }
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
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.headline)
                ForEach(items, id: \.self) { item in
                    HStack(alignment: .top, spacing: 8) { Text("•"); Text(item) }
                }
            }
        }
    }
}

private struct TranscriptList: View {
    let lines: [TranscriptLine]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if lines.isEmpty {
                Text("No transcript yet.").foregroundStyle(.secondary)
            }
            ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                HStack(alignment: .top, spacing: 10) {
                    Text(Meeting.clock(line.time)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 52, alignment: .leading)
                    Text(line.speaker.label)
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(Capsule().fill(line.speaker == .you ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.08)))
                        .frame(width: 60, alignment: .leading)
                    Text(line.text).textSelection(.enabled)
                }
            }
        }
    }
}

private struct InputLevel: View {
    let label: String
    let level: Float
    let tick: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.08))
                    Capsule().fill(Color.green.opacity(0.8)).frame(width: max(4, geo.size.width * CGFloat(level)))
                }
            }
            .frame(width: 180, height: 8)
            .id(tick)
        }
    }
}
