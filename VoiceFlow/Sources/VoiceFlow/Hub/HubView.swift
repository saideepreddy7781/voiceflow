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
        case .meetings: "person.2"
        case .insights: "chart.bar"
        case .dictionary: "book"
        case .snippets: "list.bullet.rectangle"
        case .style: "textformat"
        case .scratchpad: "doc.text"
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
    /// Which tab a meeting opens on. Screenshot mode asks for the transcript.
    var meetingsTab = 0

    var body: some View {
        Group {
            if AppRuntime.isDemo {
                // A split view's sidebar is a window-server material, which an offscreen capture can't draw.
                // Same sidebar and pages, side by side.
                HStack(spacing: 0) {
                    HubSidebar(selection: selection).frame(width: Metrics.sidebarWidth)
                    Rectangle().fill(Palette.hairline).frame(width: 1)
                    detail
                }
                .background(Palette.window)
            } else {
                NavigationSplitView {
                    HubSidebar(selection: selection)
                        .navigationSplitViewColumnWidth(min: 190, ideal: Metrics.sidebarWidth, max: 250)
                } detail: {
                    detail
                }
            }
        }
        .tint(Palette.primary)
        .focusEffectDisabled()
    }

    private var detail: some View {
        Group {
            switch selection.page {
            case .home: HomePage(model: model)
            case .meetings: MeetingsPage(model: model, notetaker: model.notetaker, initialTab: meetingsTab)
            case .insights: InsightsPage(model: model)
            case .dictionary: DictionaryPage(model: model)
            case .snippets: SnippetsPage(model: model)
            case .style: StylePage(model: model)
            case .scratchpad: ScratchpadView(model: model, compact: true)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Palette.window)
    }
}

/// The real sidebar column: its background is left empty so the system material shows through.
/// Selection is monochrome, so it is drawn here; a List would paint it in the system accent.
private struct HubSidebar: View {
    @Bindable var selection: HubSelection
    @FocusState private var listFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Spacing.xs) {
                LogoMark(size: 26)
                Text("VoiceFlow").font(.system(size: 16, weight: .bold)).foregroundStyle(Palette.foreground)
            }
            .padding(.horizontal, Spacing.md).padding(.top, AppRuntime.isDemo ? Spacing.lg : Spacing.xs).padding(.bottom, Spacing.md)
            .accessibilityElement(children: .combine)

            VStack(spacing: 2) {
                ForEach(HubPage.allCases) { page in
                    SidebarRow(title: page.title, symbol: page.symbol, selected: selection.page == page,
                               focused: listFocused && selection.page == page && KeyboardFocus.showsRing) { selection.page = page }
                }
            }
            .padding(.horizontal, 10)
            .focusable()
            .focused($listFocused)
            .focusEffectDisabled()
            .onMoveCommand(perform: moveSelection)

            Spacer(minLength: Spacing.md)
            Rectangle().fill(Palette.hairline).frame(height: 1).padding(.horizontal, Spacing.md).padding(.bottom, Spacing.xs)
            VStack(spacing: 2) {
                SidebarRow(title: "Settings", symbol: "gearshape", selected: false) { WindowManager.shared.showSettings() }
                SidebarRow(title: "Help", symbol: "questionmark.circle", selected: false) { WindowManager.shared.showHelp() }
            }
            .padding(.horizontal, 10).padding(.bottom, Spacing.sm)
        }
        // A rendered PNG has no desktop behind it, so demo mode paints the sidebar colour instead of a material.
        .background(AppRuntime.isDemo ? Palette.sidebar : Color.clear)
    }

    private func moveSelection(_ direction: MoveCommandDirection) {
        let pages = HubPage.allCases
        guard let i = pages.firstIndex(of: selection.page) else { return }
        switch direction {
        case .up where i > 0: selection.page = pages[i - 1]
        case .down where i < pages.count - 1: selection.page = pages[i + 1]
        default: break
        }
    }
}

struct SidebarRow: View {
    let title: String
    let symbol: String
    let selected: Bool
    var focused = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol).font(.system(size: 13)).frame(width: 22).accessibilityHidden(true)
                Text(title).font(TypeScale.body.weight(selected ? .semibold : .regular)).lineLimit(1)
                Spacer(minLength: 0)
            }
            .foregroundStyle(selected || hovering ? Palette.foreground : Palette.muted)
            .padding(.horizontal, 10).frame(height: 32)
            .background(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous)
                .fill(selected ? Palette.selected : (hovering ? Palette.hover : .clear)))
            .overlay(alignment: .leading) {
                if selected {
                    RoundedRectangle(cornerRadius: 1.5).fill(Palette.primary).frame(width: 3, height: 16).padding(.leading, 2)
                }
            }
            .overlay(FocusRing(radius: Radius.overlay, visible: focused))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusable(false)
        .onHover { hovering = $0 }
        .accessibilityLabel(title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
