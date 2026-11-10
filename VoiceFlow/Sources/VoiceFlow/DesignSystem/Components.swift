import AppKit
import SwiftUI
import VoiceFlowCore

// MARK: Buttons

/// Filled primary, outlined secondary, or filled signal (recording only).
struct VFButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, signal }
    var kind: Kind = .secondary
    var fullWidth = false
    var compact = false

    func makeBody(configuration: Configuration) -> some View {
        StyledBody(configuration: configuration, kind: kind, fullWidth: fullWidth, compact: compact)
    }

    private struct StyledBody: View {
        let configuration: Configuration
        let kind: Kind
        let fullWidth: Bool
        let compact: Bool
        @Environment(\.isEnabled) private var enabled
        @Environment(\.isFocused) private var focused
        @State private var hovering = false

        var body: some View {
            configuration.label
                .font(compact ? TypeScale.calloutMedium : TypeScale.bodyMedium)
                .lineLimit(1)
                .foregroundStyle(foreground)
                .padding(.horizontal, compact ? Spacing.sm : Spacing.md)
                .frame(maxWidth: fullWidth ? .infinity : nil, minHeight: compact ? 28 : 34)
                .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(fill))
                .overlay(RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .strokeBorder(kind == .secondary ? Palette.hairlineStrong : .clear))
                .overlay(FocusRing(radius: Radius.control, visible: focused))
                .opacity(enabled ? 1 : 0.45)
                .contentShape(RoundedRectangle(cornerRadius: Radius.control, style: .continuous))
                .onHover { hovering = $0 }
        }

        private var foreground: Color {
            switch kind {
            case .primary: Palette.onPrimary
            case .signal: .white
            case .secondary: Palette.foreground
            }
        }

        private var fill: Color {
            switch kind {
            case .primary: configuration.isPressed ? Palette.primaryPressed : (hovering ? Palette.primaryHover : Palette.primary)
            case .signal: Palette.signal.opacity(configuration.isPressed ? 0.8 : (hovering ? 0.9 : 1))
            case .secondary: configuration.isPressed ? Palette.pressed : (hovering ? Palette.hover : Palette.surface)
            }
        }
    }
}

extension ButtonStyle where Self == VFButtonStyle {
    static var vfPrimary: VFButtonStyle { VFButtonStyle(kind: .primary) }
    static var vfSecondary: VFButtonStyle { VFButtonStyle(kind: .secondary) }
    static var vfSignal: VFButtonStyle { VFButtonStyle(kind: .signal) }
    static func vf(_ kind: VFButtonStyle.Kind, fullWidth: Bool = false, compact: Bool = false) -> VFButtonStyle {
        VFButtonStyle(kind: kind, fullWidth: fullWidth, compact: compact)
    }
}

/// A text link in the app's foreground colour, underlined on hover.
struct VFLinkStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { StyledBody(configuration: configuration) }

    private struct StyledBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var focused
        @State private var hovering = false

        var body: some View {
            configuration.label
                .font(TypeScale.calloutMedium)
                .foregroundStyle(Palette.foreground.opacity(configuration.isPressed ? 0.6 : 1))
                .underline(hovering)
                .overlay(FocusRing(radius: 3, visible: focused).padding(-2))
                .onHover { hovering = $0 }
        }
    }
}

extension ButtonStyle where Self == VFLinkStyle {
    static var vfLink: VFLinkStyle { VFLinkStyle() }
}

/// A custom-drawn button (tab, chip, row, card) with the app's focus ring and a slight press.
struct QuietButtonStyle: ButtonStyle {
    var radius: CGFloat = Radius.control

    func makeBody(configuration: Configuration) -> some View {
        StyledBody(configuration: configuration, radius: radius)
    }

    private struct StyledBody: View {
        let configuration: Configuration
        let radius: CGFloat
        @Environment(\.isFocused) private var focused

        var body: some View {
            configuration.label
                .opacity(configuration.isPressed ? 0.75 : 1)
                .overlay(FocusRing(radius: radius, visible: focused))
        }
    }
}

extension ButtonStyle where Self == QuietButtonStyle {
    static func quiet(radius: CGFloat = Radius.control) -> QuietButtonStyle { QuietButtonStyle(radius: radius) }
}

struct FocusRing: View {
    let radius: CGFloat
    let visible: Bool
    var body: some View {
        RoundedRectangle(cornerRadius: radius, style: .continuous)
            .strokeBorder(Palette.focusRing, lineWidth: 2)
            .opacity(visible ? 1 : 0)
    }
}

/// An external link with the small arrow out of the box.
struct OutboundLink: View {
    let title: String
    let url: URL

    var body: some View {
        Button { NSWorkspace.shared.open(url) } label: {
            HStack(spacing: Spacing.xxs) {
                Text(title)
                Image(systemName: "arrow.up.right.square").font(.system(size: 11)).accessibilityHidden(true)
            }
        }
        .buttonStyle(.vfLink)
        .accessibilityAddTraits(.isLink)
        .help(url.absoluteString)
    }
}

/// An icon with no border. The tooltip doubles as its accessibility label.
struct IconButton: View {
    let symbol: String
    let help: String
    var size: CGFloat = 28
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 13)).frame(width: size, height: size)
        }
        .buttonStyle(IconButtonStyle())
        .help(help)
        .accessibilityLabel(help)
    }
}

struct IconButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { StyledBody(configuration: configuration) }

    private struct StyledBody: View {
        let configuration: Configuration
        @Environment(\.isEnabled) private var enabled
        @Environment(\.isFocused) private var focused
        @State private var hovering = false

        var body: some View {
            configuration.label
                .foregroundStyle(enabled ? (hovering ? Palette.foreground : Palette.muted) : Palette.subtle.opacity(0.5))
                .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .fill(configuration.isPressed ? Palette.pressed : (hovering ? Palette.hover : .clear)))
                .overlay(FocusRing(radius: Radius.control, visible: focused))
                .contentShape(Rectangle())
                .onHover { hovering = $0 }
        }
    }
}

// MARK: Surfaces

struct Card<Content: View>: View {
    var padding: CGFloat = Spacing.lg
    /// Fill the row's height, for cards side by side.
    var fillsHeight = false
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, maxHeight: fillsHeight ? .infinity : nil, alignment: .topLeading)
            .cardSurface()
    }
}

extension View {
    func cardSurface(radius: CGFloat = Radius.card) -> some View {
        background(RoundedRectangle(cornerRadius: radius, style: .continuous).fill(Palette.surface))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(Palette.hairline))
    }

    /// Rows sitting in one card. Clipped so a hovered row keeps the card's corners.
    func rowGroupSurface() -> some View {
        clipShape(RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
            .background(RoundedRectangle(cornerRadius: Radius.card, style: .continuous).fill(Palette.surface))
            .overlay(RoundedRectangle(cornerRadius: Radius.card, style: .continuous).strokeBorder(Palette.hairline))
    }

    func insetSurface() -> some View {
        background(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous).fill(Palette.surfaceInset))
    }
}

struct RowDivider: View {
    var body: some View { Rectangle().fill(Palette.hairline).frame(height: 1).accessibilityHidden(true) }
}

struct StatCard: View {
    let label: String
    let value: String
    var detail: String? = nil
    var large = false

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            Text(label).font(TypeScale.callout).foregroundStyle(Palette.muted).lineLimit(1)
            Text(value).font(large ? TypeScale.statLarge : TypeScale.statValue).foregroundStyle(Palette.foreground)
                .monospacedDigit().lineLimit(1).minimumScaleFactor(0.7)
            if let detail { Text(detail).font(TypeScale.caption).foregroundStyle(Palette.muted).lineLimit(2).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(.horizontal, Spacing.lg).padding(.vertical, Spacing.md + 2)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .cardSurface()
        .accessibilityElement(children: .combine)
    }
}

// MARK: Text pieces

struct KeyCap: View {
    let text: String
    var body: some View {
        Text(text)
            .font(TypeScale.label)
            .foregroundStyle(Palette.foreground)
            .padding(.horizontal, Spacing.xs).padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(Palette.surface))
            .overlay(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).strokeBorder(Palette.hairlineStrong))
            .fixedSize()
    }
}

struct Badge: View {
    enum Tone { case neutral, inverse }
    let text: String
    var tone: Tone = .neutral

    var body: some View {
        Text(text)
            .font(TypeScale.label)
            .foregroundStyle(tone == .inverse ? Palette.onPrimary : Palette.foreground)
            .lineLimit(1)
            .padding(.horizontal, Spacing.xs).padding(.vertical, 2)
            .background(Capsule().fill(tone == .inverse ? Palette.primary : Palette.selected))
            .fixedSize()
    }
}

struct RecordingDot: View {
    var size: CGFloat = 8
    var body: some View {
        Circle().fill(Palette.signal).frame(width: size, height: size).accessibilityHidden(true)
    }
}

/// "Today" above a group of rows.
struct SectionLabel: View {
    let text: String
    var body: some View {
        Text(text).font(TypeScale.label).foregroundStyle(Palette.muted).accessibilityAddTraits(.isHeader)
    }
}

struct SectionHeading: View {
    let text: String
    var body: some View {
        Text(text).font(TypeScale.cardTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
    }
}

/// A short line in the signal colour with an icon, for things that need attention.
struct AttentionLine: View {
    let text: String
    var symbol = "exclamationmark.triangle"
    var body: some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: symbol).accessibilityHidden(true) }
            .font(TypeScale.callout)
            .foregroundStyle(Palette.signal)
    }
}

struct EmptyState<Action: View>: View {
    let symbol: String
    let text: String
    @ViewBuilder var action: Action

    var body: some View {
        VStack(spacing: Spacing.sm) {
            Image(systemName: symbol).font(.system(size: 30, weight: .light)).foregroundStyle(Palette.subtle).accessibilityHidden(true)
            Text(text).font(TypeScale.callout).foregroundStyle(Palette.muted)
                .multilineTextAlignment(.center).frame(maxWidth: 420)
                .fixedSize(horizontal: false, vertical: true)
            action.padding(.top, Spacing.xxs)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, Spacing.xxl)
    }
}

extension EmptyState where Action == EmptyView {
    init(symbol: String, text: String) { self.init(symbol: symbol, text: text) { EmptyView() } }
}

// MARK: Page scaffolding

struct PageHeader<Trailing: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.lg) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                if let subtitle { Text(subtitle).font(TypeScale.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer(minLength: 0)
            trailing
        }
    }
}

extension PageHeader where Trailing == EmptyView {
    init(title: String, subtitle: String? = nil) { self.init(title: title, subtitle: subtitle) { EmptyView() } }
}

/// A scrolling page with the standard padding and reading width.
struct Page<Content: View>: View {
    var width: CGFloat = Metrics.contentWidth
    var spacing: CGFloat = Spacing.lg
    @ViewBuilder var content: Content

    var body: some View {
        VerticalScroll {
            VStack(alignment: .leading, spacing: spacing) { content }
                .padding(.horizontal, Metrics.pagePadding)
                .padding(.top, Spacing.xl).padding(.bottom, Metrics.pagePadding)
                .frame(maxWidth: width + 2 * Metrics.pagePadding, alignment: .leading)
                .frame(maxWidth: .infinity)
        }
    }
}

/// A vertical ScrollView. Offscreen capture can't draw NSScrollView content, so demo mode lays the content out
/// directly and clips it at the frame.
struct VerticalScroll<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        if AppRuntime.isDemo {
            content
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .top)
                .clipped()
        } else {
            ScrollView { content }
        }
    }
}

/// Whether to draw keyboard focus rings: only when the user navigates by keyboard.
@MainActor
enum KeyboardFocus {
    static var showsRing: Bool { !AppRuntime.isDemo && NSApp?.isFullKeyboardAccessEnabled == true }
}

/// The sidebar material behind a Settings-style column; a flat colour in demo mode.
struct SidebarBackground: View {
    var body: some View {
        if AppRuntime.isDemo {
            Palette.sidebar
        } else {
            VisualEffect()
        }
    }

    private struct VisualEffect: NSViewRepresentable {
        func makeNSView(context: Context) -> NSVisualEffectView {
            let view = NSVisualEffectView()
            view.material = .sidebar
            view.blendingMode = .behindWindow
            view.state = .followsWindowActiveState
            return view
        }
        func updateNSView(_ view: NSVisualEffectView, context: Context) {}
    }
}

/// Wraps its children onto new lines, for the language chips.
struct FlowLayout: Layout {
    var spacing: CGFloat = Spacing.xs

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let result = arrange(width: proposal.width ?? .infinity, subviews: subviews)
        return CGSize(width: proposal.width ?? result.width, height: result.height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let result = arrange(width: bounds.width, subviews: subviews)
        for (subview, origin) in zip(subviews, result.origins) {
            subview.place(at: CGPoint(x: bounds.minX + origin.x, y: bounds.minY + origin.y), proposal: .unspecified)
        }
    }

    private func arrange(width: CGFloat, subviews: Subviews) -> (origins: [CGPoint], width: CGFloat, height: CGFloat) {
        var origins: [CGPoint] = []
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0, maxX: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            origins.append(CGPoint(x: x, y: y))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
            maxX = max(maxX, x - spacing)
        }
        return (origins, maxX, y + rowHeight)
    }
}

/// Pill tabs inside one bordered strip.
struct TabStrip<T: Hashable & Identifiable>: View {
    let items: [T]
    let title: (T) -> String
    @Binding var selection: T

    var body: some View {
        HStack(spacing: 2) {
            ForEach(items) { item in
                Button { selection = item } label: {
                    Text(title(item))
                        .font(TypeScale.callout.weight(selection == item ? .semibold : .regular))
                        .foregroundStyle(selection == item ? Palette.foreground : Palette.muted)
                        .padding(.horizontal, Spacing.sm).frame(height: 26)
                        .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(selection == item ? Palette.selected : .clear))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.quiet())
                .accessibilityAddTraits(selection == item ? .isSelected : [])
            }
        }
        .padding(3)
        .background(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous).fill(Palette.surfaceInset))
        .overlay(RoundedRectangle(cornerRadius: Radius.overlay, style: .continuous).strokeBorder(Palette.hairline))
        .fixedSize()
    }
}

// MARK: Inputs

extension View {
    /// The bordered look shared by every text input.
    func fieldChrome(focused: Bool) -> some View {
        padding(.horizontal, Spacing.sm).frame(minHeight: 34)
            .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(Palette.surface))
            .overlay(RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                .strokeBorder(focused ? Palette.focusRing : Palette.hairlineStrong, lineWidth: focused ? 2 : 1))
    }
}

struct SearchField: View {
    let prompt: String
    @Binding var text: String
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: Spacing.xs) {
            Image(systemName: "magnifyingglass").font(.system(size: 13)).foregroundStyle(Palette.subtle).accessibilityHidden(true)
            PlaceholderTextField(prompt: prompt, text: $text)
                .focused($focused)
                .accessibilityLabel(prompt)
            if !text.isEmpty {
                Button { text = "" } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 12)).foregroundStyle(Palette.subtle)
                }
                .buttonStyle(.plain).accessibilityLabel("Clear search")
            }
        }
        .fieldChrome(focused: focused)
    }
}

/// A one-line text input with the standard border.
struct InputField: View {
    let prompt: String
    @Binding var text: String
    var onSubmit: () -> Void = {}
    @FocusState private var focused: Bool

    var body: some View {
        PlaceholderTextField(prompt: prompt, text: $text)
            .focused($focused)
            .onSubmit(onSubmit)
            .accessibilityLabel(prompt)
            .fieldChrome(focused: focused)
    }
}

/// A plain field whose placeholder is muted. SwiftUI would draw it in the field's foreground colour.
struct PlaceholderTextField: View {
    let prompt: String
    @Binding var text: String

    var body: some View {
        ZStack(alignment: .leading) {
            if text.isEmpty {
                Text(prompt).font(TypeScale.callout).foregroundStyle(Palette.subtle).lineLimit(1).allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
            TextField("", text: $text).textFieldStyle(.plain).font(TypeScale.callout).foregroundStyle(Palette.foreground)
        }
    }
}

/// A monochrome switch. The system switch follows the accent colour in some windows and ignores `tint` in others.
struct VFSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View { StyledBody(configuration: configuration) }

    private struct StyledBody: View {
        let configuration: Configuration
        @Environment(\.isEnabled) private var enabled

        var body: some View {
            Button { configuration.isOn.toggle() } label: {
                ZStack(alignment: configuration.isOn ? .trailing : .leading) {
                    RoundedRectangle(cornerRadius: 10, style: .circular)
                        .fill(configuration.isOn ? Palette.primary : Palette.track)
                        .overlay(RoundedRectangle(cornerRadius: 10, style: .circular)
                            .strokeBorder(configuration.isOn ? Color.clear : Palette.hairlineStrong))
                    Circle()
                        .fill(configuration.isOn ? Palette.onPrimary : Palette.muted)
                        .shadow(color: .black.opacity(0.2), radius: 1, y: 0.5)
                        .frame(width: 16, height: 16)
                        .padding(2)
                }
                .frame(width: 36, height: 20)
                .opacity(enabled ? 1 : 0.45)
                .contentShape(Rectangle())
            }
            .buttonStyle(.quiet(radius: 10))
            .animation(.easeOut(duration: 0.12), value: configuration.isOn)
            .accessibilityValue(configuration.isOn ? "On" : "Off")
            .accessibilityAddTraits(.isToggle)
        }
    }
}

struct VFCheckboxStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View { StyledBody(configuration: configuration) }

    private struct StyledBody: View {
        let configuration: Configuration

        var body: some View {
            Button { configuration.isOn.toggle() } label: {
                HStack(spacing: Spacing.xs) {
                    CheckMark(isOn: configuration.isOn)
                    configuration.label.font(TypeScale.callout).foregroundStyle(Palette.foreground)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.quiet())
            .accessibilityValue(configuration.isOn ? "On" : "Off")
        }
    }
}

struct CheckMark: View {
    let isOn: Bool
    var body: some View {
        RoundedRectangle(cornerRadius: 4, style: .continuous)
            .fill(isOn ? Palette.primary : Palette.surface)
            .overlay(RoundedRectangle(cornerRadius: 4, style: .continuous).strokeBorder(isOn ? .clear : Palette.hairlineStrong))
            .overlay {
                if isOn { Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(Palette.onPrimary) }
            }
            .frame(width: 16, height: 16)
            .accessibilityHidden(true)
    }
}

/// A filled radio dot in the foreground colour.
struct RadioMark: View {
    let isOn: Bool
    var body: some View {
        Circle()
            .strokeBorder(isOn ? Palette.primary : Palette.hairlineStrong, lineWidth: isOn ? 5 : 1)
            .frame(width: 16, height: 16)
            .accessibilityHidden(true)
    }
}

/// Selected card marker: an outlined tick.
struct CardCheck: View {
    let selected: Bool
    var body: some View {
        Image(systemName: selected ? "checkmark.circle" : "circle")
            .font(.system(size: 17, weight: .regular))
            .foregroundStyle(selected ? Palette.foreground : Palette.hairlineStrong)
            .accessibilityHidden(true)
    }
}

// MARK: Rows with hover actions

/// Row actions fade in on hover. With Full Keyboard Access on they stay visible, since a keyboard user cannot hover.
struct HoverActions<Content: View>: View {
    let visible: Bool
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: Spacing.xxs) { content }
            .opacity(visible || (!AppRuntime.isDemo && NSApp?.isFullKeyboardAccessEnabled == true) ? 1 : 0)
    }
}

// MARK: Meters

/// Segmented level meter, used by the mic test and the live meeting view.
struct SegmentedMeter: View {
    let level: Float
    var segments = 28
    var height: CGFloat = 10

    var body: some View {
        let lit = Int((min(max(level, 0), 1) * Float(segments)).rounded())
        HStack(spacing: 3) {
            ForEach(0..<segments, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                    .fill(i < lit ? Palette.meterFill : Palette.track)
                    .frame(height: height)
            }
        }
        .accessibilityElement()
        .accessibilityLabel("Level")
        .accessibilityValue("\(Int((level * 100).rounded())) percent")
    }
}

/// Mirrored wave bars for the mic test in onboarding, driven by one level value.
struct LevelMeter: View {
    let level: Float
    var bars = 24

    var body: some View {
        HStack(alignment: .center, spacing: 3) {
            ForEach(0..<bars, id: \.self) { i in
                let mid = Float(bars) / 2
                let falloff = 1 - abs(Float(i) - mid) / (mid + 2)
                Capsule()
                    .fill(Palette.meterFill)
                    .frame(width: 4, height: max(4, CGFloat(level * falloff) * 44))
            }
        }
        .frame(height: 48)
        .animation(.linear(duration: 0.08), value: level)
        .accessibilityElement()
        .accessibilityLabel("Microphone level")
        .accessibilityValue("\(Int((level * 100).rounded())) percent")
    }
}

// MARK: Notices

/// A quiet one-line notice with an optional link and dismiss button. Used for the update notice.
struct InlineNotice<Link: View>: View {
    let symbol: String
    let text: String
    var onDismiss: (() -> Void)? = nil
    @ViewBuilder var link: Link

    var body: some View {
        HStack(spacing: Spacing.sm) {
            Image(systemName: symbol).font(.system(size: 13)).foregroundStyle(Palette.muted).accessibilityHidden(true)
            Text(text).font(TypeScale.callout).foregroundStyle(Palette.foreground).lineLimit(2)
            link
            Spacer(minLength: 0)
            if let onDismiss { IconButton(symbol: "xmark", help: "Dismiss", size: 22, action: onDismiss) }
        }
        .padding(.horizontal, Spacing.md).padding(.vertical, 10)
        .cardSurface(radius: Radius.overlay)
    }
}
