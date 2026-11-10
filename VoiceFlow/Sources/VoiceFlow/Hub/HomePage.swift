import SwiftUI
import VoiceFlowCore

struct HomePage: View {
    @Bindable var model: AppModel
    @State private var query = ""

    private var entries: [HistoryEntry] {
        query.isEmpty ? model.history : model.history.filter {
            $0.final.localizedCaseInsensitiveContains(query) || $0.raw.localizedCaseInsensitiveContains(query)
                || $0.appName.localizedCaseInsensitiveContains(query)
        }
    }

    /// Newest 30 days, so a long history doesn't lay out thousands of rows.
    private var grouped: [(String, [HistoryEntry])] {
        let calendar = Calendar.current
        var groups: [(String, [HistoryEntry])] = []
        for entry in entries {
            let label: String
            if calendar.isDate(entry.date, inSameDayAs: model.now) { label = "Today" }
            else if let yesterday = calendar.date(byAdding: .day, value: -1, to: model.now), calendar.isDate(entry.date, inSameDayAs: yesterday) { label = "Yesterday" }
            else { label = entry.date.formatted(.dateTime.weekday(.wide).month(.wide).day()) }
            if groups.last?.0 == label { groups[groups.count - 1].1.append(entry) }
            else if groups.count == 30 { break }
            else { groups.append((label, [entry])) }
        }
        return groups
    }

    private var greeting: String {
        switch Calendar.current.component(.hour, from: model.now) {
        case ..<12: "Good morning"
        case ..<17: "Good afternoon"
        default: "Good evening"
        }
    }

    private var hotkey: String { model.preferences.hotkey.shortName.lowercased() }

    var body: some View {
        Page {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                Text(greeting).font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                HStack(spacing: Spacing.xs) {
                    Text("Hold")
                    KeyCap(text: hotkey)
                    Text("to dictate in any app. Add")
                    KeyCap(text: "space")
                    Text("for hands-free.")
                }
                .font(TypeScale.body).foregroundStyle(Palette.muted)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Hold \(hotkey) to dictate in any app. Add space for hands-free.")
            }

            if let release = model.updates.available, model.preferences.checkForUpdates,
               release.version != model.preferences.dismissedUpdateVersion {
                InlineNotice(symbol: "arrow.down.circle",
                             text: "VoiceFlow \(release.version) is available.",
                             onDismiss: { model.preferences.dismissedUpdateVersion = release.version }) {
                    OutboundLink(title: "View release", url: release.url)
                }
            }

            stats

            if !model.setupProblems.isEmpty { setupCard }

            SearchField(prompt: "Search your transcripts", text: $query)

            if model.history.isEmpty {
                EmptyState(symbol: "waveform", text: "Your dictations will show up here. Hold \(hotkey) in any app to make the first one.")
            } else if grouped.isEmpty {
                EmptyState(symbol: "magnifyingglass", text: "No transcripts match “\(query)”.")
            }

            ForEach(Array(grouped.enumerated()), id: \.element.0) { groupIndex, group in
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    SectionLabel(text: group.0)
                    VStack(spacing: 0) {
                        ForEach(Array(group.1.enumerated()), id: \.element.id) { index, entry in
                            TranscriptRow(model: model, entry: entry,
                                          showHover: model.isDemo && groupIndex == 0 && index == 0)
                            if entry.id != group.1.last?.id { RowDivider() }
                        }
                    }
                    .rowGroupSurface()
                }
            }
        }
    }

    private var stats: some View {
        let today = DayUsage.compute(model.history, on: model.now)
        let week = model.stats.wordsThisWeek
        return HStack(alignment: .top, spacing: Spacing.md) {
            StatCard(label: "Words today", value: today.words.formatted(), detail: week > 0 ? "\(week.formatted()) this week" : nil)
            StatCard(label: "Dictations today", value: today.dictations.formatted(),
                     detail: model.stats.dictations > 0 ? "\(model.stats.dictations.formatted()) in total" : nil)
            StatCard(label: "Typing time saved", value: today.timeSavedLabel, detail: "Compared with 40 wpm typing")
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private var setupCard: some View {
        Card {
            HStack(alignment: .top, spacing: Spacing.md) {
                Image(systemName: "exclamationmark.triangle")
                    .font(.system(size: 14))
                    .foregroundStyle(Palette.signal)
                    .frame(width: 32, height: 32)
                    .background(Circle().fill(Palette.signalSoft))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text("Finish setting up").font(TypeScale.cardTitle).foregroundStyle(Palette.foreground)
                    ForEach(model.setupProblems, id: \.self) { problem in
                        Text(problem).font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                }
                Spacer(minLength: Spacing.md)
                Button("Open setup") { WindowManager.shared.showOnboarding() }.buttonStyle(.vfPrimary)
            }
        }
    }
}

private struct TranscriptRow: View {
    let model: AppModel
    let entry: HistoryEntry
    var showHover = false
    @State private var hovering = false

    var body: some View {
        let highlighted = hovering || showHover
        HStack(alignment: .top, spacing: Spacing.md) {
            Text(entry.date, format: .dateTime.hour().minute())
                .font(TypeScale.caption).monospacedDigit().foregroundStyle(Palette.muted)
                .frame(width: 66, alignment: .leading)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 5) {
                Text(entry.final.isEmpty ? "(no text)" : entry.final)
                    .font(TypeScale.body).foregroundStyle(Palette.foreground)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                metaRow
            }
            Spacer(minLength: Spacing.xs)
            HoverActions(visible: highlighted) {
                IconButton(symbol: "doc.on.doc", help: "Copy") { TextInserter.copyToClipboard(entry.final) }
                IconButton(symbol: entry.flagged == true ? "flag.fill" : "flag", help: entry.flagged == true ? "Remove flag" : "Flag this transcript") {
                    model.toggleFlag(entry.id)
                }
                Menu {
                    Button("Copy") { TextInserter.copyToClipboard(entry.final) }
                    Button("Undo AI edit (copy what you said)") { TextInserter.copyToClipboard(entry.raw) }
                    Divider()
                    Button("Delete", role: .destructive) { model.deleteHistory([entry.id]) }
                } label: { Image(systemName: "ellipsis") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .frame(width: 28, height: 28)
                .help("More actions")
                .accessibilityLabel("More actions")
            }
        }
        .padding(.horizontal, Spacing.md).padding(.vertical, 12)
        .background(highlighted ? Palette.hover : Color.clear)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }

    @ViewBuilder private var metaRow: some View {
        let meta = HStack(spacing: Spacing.xs) {
            if entry.kind == .command { Badge(text: "Command") }
            if !entry.appName.isEmpty { Text(entry.appName).foregroundStyle(Palette.muted) }
            if entry.outcome == "failed" { Text("Failed").foregroundStyle(Palette.signal) }
            if entry.flagged == true {
                Image(systemName: "flag").foregroundStyle(Palette.muted).accessibilityLabel("Flagged")
            }
        }
        .font(TypeScale.caption)
        if entry.kind == .command || !entry.appName.isEmpty || entry.outcome == "failed" || entry.flagged == true { meta }
    }
}
