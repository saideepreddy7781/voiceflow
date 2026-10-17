import AppKit
import SwiftUI
import UniformTypeIdentifiers
import VoiceFlowCore

struct SnippetsPage: View {
    @Bindable var model: AppModel
    @State private var editing: Snippet?
    @State private var adding = false
    @State private var deleting: Snippet?
    @State private var notice: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: "Snippets", subtitle: "Say a short cue and VoiceFlow types the full text, on its own or inside a sentence.") {
                    HStack(spacing: 8) {
                        Button("Import") { importFile() }
                        Button("Add new") { adding = true }.buttonStyle(.borderedProminent)
                    }
                }
                if let notice { Text(notice).font(.callout).foregroundStyle(.secondary) }

                if model.snippets.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "text.badge.plus").font(.system(size: 28)).foregroundStyle(.secondary)
                        Text("No snippets yet. Try one for your calendar link, address or a reply you send often.")
                            .foregroundStyle(.secondary).multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 40)
                } else {
                    VStack(spacing: 0) {
                        ForEach(model.snippets) { snippet in
                            SnippetRow(snippet: snippet, onEdit: { editing = snippet }, onDelete: { deleting = snippet })
                            if snippet.id != model.snippets.last?.id { Divider() }
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
        .sheet(isPresented: $adding) { SnippetEditor(model: model, snippet: nil) }
        .sheet(item: $editing) { SnippetEditor(model: model, snippet: $0) }
        .alert("Delete “\(deleting?.trigger ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Yes, delete it", role: .destructive) {
                if let d = deleting { model.snippets.removeAll { $0.id == d.id } }
                deleting = nil
            }
            Button("Cancel", role: .cancel) { deleting = nil }
        }
    }

    private func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText, .commaSeparatedText, .tabSeparatedText]
        panel.message = "Choose a file with one snippet per line: cue, then a comma or tab, then the text (up to 1,000)."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let added = model.importSnippets(from: url)
        notice = "Imported \(added) snippet\(added == 1 ? "" : "s")."
    }
}

private struct SnippetRow: View {
    let snippet: Snippet
    let onEdit: () -> Void
    let onDelete: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(snippet.trigger).font(.body.weight(.semibold)).frame(width: 200, alignment: .leading)
            Image(systemName: "arrow.right").font(.caption).foregroundStyle(.secondary).padding(.top, 3)
            Text(snippet.expansion).foregroundStyle(.secondary).lineLimit(3)
            Spacer()
            HStack(spacing: 2) {
                IconButton(symbol: "pencil", help: "Edit", action: onEdit)
                IconButton(symbol: "trash", help: "Delete", action: onDelete)
            }
            .opacity(hovering ? 1 : 0)
        }
        .padding(.horizontal, 14).padding(.vertical, 11)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

private struct SnippetEditor: View {
    @Bindable var model: AppModel
    let snippet: Snippet?
    @Environment(\.dismiss) private var dismiss
    @State private var trigger = ""
    @State private var expansion = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(snippet == nil ? "Add snippet" : "Edit snippet").font(.title2.bold())
            Text("Snippet").font(.callout.weight(.medium))
            TextField("e.g. my calendar link", text: $trigger).textFieldStyle(.roundedBorder)
            Text("Expansion").font(.callout.weight(.medium))
            TextEditor(text: $expansion)
                .font(.body)
                .frame(height: 140)
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.15)))
            HStack {
                Text("\(trigger.count)/\(Snippet.maxTrigger) · \(expansion.count)/\(Snippet.maxExpansion)")
                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                Spacer()
            }
            if let error { Text(error).foregroundStyle(.red).font(.callout) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(snippet == nil ? "Add snippet" : "Save changes", action: save)
                    .keyboardShortcut(.return, modifiers: .command).buttonStyle(.borderedProminent)
            }
        }
        .padding(24)
        .frame(width: 480)
        .onAppear {
            trigger = snippet?.trigger ?? ""
            expansion = snippet?.expansion ?? ""
        }
    }

    private func save() {
        let t = trigger.trimmingCharacters(in: .whitespacesAndNewlines)
        if let s = snippet, s.trigger == t, s.expansion == expansion { error = "No changes made."; return }
        if let problem = SnippetMatcher.validate(trigger: t, expansion: expansion, existing: model.snippets, editing: snippet?.id) {
            error = problem
            return
        }
        if let s = snippet, let i = model.snippets.firstIndex(where: { $0.id == s.id }) {
            model.snippets[i].trigger = t
            model.snippets[i].expansion = expansion
        } else {
            model.snippets.insert(Snippet(trigger: t, expansion: expansion), at: 0)
        }
        dismiss()
    }
}
