import SwiftUI
import VoiceFlowCore

struct StylePage: View {
    @Bindable var model: AppModel
    @State private var category: StyleCategory = .personal

    var body: some View {
        Page(width: Metrics.wideContentWidth, spacing: Spacing.md) {
            PageHeader(title: "Style", subtitle: "VoiceFlow matches its writing to the app you're in. Changes save instantly.")
            Label {
                Text("Styles apply to English dictation. Code editors and terminals always keep code formatting.")
            } icon: {
                Image(systemName: "info.circle").accessibilityHidden(true)
            }
            .font(TypeScale.callout).foregroundStyle(Palette.muted)

            TabStrip(items: StyleCategory.allCases, title: \.displayName, selection: $category)
            Text(category.appsHint).font(TypeScale.callout).foregroundStyle(Palette.muted)

            HStack(alignment: .top, spacing: Spacing.md) {
                ForEach(category.options) { style in
                    StyleCard(style: style, example: style.example(for: category),
                              selected: model.preferences.style(for: category) == style) {
                        model.preferences.styles[category] = style
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)

            RowDivider().padding(.vertical, Spacing.xs)

            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text("Auto Cleanup").font(TypeScale.sectionTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                Text("How much VoiceFlow edits what you say. Applies in every app, starting with your next dictation.")
                    .font(TypeScale.callout).foregroundStyle(Palette.muted)
            }
            HStack(alignment: .top, spacing: Spacing.md) {
                ForEach(CleanupLevel.allCases) { level in
                    CleanupCard(level: level, selected: model.preferences.cleanupLevel == level) {
                        model.preferences.cleanupLevel = level
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// A selectable card: a hairline border that becomes solid foreground when chosen.
private struct SelectableCard<Content: View>: View {
    let selected: Bool
    let title: String
    let select: () -> Void
    @ViewBuilder var content: Content
    @State private var hovering = false

    var body: some View {
        Button(action: select) {
            content
                .padding(Spacing.md)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .background(RoundedRectangle(cornerRadius: Radius.card, style: .continuous).fill(hovering ? Palette.surfaceRaised : Palette.surface))
                .overlay(RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                    .strokeBorder(selected ? Palette.primary : Palette.hairline, lineWidth: selected ? 2 : 1))
                .contentShape(RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        }
        .buttonStyle(.quiet(radius: Radius.card))
        .onHover { hovering = $0 }
        .accessibilityLabel(title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct StyleCard: View {
    let style: WritingStyle
    let example: String
    let selected: Bool
    let select: () -> Void

    var body: some View {
        SelectableCard(selected: selected, title: style.displayName, select: select) {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                HStack {
                    Text(style.displayName).font(TypeScale.cardTitle).foregroundStyle(Palette.foreground)
                    Spacer()
                    CardCheck(selected: selected)
                }
                Text(style.summary).font(TypeScale.caption).foregroundStyle(Palette.muted)
                Text(example)
                    .font(TypeScale.callout).foregroundStyle(Palette.foreground)
                    .padding(Spacing.sm)
                    .frame(maxWidth: .infinity, minHeight: 90, alignment: .topLeading)
                    .insetSurface()
            }
        }
    }
}

private struct CleanupCard: View {
    let level: CleanupLevel
    let selected: Bool
    let select: () -> Void

    var body: some View {
        SelectableCard(selected: selected, title: level.displayName, select: select) {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                HStack(spacing: Spacing.xs) {
                    Text(level.displayName).font(TypeScale.cardTitle).foregroundStyle(Palette.foreground)
                    if level == .light { Badge(text: "Default") }
                    Spacer()
                    CardCheck(selected: selected)
                }
                Text(level.summary).font(TypeScale.caption).foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text("You said").font(TypeScale.label).foregroundStyle(Palette.muted)
                    Text(level.example.said).font(TypeScale.caption).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                    Text("VoiceFlow types").font(TypeScale.label).foregroundStyle(Palette.muted).padding(.top, Spacing.xxs)
                    Text(level.example.typed).font(TypeScale.callout).foregroundStyle(Palette.foreground).fixedSize(horizontal: false, vertical: true)
                }
                .padding(Spacing.sm)
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .insetSurface()
            }
        }
    }
}
