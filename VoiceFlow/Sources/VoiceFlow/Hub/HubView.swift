import Observation
import SwiftUI
import VoiceFlowCore

enum HubPage: String, CaseIterable, Identifiable {
    case home, meetings, insights, dictionary, snippets, style, scratchpad
    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: "Home"
        case .meetings: "Meetings"
        case .insights: "Insights"
        case .dictionary: "Dictionary"
        case .snippets: "Snippets"
        case .style: "Style"
        case .scratchpad: "Scratchpad"
        }
    }

    var symbol: String {
        switch self {
        case .home: "house"
        case .meetings: "person.2.wave.2"
        case .insights: "chart.bar"
        case .dictionary: "character.book.closed"
        case .snippets: "text.badge.plus"
        case .style: "textformat"
        case .scratchpad: "note.text"
        }
    }
}

@MainActor
@Observable
final class HubSelection {
    var page: HubPage = .home
}

struct HubView: View {
    @Bindable var model: AppModel
    @Bindable var selection: HubSelection

    var body: some View {
        NavigationSplitView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 8) {
                    LogoMark(size: 26)
                    Text("VoiceFlow").font(.system(size: 17, weight: .bold, design: .rounded))
                }
                .padding(.horizontal, 16).padding(.top, 8).padding(.bottom, 12)
                List(selection: Binding(get: { selection.page }, set: { if let p = $0 { selection.page = p } })) {
                    ForEach(HubPage.allCases) { page in
                        Label(page.title, systemImage: page.symbol).tag(page)
                    }
                }
                .listStyle(.sidebar)
                Divider()
                VStack(alignment: .leading, spacing: 2) {
                    SidebarButton(title: "Settings", symbol: "gearshape") { WindowManager.shared.showSettings() }
                    SidebarButton(title: "Help", symbol: "questionmark.circle") { WindowManager.shared.showHelp() }
                }
                .padding(8)
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 200, max: 240)
        } detail: {
            Group {
                switch selection.page {
                case .home: HomePage(model: model)
                case .meetings: MeetingsPage(model: model, notetaker: model.notetaker)
                case .insights: InsightsPage(model: model)
                case .dictionary: DictionaryPage(model: model)
                case .snippets: SnippetsPage(model: model)
                case .style: StylePage(model: model)
                case .scratchpad: ScratchpadView(model: model, compact: true)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }
}

private struct SidebarButton: View {
    let title: String
    let symbol: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 6).fill(hovering ? Color.primary.opacity(0.06) : .clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

// MARK: Shared page pieces

struct PageHeader<Trailing: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.system(size: 26, weight: .bold))
                if let subtitle { Text(subtitle).foregroundStyle(.secondary) }
            }
            Spacer()
            trailing
        }
    }
}

extension PageHeader where Trailing == EmptyView {
    init(title: String, subtitle: String? = nil) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14).fill(Color.primary.opacity(0.04)))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Color.primary.opacity(0.08)))
    }
}

struct KeyCap: View {
    let text: String
    var body: some View {
        Text(text)
            .font(.system(size: 12, weight: .semibold, design: .rounded))
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: 5).fill(Color.primary.opacity(0.08)))
            .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(Color.primary.opacity(0.15)))
    }
}

struct TabStrip<T: Hashable & Identifiable>: View {
    let items: [T]
    let title: (T) -> String
    @Binding var selection: T

    var body: some View {
        HStack(spacing: 4) {
            ForEach(items) { item in
                Button { selection = item } label: {
                    Text(title(item))
                        .font(.system(size: 13, weight: selection == item ? .semibold : .regular))
                        .padding(.horizontal, 12).padding(.vertical, 6)
                        .background(Capsule().fill(selection == item ? Color.primary.opacity(0.1) : .clear))
                }
                .buttonStyle(.plain)
            }
        }
    }
}

// MARK: Home

struct HomePage: View {
    let model: AppModel
    @State private var query = ""

    private var entries: [HistoryEntry] {
        query.isEmpty ? model.history : model.history.filter {
            $0.final.localizedCaseInsensitiveContains(query) || $0.raw.localizedCaseInsensitiveContains(query)
                || $0.appName.localizedCaseInsensitiveContains(query)
        }
    }

    private var grouped: [(String, [HistoryEntry])] {
        let calendar = Calendar.current
        var groups: [(String, [HistoryEntry])] = []
        for entry in entries {
            let label: String
            if calendar.isDateInToday(entry.date) { label = "Today" }
            else if calendar.isDateInYesterday(entry.date) { label = "Yesterday" }
            else { label = entry.date.formatted(.dateTime.weekday(.wide).month(.wide).day()) }
            if groups.last?.0 == label { groups[groups.count - 1].1.append(entry) } else { groups.append((label, [entry])) }
        }
        return groups
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .top, spacing: 16) {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Welcome back").font(.system(size: 28, weight: .bold))
                        HStack(spacing: 6) {
                            Text("Hold")
                            KeyCap(text: model.preferences.hotkey.shortName.lowercased())
                            Text("to dictate in any app. Add")
                            KeyCap(text: "space")
                            Text("for hands-free.")
                        }
                        .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button { WindowManager.shared.showHub(.insights) } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("\(model.stats.wordsThisWeek)").font(.system(size: 24, weight: .bold, design: .rounded))
                            Text("words this week").font(.callout).foregroundStyle(.secondary)
                            Text("No weekly cap").font(.caption).foregroundStyle(.secondary)
                        }
                        .padding(14)
                        .frame(width: 170, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 14).fill(Color.primary.opacity(0.05)))
                    }
                    .buttonStyle(.plain)
                }

                if !model.setupProblems.isEmpty {
                    Card {
                        VStack(alignment: .leading, spacing: 8) {
                            Label("Finish setting up", systemImage: "exclamationmark.triangle.fill").font(.headline).foregroundStyle(.orange)
                            ForEach(model.setupProblems, id: \.self) { Text("• \($0)") }
                            Button("Open setup") { WindowManager.shared.showOnboarding() }
                        }
                    }
                }

                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Search your transcripts", text: $query).textFieldStyle(.plain)
                }
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.05)))

                if model.history.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "waveform").font(.system(size: 28)).foregroundStyle(.secondary)
                        Text("Your dictations will show up here.").foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 40)
                }

                ForEach(grouped, id: \.0) { label, items in
                    VStack(alignment: .leading, spacing: 0) {
                        Text(label.uppercased()).font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.bottom, 6)
                        VStack(spacing: 0) {
                            ForEach(items) { entry in
                                TranscriptRow(model: model, entry: entry)
                                if entry.id != items.last?.id { Divider() }
                            }
                        }
                        .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.03)))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.07)))
                    }
                }
            }
            .padding(28)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct TranscriptRow: View {
    let model: AppModel
    let entry: HistoryEntry
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Text(entry.date, format: .dateTime.hour().minute())
                .font(.system(size: 12, design: .monospaced)).foregroundStyle(.secondary)
                .frame(width: 64, alignment: .leading)
            VStack(alignment: .leading, spacing: 3) {
                Text(entry.final.isEmpty ? "(no text)" : entry.final).textSelection(.enabled)
                HStack(spacing: 6) {
                    if entry.kind == .command { Label("Command", systemImage: "wand.and.stars").labelStyle(.titleAndIcon) }
                    if !entry.appName.isEmpty { Text(entry.appName) }
                    if entry.outcome == "failed" { Text("Failed").foregroundStyle(.red) }
                    if entry.flagged == true { Image(systemName: "flag.fill").foregroundStyle(.orange) }
                }
                .font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            HStack(spacing: 2) {
                IconButton(symbol: "doc.on.doc", help: "Copy") { TextInserter.copyToClipboard(entry.final) }
                IconButton(symbol: entry.flagged == true ? "flag.fill" : "flag", help: "Flag this transcript") { model.toggleFlag(entry.id) }
                Menu {
                    Button("Copy") { TextInserter.copyToClipboard(entry.final) }
                    Button("Undo AI edit (copy what you said)") { TextInserter.copyToClipboard(entry.raw) }
                    Divider()
                    Button("Delete", role: .destructive) { model.deleteHistory([entry.id]) }
                } label: { Image(systemName: "ellipsis") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
            }
            .opacity(hovering ? 1 : 0)
        }
        .padding(.horizontal, 14).padding(.vertical, 11)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

struct IconButton: View {
    let symbol: String
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol).frame(width: 24, height: 22)
        }
        .buttonStyle(.borderless)
        .help(help)
    }
}
