import SwiftUI
import VoiceFlowCore

struct StylePage: View {
    @Bindable var model: AppModel
    @State private var category: StyleCategory = .personal

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "Style", subtitle: "VoiceFlow matches its writing to the app you're in. Changes save instantly.")
                Label("Styles apply to English dictation. Code editors and terminals always keep code formatting.", systemImage: "info.circle")
                    .font(.callout).foregroundStyle(.secondary)

                TabStrip(items: StyleCategory.allCases, title: \.displayName, selection: $category)
                Text(category.appsHint).font(.callout).foregroundStyle(.secondary)

                HStack(alignment: .top, spacing: 14) {
                    ForEach(category.options) { style in
                        StyleCard(style: style, example: style.example(for: category),
                                  selected: model.preferences.style(for: category) == style) {
                            model.preferences.styles[category] = style
                        }
                    }
                }

                Divider().padding(.vertical, 6)

                HStack(spacing: 8) {
                    Text("Auto Cleanup").font(.title3.bold())
                    Text("Beta").font(.caption.weight(.semibold)).padding(.horizontal, 6).padding(.vertical, 2)
                        .background(Capsule().fill(Color.accentColor.opacity(0.15)))
                }
                Text("How much VoiceFlow edits what you say. Applies in every app, starting with your next dictation.")
                    .foregroundStyle(.secondary)
                HStack(alignment: .top, spacing: 14) {
                    ForEach(CleanupLevel.allCases) { level in
                        CleanupCard(level: level, selected: model.preferences.cleanupLevel == level) {
                            model.preferences.cleanupLevel = level
                        }
                    }
                }
            }
            .padding(28)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct StyleCard: View {
    let style: WritingStyle
    let example: String
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text(style.displayName).font(.headline)
                    Spacer()
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(selected ? Color.accentColor : .secondary)
                }
                Text(style.summary).font(.caption).foregroundStyle(.secondary)
                Text(example)
                    .font(.callout)
                    .padding(10)
                    .frame(maxWidth: .infinity, minHeight: 90, alignment: .topLeading)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.05)))
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14).fill(selected ? Color.accentColor.opacity(0.08) : Color.primary.opacity(0.03)))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(selected ? Color.accentColor : Color.primary.opacity(0.1), lineWidth: selected ? 2 : 1))
            .contentShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
    }
}

private struct CleanupCard: View {
    let level: CleanupLevel
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(level.displayName).font(.headline)
                    if level == .light { Text("Default").font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(selected ? Color.accentColor : .secondary)
                }
                Text(level.summary).font(.caption).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 4) {
                    Text("You said").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    Text(level.example.said).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                    Text("VoiceFlow types").font(.caption2.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    Text(level.example.typed).font(.callout).lineLimit(4)
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.05)))
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14).fill(selected ? Color.accentColor.opacity(0.08) : Color.primary.opacity(0.03)))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(selected ? Color.accentColor : Color.primary.opacity(0.1), lineWidth: selected ? 2 : 1))
            .contentShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
    }
}
