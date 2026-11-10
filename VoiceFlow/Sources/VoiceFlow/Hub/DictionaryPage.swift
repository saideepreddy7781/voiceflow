import AppKit
import SwiftUI
import UniformTypeIdentifiers
import VoiceFlowCore

private enum DictionaryTab: String, CaseIterable, Identifiable {
    case all, yours, learned
    var id: String { rawValue }
    var title: String {
        switch self {
        case .all: "All"
        case .yours: "Added by you"
        case .learned: "Learned"
        }
    }
}

struct DictionaryPage: View {
    @Bindable var model: AppModel
    @State private var tab: DictionaryTab = .all
    @State private var query = ""
    @State private var editing: DictionaryEntry?
    @State private var adding = false
    @State private var deleting: DictionaryEntry?
    @State private var notice: String?

    private var entries: [DictionaryEntry] {
        model.dictionary.filter { entry in
            switch tab {
            case .all: true
            case .yours: entry.source == .manual
            case .learned: entry.source == .autoLearned
            }
        }
        .filter { query.isEmpty || $0.term.localizedCaseInsensitiveContains(query) || ($0.misspelling ?? "").localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        Page(spacing: Spacing.md) {
            PageHeader(title: "Dictionary", subtitle: "Names, jargon and spellings VoiceFlow should always get right.") {
                HStack(spacing: Spacing.xs) {
                    Button("Import") { importFile() }.buttonStyle(.vfSecondary)
                    Button { adding = true } label: { Label("Add word", systemImage: "plus") }.buttonStyle(.vfPrimary)
                }
            }
            HStack(spacing: Spacing.md) {
                TabStrip(items: DictionaryTab.allCases, title: \.title, selection: $tab)
                Spacer(minLength: Spacing.md)
                SearchField(prompt: "Search words", text: $query).frame(maxWidth: 240)
            }
            if let notice { Text(notice).font(TypeScale.callout).foregroundStyle(Palette.muted) }

            if entries.isEmpty {
                EmptyState(symbol: "book",
                           text: query.isEmpty ? "No words yet. Add names and terms you use, or correct a dictation and VoiceFlow learns it."
                                               : "No words match “\(query)”.")
            } else {
                VStack(spacing: 0) {
                    ForEach(entries) { entry in
                        DictionaryRow(entry: entry, onEdit: { editing = entry }, onDelete: { deleting = entry })
                        if entry.id != entries.last?.id { RowDivider() }
                    }
                }
                .rowGroupSurface()
            }
        }
        .sheet(isPresented: $adding) { DictionaryEditor(model: model, entry: nil) }
        .sheet(item: $editing) { DictionaryEditor(model: model, entry: $0) }
        .alert("Delete “\(deleting?.term ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Yes, delete it", role: .destructive) {
                if let d = deleting { model.removeDictionaryEntries([d.id]) }
                deleting = nil
            }
            Button("Cancel", role: .cancel) { deleting = nil }
        } message: {
            Text("VoiceFlow won't learn this word again on its own.")
        }
    }

    private func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .commaSeparatedText]
        panel.message = "Choose a text or CSV file with one word per line (up to 1,000)."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let added = model.importDictionary(from: url)
        notice = "Imported \(added) word\(added == 1 ? "" : "s")."
    }
}

private struct DictionaryRow: View {
    let entry: DictionaryEntry
    let onEdit: () -> Void
    let onDelete: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: Spacing.sm) {
            if let wrong = entry.misspelling {
                Text(wrong).font(TypeScale.body).foregroundStyle(Palette.muted).strikethrough().lineLimit(1)
                Image(systemName: "arrow.right").font(.system(size: 11)).foregroundStyle(Palette.subtle).accessibilityLabel("corrects to")
            }
            Text(entry.term).font(TypeScale.bodyMedium).foregroundStyle(Palette.foreground).lineLimit(1)
            if entry.source == .autoLearned { Badge(text: "Learned") }
            Spacer(minLength: Spacing.xs)
            HoverActions(visible: hovering) {
                IconButton(symbol: "pencil", help: "Edit", action: onEdit)
                IconButton(symbol: "trash", help: "Delete", action: onDelete)
            }
        }
        .padding(.horizontal, Spacing.md).frame(minHeight: 44)
        .background(hovering ? Palette.hover : Color.clear)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

private struct DictionaryEditor: View {
    let model: AppModel
    let entry: DictionaryEntry?
    @Environment(\.dismiss) private var dismiss
    @State private var term = ""
    @State private var misspelling = ""
    @State private var correcting = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.md) {
            Text(entry == nil ? "Add to dictionary" : (entry?.misspelling == nil ? "Edit word" : "Edit replacement"))
                .font(TypeScale.sectionTitle).foregroundStyle(Palette.foreground)
            Toggle("Correct a misspelling", isOn: $correcting).toggleStyle(VFCheckboxStyle())
            if correcting {
                InputField(prompt: "Misspelling (what VoiceFlow types now)", text: $misspelling)
            }
            InputField(prompt: correcting ? "Correct spelling" : "Word or phrase", text: $term, onSubmit: save)
            if let error { AttentionLine(text: error) }
            HStack(spacing: Spacing.xs) {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).buttonStyle(.vfSecondary)
                Button(entry == nil ? "Add word" : "Save changes", action: save)
                    .keyboardShortcut(.defaultAction).buttonStyle(.vfPrimary)
            }
        }
        .padding(Spacing.lg)
        .frame(width: 420)
        .background(Palette.window)
        .onAppear {
            term = entry?.term ?? ""
            misspelling = entry?.misspelling ?? ""
            correcting = entry?.misspelling != nil
        }
    }

    private func save() {
        let wrong = correcting ? misspelling : nil
        if var existing = entry {
            let t = term.trimmingCharacters(in: .whitespacesAndNewlines)
            let m = wrong?.trimmingCharacters(in: .whitespacesAndNewlines)
            if t == existing.term && m == existing.misspelling { error = "No changes made."; return }
            if t.isEmpty { error = "Word cannot be empty."; return }
            existing.term = t
            existing.misspelling = (m?.isEmpty ?? true) ? nil : m
            model.updateDictionaryEntry(existing)
            dismiss()
        } else if let problem = model.addDictionaryEntry(term: term, misspelling: wrong) {
            error = problem
        } else {
            dismiss()
        }
    }
}
