import SwiftUI
import VoiceFlowCore

/// Notes for drafting by voice or keyboard. Opens with ⌥S, or from the Hub.
struct ScratchpadView: View {
    @Bindable var model: AppModel
    /// In the Hub the page header is shown; the standalone window uses the window title instead.
    let compact: Bool
    @State private var selected: Note.ID?
    @State private var query = ""
    @State private var deleting: Note?

    private var notes: [Note] {
        model.sortedNotes.filter { query.isEmpty || $0.displayTitle.localizedCaseInsensitiveContains(query) || $0.body.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("Scratchpad").font(.headline)
                    Spacer()
                    IconButton(symbol: "square.and.pencil", help: "New note") { selected = model.newNote().id }
                }
                TextField("Search", text: $query).textFieldStyle(.roundedBorder)
                ScrollView {
                    VStack(spacing: 4) {
                        ForEach(notes) { note in
                            NoteListRow(note: note, selected: note.id == selected,
                                        onSelect: { selected = note.id },
                                        onPin: { var n = note; n.pinned.toggle(); model.updateNote(n) },
                                        onDelete: { deleting = note })
                        }
                    }
                }
                Text("⌥S opens Scratchpad from any app").font(.caption).foregroundStyle(.secondary)
            }
            .padding(14)
            .frame(width: 240)
            .background(Color.primary.opacity(0.03))

            Divider()

            if let id = selected, let note = model.notes.first(where: { $0.id == id }) {
                NoteEditor(model: model, note: note)
                    .id(note.id)
            } else {
                VStack(spacing: 10) {
                    Image(systemName: "note.text").font(.system(size: 30)).foregroundStyle(.secondary)
                    Text("Draft anything here. Hold \(model.preferences.hotkey.shortName.lowercased()) to dictate into a note.")
                        .foregroundStyle(.secondary)
                    Button("New note") { selected = model.newNote().id }.buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .onAppear { if selected == nil { selected = model.sortedNotes.first?.id } }
        .alert("Delete “\(deleting?.displayTitle ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Delete", role: .destructive) {
                if let d = deleting {
                    model.deleteNote(d.id)
                    if selected == d.id { selected = model.sortedNotes.first?.id }
                }
                deleting = nil
            }
            Button("Cancel", role: .cancel) { deleting = nil }
        } message: {
            Text("This can't be undone.")
        }
    }
}

private struct NoteListRow: View {
    let note: Note
    let selected: Bool
    let onSelect: () -> Void
    let onPin: () -> Void
    let onDelete: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    if note.pinned { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(.orange) }
                    Text(note.displayTitle).font(.callout.weight(.medium)).lineLimit(1)
                }
                Text(note.updated, format: .relative(presentation: .named)).font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            if hovering {
                IconButton(symbol: note.pinned ? "pin.slash" : "pin", help: note.pinned ? "Unpin" : "Pin", action: onPin)
                IconButton(symbol: "trash", help: "Delete", action: onDelete)
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.accentColor.opacity(0.15) : (hovering ? Color.primary.opacity(0.05) : .clear)))
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
        .onHover { hovering = $0 }
    }
}

private struct NoteEditor: View {
    let model: AppModel
    @State var note: Note

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                TextField("Untitled", text: $note.title).font(.title2.bold()).textFieldStyle(.plain)
                Spacer()
                IconButton(symbol: "mic", help: "Dictate hands-free") { model.controller.startHandsFreeFromBar() }
                IconButton(symbol: "doc.on.doc", help: "Copy note") { TextInserter.copyToClipboard(note.body) }
            }
            TextEditor(text: $note.body)
                .font(.system(size: 15))
                .scrollContentBackground(.hidden)
            Text("Saved automatically").font(.caption).foregroundStyle(.secondary)
        }
        .padding(20)
        .onChange(of: note) { _, updated in model.updateNote(updated) }
    }
}
