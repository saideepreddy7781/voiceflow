import SwiftUI
import VoiceFlowCore

/// Notes for drafting by voice or keyboard. Opens with ⌥S, or from the Hub.
struct ScratchpadView: View {
    @Bindable var model: AppModel
    /// In the Hub the page title is large; the standalone window has a window title too.
    let compact: Bool
    @State private var selected: Note.ID?
    @State private var query = ""
    @State private var deleting: Note?

    private var notes: [Note] {
        model.sortedNotes.filter { query.isEmpty || $0.displayTitle.localizedCaseInsensitiveContains(query) || $0.body.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: Spacing.sm) {
                HStack {
                    Text("Scratchpad")
                        .font(compact ? TypeScale.pageTitle : TypeScale.cardTitle)
                        .foregroundStyle(Palette.foreground)
                        .accessibilityAddTraits(.isHeader)
                    Spacer()
                    IconButton(symbol: "plus", help: "New note") { selected = model.newNote().id }
                }
                SearchField(prompt: "Search notes", text: $query)
                VerticalScroll {
                    VStack(spacing: 2) {
                        ForEach(notes) { note in
                            NoteListRow(note: note, selected: note.id == selected,
                                        onSelect: { selected = note.id },
                                        onPin: { var n = note; n.pinned.toggle(); model.updateNote(n) },
                                        onDelete: { deleting = note })
                        }
                    }
                }
                Text("⌥S opens Scratchpad from any app").font(TypeScale.caption).foregroundStyle(Palette.muted)
            }
            .padding(.horizontal, Spacing.lg).padding(.top, Spacing.xl).padding(.bottom, Spacing.md)
            .frame(width: Metrics.listColumnWidth)

            Rectangle().fill(Palette.hairline).frame(width: 1)

            if let id = selected, let note = model.notes.first(where: { $0.id == id }) {
                NoteEditor(model: model, note: note)
                    .id(note.id)
            } else {
                VStack {
                    EmptyState(symbol: "note.text", text: "Draft anything here. Hold \(model.preferences.hotkey.shortName.lowercased()) to dictate into a note.") {
                        Button("New note") { selected = model.newNote().id }.buttonStyle(.vfPrimary)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .background(Palette.window)
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
        HStack(alignment: .top, spacing: Spacing.xs) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: Spacing.xxs) {
                    if note.pinned {
                        Image(systemName: "pin").font(.system(size: 10)).foregroundStyle(Palette.muted).accessibilityLabel("Pinned")
                    }
                    Text(note.displayTitle).font(TypeScale.calloutMedium).foregroundStyle(Palette.foreground).lineLimit(1)
                }
                Text(note.updated, format: .relative(presentation: .named)).font(TypeScale.caption).foregroundStyle(Palette.muted)
            }
            Spacer(minLength: 0)
            if hovering || KeyboardFocus.showsRing {
                IconButton(symbol: note.pinned ? "pin.slash" : "pin", help: note.pinned ? "Unpin" : "Pin", size: 24, action: onPin)
                IconButton(symbol: "trash", help: "Delete", size: 24, action: onDelete)
            }
        }
        .padding(.horizontal, Spacing.sm).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous)
            .fill(selected ? Palette.selected : (hovering ? Palette.hover : .clear)))
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
        .onHover { hovering = $0 }
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(selected ? .isSelected : .isButton)
        .accessibilityAction { onSelect() }
    }
}

private struct NoteEditor: View {
    let model: AppModel
    @State var note: Note

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            HStack(spacing: Spacing.xxs) {
                TextField("Untitled", text: $note.title)
                    .font(.system(size: 22, weight: .bold)).textFieldStyle(.plain).foregroundStyle(Palette.foreground)
                    .accessibilityLabel("Note title")
                Spacer()
                IconButton(symbol: "mic", help: "Dictate hands-free") { model.controller.startHandsFreeFromBar() }
                IconButton(symbol: "doc.on.doc", help: "Copy note") { TextInserter.copyToClipboard(note.body) }
            }
            if AppRuntime.isDemo {
                // NSTextView content doesn't draw in an offscreen capture.
                Text(note.body).font(.system(size: 15)).foregroundStyle(Palette.foreground)
                    .lineSpacing(4).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                TextEditor(text: $note.body)
                    .font(.system(size: 15))
                    .scrollContentBackground(.hidden)
                    .accessibilityLabel("Note text")
            }
            Text("Saved automatically").font(TypeScale.caption).foregroundStyle(Palette.muted)
        }
        .padding(.horizontal, Spacing.xl).padding(.top, Spacing.xl).padding(.bottom, Spacing.md)
        .onChange(of: note) { _, updated in model.updateNote(updated) }
    }
}
