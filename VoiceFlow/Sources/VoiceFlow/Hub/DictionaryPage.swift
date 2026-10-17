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
    @State private var searching = false
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
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: "Dictionary", subtitle: "Names, jargon and spellings VoiceFlow should always get right.") {
                    HStack(spacing: 8) {
                        IconButton(symbol: "magnifyingglass", help: "Search") { searching.toggle(); if !searching { query = "" } }
                        Button("Import") { importFile() }
                        Button("Add new") { adding = true }.buttonStyle(.borderedProminent)
                    }
                }
                TabStrip(items: DictionaryTab.allCases, title: \.title, selection: $tab)
                if searching {
                    TextField("Search words and corrections", text: $query).textFieldStyle(.roundedBorder)
                }
                if let notice { Text(notice).font(.callout).foregroundStyle(.secondary) }

                if entries.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "character.book.closed").font(.system(size: 28)).foregroundStyle(.secondary)
                        Text(query.isEmpty ? "No words yet. Add names and terms you use, or correct a dictation and VoiceFlow learns it."
                                           : "No matches.")
                            .foregroundStyle(.secondary).multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 40)
                } else {
                    VStack(spacing: 0) {
                        ForEach(entries) { entry in
                            DictionaryRow(entry: entry, onEdit: { editing = entry }, onDelete: { deleting = entry })
                            if entry.id != entries.last?.id { Divider() }
                        }
                    }
                    .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.03)))
                    .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.07)))
                }
            }
            .padding(28)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
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
        HStack(spacing: 10) {
            if let wrong = entry.misspelling {
                Text(wrong).foregroundStyle(.secondary).strikethrough()
                Image(systemName: "arrow.right").font(.caption).foregroundStyle(.secondary)
            }
            Text(entry.term).font(.body.weight(.medium))
            if entry.source == .autoLearned {
                Text("Learned").font(.caption).padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Capsule().fill(Color.accentColor.opacity(0.15)))
            }
            Spacer()
            HStack(spacing: 2) {
                IconButton(symbol: "pencil", help: "Edit", action: onEdit)
                IconButton(symbol: "trash", help: "Delete", action: onDelete)
            }
            .opacity(hovering ? 1 : 0)
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
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
        VStack(alignment: .leading, spacing: 14) {
            Text(entry == nil ? "Add to dictionary" : (entry?.misspelling == nil ? "Edit word" : "Edit replacement"))
                .font(.title2.bold())
            Toggle("Correct a misspelling", isOn: $correcting)
            if correcting {
                TextField("Misspelling (what VoiceFlow types now)", text: $misspelling).textFieldStyle(.roundedBorder)
            }
            TextField(correcting ? "Correct spelling" : "Word or phrase", text: $term).textFieldStyle(.roundedBorder)
                .onSubmit(save)
            if let error { Text(error).foregroundStyle(.red).font(.callout) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(entry == nil ? "Add word" : "Save changes", action: save)
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }
        }
        .padding(24)
        .frame(width: 420)
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
