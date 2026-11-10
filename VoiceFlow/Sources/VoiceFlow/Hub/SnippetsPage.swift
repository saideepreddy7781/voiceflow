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
        Page(spacing: Spacing.md) {
            PageHeader(title: "Snippets", subtitle: "Say a short cue and VoiceFlow types the full text, on its own or inside a sentence.") {
                HStack(spacing: Spacing.xs) {
                    Button("Import") { importFile() }.buttonStyle(.vfSecondary)
                    Button { adding = true } label: { Label("Add snippet", systemImage: "plus") }.buttonStyle(.vfPrimary)
                }
            }
            if let notice { Text(notice).font(TypeScale.callout).foregroundStyle(Palette.muted) }

            if model.snippets.isEmpty {
                EmptyState(symbol: "list.bullet.rectangle", text: "No snippets yet. Try one for your calendar link, address or a reply you send often.")
            } else {
                VStack(spacing: 0) {
                    ForEach(model.snippets) { snippet in
                        SnippetRow(snippet: snippet, onEdit: { editing = snippet }, onDelete: { deleting = snippet })
                        if snippet.id != model.snippets.last?.id { RowDivider() }
                    }
                }
                .rowGroupSurface()
            }
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
        HStack(alignment: .top, spacing: Spacing.sm) {
            Text(snippet.trigger).font(TypeScale.bodyMedium).foregroundStyle(Palette.foreground)
                .lineLimit(2).frame(width: 180, alignment: .leading)
            Image(systemName: "arrow.right").font(.system(size: 11)).foregroundStyle(Palette.subtle).padding(.top, 4).accessibilityLabel("types")
            Text(snippet.expansion).font(TypeScale.body).foregroundStyle(Palette.muted).lineLimit(3)
                .frame(maxWidth: .infinity, alignment: .leading)
            HoverActions(visible: hovering) {
                IconButton(symbol: "pencil", help: "Edit", action: onEdit)
                IconButton(symbol: "trash", help: "Delete", action: onDelete)
            }
        }
        .padding(.horizontal, Spacing.md).padding(.vertical, 12)
        .background(hovering ? Palette.hover : Color.clear)
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
    @FocusState private var editorFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            Text(snippet == nil ? "Add snippet" : "Edit snippet").font(TypeScale.sectionTitle).foregroundStyle(Palette.foreground)
            Text("Cue").font(TypeScale.calloutMedium).foregroundStyle(Palette.foreground)
            InputField(prompt: "e.g. my calendar link", text: $trigger)
            Text("Text to type").font(TypeScale.calloutMedium).foregroundStyle(Palette.foreground)
            TextEditor(text: $expansion)
                .font(TypeScale.body)
                .scrollContentBackground(.hidden)
                .focused($editorFocused)
                .padding(Spacing.xs)
                .frame(height: 140)
                .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(Palette.surface))
                .overlay(RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .strokeBorder(editorFocused ? Palette.focusRing : Palette.hairlineStrong, lineWidth: editorFocused ? 2 : 1))
                .accessibilityLabel("Text to type")
            Text("\(trigger.count)/\(Snippet.maxTrigger) · \(expansion.count)/\(Snippet.maxExpansion)")
                .font(TypeScale.caption).monospacedDigit().foregroundStyle(Palette.muted)
            if let error { AttentionLine(text: error) }
            HStack(spacing: Spacing.xs) {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).buttonStyle(.vfSecondary)
                Button(snippet == nil ? "Add snippet" : "Save changes", action: save)
                    .keyboardShortcut(.return, modifiers: .command).buttonStyle(.vfPrimary)
            }
        }
        .padding(Spacing.lg)
        .frame(width: 480)
        .background(Palette.window)
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
