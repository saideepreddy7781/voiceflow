import SwiftUI
import VoiceFlowCore

struct InsightsPage: View {
    let model: AppModel
    @State private var weekOffset = 0

    var body: some View {
        let s = model.stats
        Page(width: Metrics.wideContentWidth) {
            PageHeader(title: "Insights", subtitle: "Usage")

            HStack(alignment: .top, spacing: Spacing.md) {
                StatCard(label: "Words per minute", value: s.wordsPerMinute > 0 ? "\(s.wordsPerMinute)" : "–",
                         detail: "Typing averages about 40 wpm", large: true)
                    .help("Compared with typical keyboard typing speed")
                StatCard(label: "Fixes made by VoiceFlow", value: (s.wordsCorrected + s.dictionaryFixes).formatted(),
                         detail: fixesDetail(s), large: true)
                StatCard(label: "Total words dictated", value: s.totalWords.formatted(),
                         detail: "\(s.dictations.formatted()) dictations", large: true)
            }
            .fixedSize(horizontal: false, vertical: true)

            Card {
                VStack(alignment: .leading, spacing: Spacing.sm) {
                    SectionHeading(text: "Desktop usage")
                    if s.byCategory.isEmpty {
                        Text("Dictate in a few apps to see where you use VoiceFlow.")
                            .font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                    let total = max(1, s.byCategory.reduce(0) { $0 + $1.1 })
                    ForEach(s.byCategory, id: \.0) { category, words in
                        let percent = Int((Double(words) / Double(total) * 100).rounded())
                        HStack(spacing: Spacing.md) {
                            Text(category.displayName).font(TypeScale.callout).foregroundStyle(Palette.foreground)
                                .lineLimit(1).frame(width: 150, alignment: .leading)
                            GeometryReader { geo in
                                ZStack(alignment: .leading) {
                                    Capsule().fill(Palette.track)
                                    Capsule().fill(Palette.primary)
                                        .frame(width: max(6, geo.size.width * CGFloat(words) / CGFloat(total)))
                                }
                            }
                            .frame(height: 8)
                            Text("\(percent)%").font(TypeScale.callout).monospacedDigit().foregroundStyle(Palette.muted)
                                .frame(width: 40, alignment: .trailing)
                        }
                        .help("\(words) words · \(Self.level(words))")
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel(category.displayName)
                        .accessibilityValue("\(percent) percent, \(words) words")
                    }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: Spacing.sm) {
                    HStack(spacing: Spacing.md) {
                        SectionHeading(text: "Activity")
                        Spacer()
                        if s.dayStreak > 1 { streak("Current streak: \(s.dayStreak) days") }
                        if s.longestStreak > 0 { streak("Longest: \(s.longestStreak) days") }
                        HStack(spacing: Spacing.xxs) {
                            IconButton(symbol: "chevron.left", help: "Earlier") { weekOffset += 4 }
                            IconButton(symbol: "chevron.right", help: "Later") { weekOffset = max(0, weekOffset - 4) }
                                .disabled(weekOffset == 0)
                        }
                    }
                    Heatmap(daily: s.dailyWords, weekOffset: weekOffset, today: Calendar.current.startOfDay(for: model.now))
                    HStack(spacing: Spacing.xxs) {
                        Text("Less").font(TypeScale.caption).foregroundStyle(Palette.muted)
                        ForEach(0..<5) { band in
                            RoundedRectangle(cornerRadius: 2).fill(Palette.heat[band]).frame(width: 11, height: 11)
                        }
                        Text("More").font(TypeScale.caption).foregroundStyle(Palette.muted)
                    }
                    .accessibilityHidden(true)
                }
            }
        }
    }

    private func fixesDetail(_ s: UsageStats) -> String? {
        var parts: [String] = []
        if s.wordsCorrected > 0 { parts.append("\(s.wordsCorrected.formatted()) words corrected") }
        if s.dictionaryFixes > 0 { parts.append("\(s.dictionaryFixes.formatted()) dictionary fixes") }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    private func streak(_ text: String) -> some View {
        Text(text).font(TypeScale.callout).foregroundStyle(Palette.muted)
    }

    static func level(_ words: Int) -> String {
        words >= 10_000 ? "Power" : words >= 1_000 ? "Moderate" : "Light"
    }
}

/// Sunday-to-Saturday grid of daily words in five bands: 0, 1–249, 250–499, 500–749, 750+.
/// Shows as many weeks as fit the card.
private struct Heatmap: View {
    let daily: [Date: Int]
    let weekOffset: Int
    let today: Date

    private let cell: CGFloat = 13
    private let gap: CGFloat = 3
    private let labelWidth: CGFloat = 34

    static func band(_ words: Int) -> Int {
        switch words {
        case 0: 0
        case 1..<250: 1
        case 250..<500: 2
        case 500..<750: 3
        default: 4
        }
    }

    var body: some View {
        GeometryReader { geo in
            let weeks = max(4, Int((geo.size.width - labelWidth + gap) / (cell + gap)))
            grid(weeks: weeks)
        }
        .frame(height: 7 * cell + 6 * gap + 20)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Activity heatmap")
        .accessibilityValue("\(daily.filter { $0.value > 0 }.count) active days")
    }

    private func grid(weeks: Int) -> some View {
        let calendar = Calendar.current
        let thisSunday = calendar.date(byAdding: .day, value: -(calendar.component(.weekday, from: today) - 1), to: today)!
        let lastSunday = calendar.date(byAdding: .weekOfYear, value: -weekOffset, to: thisSunday)!
        let starts = (0..<weeks).map { calendar.date(byAdding: .weekOfYear, value: $0 - (weeks - 1), to: lastSunday)! }

        return VStack(alignment: .leading, spacing: 4) {
            monthLabels(starts: starts).padding(.leading, labelWidth)
            HStack(alignment: .top, spacing: 0) {
                VStack(alignment: .leading, spacing: gap) {
                    ForEach(0..<7, id: \.self) { d in
                        Text(d == 1 ? "Mon" : d == 3 ? "Wed" : d == 5 ? "Fri" : " ")
                            .font(TypeScale.micro).foregroundStyle(Palette.muted)
                            .frame(height: cell, alignment: .leading)
                    }
                }
                .frame(width: labelWidth, alignment: .leading)
                HStack(alignment: .top, spacing: gap) {
                    ForEach(starts, id: \.self) { start in
                        VStack(spacing: gap) {
                            ForEach(0..<7, id: \.self) { d in
                                dayCell(calendar.date(byAdding: .day, value: d, to: start)!)
                            }
                        }
                    }
                }
            }
        }
    }

    private func dayCell(_ day: Date) -> some View {
        let words = daily[day] ?? 0
        return RoundedRectangle(cornerRadius: 2)
            .fill(day > today ? Color.clear : Palette.heat[Self.band(words)])
            .frame(width: cell, height: cell)
            .help(day > today ? "" : "\(day.formatted(date: .abbreviated, time: .omitted)): \(words) words")
    }

    /// One label over the first week of each month; skipped when it would run into the previous one.
    private func monthLabels(starts: [Date]) -> some View {
        let calendar = Calendar.current
        var labels: [(index: Int, name: String)] = []
        var lastMonth = -1
        for (i, start) in starts.enumerated() {
            let month = calendar.component(.month, from: start)
            guard month != lastMonth else { continue }
            lastMonth = month
            if let previous = labels.last, i - previous.index < 3 { labels.removeLast() }
            labels.append((i, start.formatted(.dateTime.month(.abbreviated))))
        }
        return ZStack(alignment: .leading) {
            Color.clear.frame(height: 14)
            ForEach(labels, id: \.index) { label in
                Text(label.name).font(TypeScale.micro).foregroundStyle(Palette.muted)
                    .offset(x: CGFloat(label.index) * (cell + gap))
            }
        }
    }
}
