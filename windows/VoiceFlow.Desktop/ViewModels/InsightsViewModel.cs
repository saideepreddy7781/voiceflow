using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed record CategoryUsage(string Name, int Words, double Share, string Percent);

public sealed partial class InsightsViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly DateTimeOffset _now;

    public InsightsViewModel(IAppServices services, DateTimeOffset now)
    {
        _services = services;
        _now = now;
        Today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        services.History.Changed += (_, _) => Refresh();
        Refresh();
    }

    public DateOnly Today { get; }

    public ObservableCollection<CategoryUsage> Categories { get; } = [];

    [ObservableProperty] string _wordsPerMinute = "–";
    [ObservableProperty] string _fixes = "0";
    [ObservableProperty] string _fixesDetail = "";
    [ObservableProperty] string _totalWords = "0";
    [ObservableProperty] string _dictations = "0 dictations";
    [ObservableProperty] string _streakText = "";
    [ObservableProperty] string _longestText = "";
    [ObservableProperty] bool _hasCategories;
    [ObservableProperty] IReadOnlyDictionary<DateOnly, int>? _dailyWords;
    [ObservableProperty] int _weekOffset;

    public bool CanGoNewer => WeekOffset > 0;

    partial void OnWeekOffsetChanged(int value) => OnPropertyChanged(nameof(CanGoNewer));

    void Refresh()
    {
        var s = UsageStats.Compute(_services.History.Entries, _now);
        var c = CultureInfo.CurrentCulture;
        WordsPerMinute = s.WordsPerMinute > 0 ? s.WordsPerMinute.ToString("N0", c) : "–";
        Fixes = (s.WordsCorrected + s.DictionaryFixes).ToString("N0", c);
        var details = new List<string>();
        if (s.WordsCorrected > 0) details.Add($"{s.WordsCorrected:N0} words corrected");
        if (s.DictionaryFixes > 0) details.Add($"{s.DictionaryFixes:N0} dictionary fixes");
        FixesDetail = string.Join("  ·  ", details);
        TotalWords = s.TotalWords.ToString("N0", c);
        Dictations = $"{s.Dictations:N0} dictations";
        StreakText = s.DayStreak > 1 ? $"Current streak: {s.DayStreak} days" : "";
        LongestText = s.LongestStreak > 0 ? $"Longest: {s.LongestStreak} days" : "";
        DailyWords = s.DailyWords;

        Categories.Clear();
        var total = Math.Max(1, s.ByCategory.Sum(p => p.Words));
        foreach (var (category, words) in s.ByCategory)
        {
            var share = (double)words / total;
            Categories.Add(new CategoryUsage(category.DisplayName(), words, share, $"{Math.Round(share * 100)}%"));
        }
        HasCategories = Categories.Count > 0;
    }

    [RelayCommand]
    void Older() => WeekOffset += 4;

    [RelayCommand]
    void Newer() => WeekOffset = Math.Max(0, WeekOffset - 4);
}
