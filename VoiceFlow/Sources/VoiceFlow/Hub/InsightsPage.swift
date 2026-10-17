import SwiftUI
import VoiceFlowCore

struct InsightsPage: View {
    let model: AppModel
    @State private var weekOffset = 0

    var body: some View {
        let s = model.stats
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "Insights", subtitle: "Usage")

                HStack(alignment: .top, spacing: 14) {
                    Card {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Words per minute").font(.callout).foregroundStyle(.secondary)
                            Text(s.wordsPerMinute > 0 ? "\(s.wordsPerMinute)" : "–").font(.system(size: 34, weight: .bold, design: .rounded))
                            Text("Typing averages about 40 wpm").font(.caption).foregroundStyle(.secondary)
                                .help("Compared with typical keyboard typing speed")
                        }
                    }
                    Card {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Fixes made by VoiceFlow").font(.callout).foregroundStyle(.secondary)
                            Text("\(s.wordsCorrected + s.dictionaryFixes)").font(.system(size: 34, weight: .bold, design: .rounded))
                            if s.wordsCorrected > 0 { Text("\(s.wordsCorrected) words corrected").font(.caption).foregroundStyle(.secondary) }
                            if s.dictionaryFixes > 0 { Text("\(s.dictionaryFixes) dictionary fixes").font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                    Card {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Total words dictated").font(.callout).foregroundStyle(.secondary)
                            Text("\(s.totalWords)").font(.system(size: 34, weight: .bold, design: .rounded))
                            Text("\(s.dictations) dictations").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }

                Card {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Desktop usage").font(.headline)
                        if s.byCategory.isEmpty {
                            Text("Dictate in a few apps to see where you use VoiceFlow.").foregroundStyle(.secondary)
                        }
                        let total = max(1, s.byCategory.reduce(0) { $0 + $1.1 })
                        ForEach(s.byCategory, id: \.0) { category, words in
                            HStack(spacing: 10) {
                                Text(category.displayName).frame(width: 150, alignment: .leading)
                                GeometryReader { geo in
                                    Capsule().fill(Color.accentColor.opacity(0.75))
                                        .frame(width: max(6, geo.size.width * CGFloat(words) / CGFloat(total)))
                                }
                                .frame(height: 10)
                                Text("\(Int((Double(words) / Double(total) * 100).rounded()))%")
                                    .font(.callout.monospacedDigit()).foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
                            }
                            .help("\(words) words · \(Self.level(words))")
                        }
                    }
                }

                Card {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Text("Activity").font(.headline)
                            Spacer()
                            if s.dayStreak > 1 { Text("Current streak: \(s.dayStreak) days").font(.callout).foregroundStyle(.secondary) }
                            if s.longestStreak > 0 { Text("Longest: \(s.longestStreak) days").font(.callout).foregroundStyle(.secondary) }
                            Button { weekOffset += 4 } label: { Image(systemName: "chevron.left") }.buttonStyle(.borderless)
                            Button { weekOffset = max(0, weekOffset - 4) } label: { Image(systemName: "chevron.right") }
                                .buttonStyle(.borderless).disabled(weekOffset == 0)
                        }
                        Heatmap(daily: s.dailyWords, weeks: 20, weekOffset: weekOffset)
                        HStack(spacing: 4) {
                            Text("Less").font(.caption).foregroundStyle(.secondary)
                            ForEach(0..<5) { band in RoundedRectangle(cornerRadius: 2).fill(Heatmap.color(band: band)).frame(width: 11, height: 11) }
                            Text("More").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding(28)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }

    static func level(_ words: Int) -> String {
        words >= 10_000 ? "Power" : words >= 1_000 ? "Moderate" : "Light"
    }
}

/// Sunday-to-Saturday grid of daily words, five bands: 0, 1–249, 250–499, 500–749, 750+.
private struct Heatmap: View {
    let daily: [Date: Int]
    let weeks: Int
    let weekOffset: Int

    static func band(_ words: Int) -> Int {
        switch words {
        case 0: 0
        case 1..<250: 1
        case 250..<500: 2
        case 500..<750: 3
        default: 4
        }
    }

    static func color(band: Int) -> Color {
        band == 0 ? Color.primary.opacity(0.07) : Color.accentColor.opacity(0.25 + 0.18 * Double(band))
    }

    var body: some View {
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        let thisSunday = calendar.date(byAdding: .day, value: -(calendar.component(.weekday, from: today) - 1), to: today)!
        let lastSunday = calendar.date(byAdding: .weekOfYear, value: -weekOffset, to: thisSunday)!
        HStack(alignment: .top, spacing: 3) {
            ForEach(0..<weeks, id: \.self) { w in
                let weekStart = calendar.date(byAdding: .weekOfYear, value: w - (weeks - 1), to: lastSunday)!
                VStack(spacing: 3) {
                    ForEach(0..<7, id: \.self) { d in
                        let day = calendar.date(byAdding: .day, value: d, to: weekStart)!
                        let words = daily[day] ?? 0
                        RoundedRectangle(cornerRadius: 2)
                            .fill(day > today ? Color.clear : Self.color(band: Self.band(words)))
                            .frame(width: 13, height: 13)
                            .help(day > today ? "" : "\(day.formatted(date: .abbreviated, time: .omitted)): \(words) words")
                    }
                }
            }
        }
    }
}
