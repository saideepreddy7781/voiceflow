using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Desktop.Controls;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.Services.Demo;
using VoiceFlow.Desktop.ViewModels;
using Xunit;
using Page = VoiceFlow.Desktop.ViewModels.Page;

namespace VoiceFlow.Desktop.Tests;

public class ViewModelTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 10, 18, 42, 0, TimeSpan.FromHours(5.5));

    static DemoServices Services(bool empty = false) => new(Now, animated: false, empty: empty);

    [Fact]
    public void HomeGroupsHistoryByDayNewestFirst()
    {
        var vm = new HomeViewModel(Services(), Now, () => { });
        Assert.Equal("Today", vm.Groups[0].Label);
        Assert.Equal("Yesterday", vm.Groups[1].Label);
    }

    [Fact]
    public void LeavingSettingsStopsTheMicrophoneTest()
    {
        var services = Services();
        var shell = new ShellViewModel(services, Now);
        shell.Navigate(Page.Settings);
        services.Microphone.StartTest("");
        shell.Navigate(Page.Home);
        Assert.False(services.Microphone.IsTesting);
    }

    [Fact]
    public void HomeSearchFiltersAndShowsNoMatches()
    {
        var vm = new HomeViewModel(Services(), Now, () => { });
        vm.SearchText = "no such phrase anywhere";
        Assert.Empty(vm.Groups);
        Assert.True(vm.NoMatches);
        vm.SearchText = "";
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public void HomeShowsSetupCardOnlyWhenSomethingIsMissing()
    {
        var services = Services();
        var vm = new HomeViewModel(services, Now, () => { });
        Assert.False(vm.ShowSetup);
        services.DemoSetup.Set(new SetupProblem("Groq key missing", ""));
        Assert.True(vm.ShowSetup);
    }

    [Fact]
    public void HomeEmptyStateHasZeroStats()
    {
        var vm = new HomeViewModel(Services(empty: true), Now, () => { });
        Assert.True(vm.NoHistory);
        Assert.Equal("0", vm.WordsToday);
        Assert.Equal("0 min", vm.TimeSavedToday);
    }

    [Fact]
    public void GreetingFollowsTimeOfDay()
    {
        var morning = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal("Good morning", new HomeViewModel(Services(), morning.ToLocalTime().Date.AddHours(8), () => { }).Greeting);
    }

    [Fact]
    public void ShellNavigatesAndKeepsOnlyOneItemSelected()
    {
        var shell = new ShellViewModel(Services(), Now);
        shell.Navigate(Page.Insights);
        Assert.IsType<InsightsViewModel>(shell.Current);
        Assert.Single(shell.AllItems, i => i.IsSelected);
        shell.BottomItems[0].IsSelected = true;
        Assert.IsType<SettingsViewModel>(shell.Current);
        Assert.Single(shell.AllItems, i => i.IsSelected);
    }

    [Fact]
    public void InsightsPercentagesCoverEveryCategory()
    {
        var vm = new InsightsViewModel(Services(), Now);
        Assert.True(vm.HasCategories);
        var total = vm.Categories.Sum(c => c.Share);
        Assert.InRange(total, 0.99, 1.01);
        vm.OlderCommand.Execute(null);
        Assert.True(vm.CanGoNewer);
        vm.NewerCommand.Execute(null);
        Assert.False(vm.CanGoNewer);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(249, 1)]
    [InlineData(250, 2)]
    [InlineData(500, 3)]
    [InlineData(750, 4)]
    [InlineData(9000, 4)]
    public void HeatmapBandsMatchTheMacApp(int words, int band) => Assert.Equal(band, ActivityHeatmap.Band(words));

    [Fact]
    public void MeetingDetailRenamesSpeakersAndKeepsThemInMarkdown()
    {
        var services = Services();
        var meeting = services.Meetings.Meetings[0];
        var vm = new MeetingDetailViewModel(meeting, services, _ => { });
        vm.OthersName = "Rahul";
        Assert.Equal("Rahul", vm.Lines.First(l => !l.IsYou).Speaker);
        Assert.Contains("Rahul", MeetingMarkdown.Render(meeting));
    }

    [Fact]
    public void TogglingAnActionItemReachesTheMeeting()
    {
        var services = Services();
        var meeting = services.Meetings.Meetings[0];
        var vm = new MeetingDetailViewModel(meeting, services, _ => { });
        var first = meeting.Notes!.ActionItems[0];
        var before = first.Done;
        vm.ActionItems[0].Done = !before;
        Assert.Equal(!before, first.Done);
    }

    [Fact]
    public void RenamingKeepsTheOpenMeetingPane()
    {
        var services = Services();
        var vm = new MeetingsViewModel(services, _ => { });
        var pane = vm.Pane;
        services.Meetings.Rename(services.Meetings.Meetings[0].Id, "Renamed");
        Assert.Same(pane, vm.Pane);
        Assert.Equal("Renamed", vm.Items[0].Title);
    }

    [Fact]
    public void DeletingAMeetingAsksFirstAndRemovesOnConfirm()
    {
        var services = Services();
        object? dialog = null;
        var meeting = services.Meetings.Meetings[0];
        var count = services.Meetings.Meetings.Count;
        var detail = new MeetingDetailViewModel(meeting, services, d => dialog = d);
        detail.AskDeleteCommand.Execute(null);
        Assert.Equal(count, services.Meetings.Meetings.Count);
        ((ConfirmDialogViewModel)dialog!).ConfirmCommand.Execute(null);
        Assert.Equal(count - 1, services.Meetings.Meetings.Count);
    }

    [Fact]
    public void DictionaryFiltersByTabAndSearch()
    {
        var vm = new DictionaryViewModel(Services(), _ => { });
        var all = vm.Rows.Count;
        vm.ShowAll = false;
        vm.ShowLearned = true;
        Assert.True(vm.Rows.Count < all);
        Assert.All(vm.Rows, r => Assert.True(r.Learned));
        vm.SearchText = "Whisper";
        Assert.Single(vm.Rows);
    }

    [Fact]
    public void DictionaryEditRejectsEmptyWords()
    {
        var services = Services();
        var edit = new DictionaryEditViewModel(null, services, () => { });
        edit.SaveCommand.Execute(null);
        Assert.True(edit.HasError);
    }

    [Fact]
    public void SnippetEditRejectsDuplicates()
    {
        var services = Services();
        var existing = services.Snippets.Snippets[0].Trigger;
        var edit = new SnippetEditViewModel(null, services, () => { }) { Trigger = existing, Expansion = "x" };
        edit.SaveCommand.Execute(null);
        Assert.True(edit.HasError);
    }

    [Fact]
    public void ScratchpadSavesEditsToTheNote()
    {
        var services = Services();
        var vm = new ScratchpadViewModel(services, _ => { });
        vm.Body = "changed";
        Assert.Equal("changed", vm.SelectedRow!.Note.Body);
    }

    [Fact]
    public void ScratchpadNewNoteIsSelected()
    {
        var vm = new ScratchpadViewModel(Services(), _ => { });
        vm.NewNoteCommand.Execute(null);
        Assert.Equal("", vm.Title);
        Assert.True(vm.HasNote);
    }

    [Fact]
    public void GeneralLanguagesSaveAndControlTeluguChoice()
    {
        var services = Services();
        var vm = new GeneralSettings(services);
        Assert.True(vm.SpeaksTelugu);
        vm.AllLanguages.First(l => l.Code == "te").IsSelected = false;
        Assert.False(services.Preferences.Current.SpeaksTelugu);
        Assert.False(vm.SpeaksTelugu);
    }

    [Fact]
    public void GeneralMicTestUpdatesTheLevel()
    {
        var vm = new GeneralSettings(Services());
        vm.ToggleTestCommand.Execute(null);
        Assert.True(vm.IsTesting);
        Assert.True(vm.TestLevel > 0);
        vm.ToggleTestCommand.Execute(null);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public void KeysTestReportsSuccess()
    {
        var services = Services();
        var field = new KeysSettings(services).Groq;
        field.TestCommand.Execute(null);
        // The demo answers after a short delay; polling keeps this free of timing assumptions.
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!field.HasResult && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(field.ResultOk);
    }

    [Fact]
    public void OnboardingWaitsForAKeyOnTheKeyStep()
    {
        var services = Services();
        services.Keys.Remove(SecretKey.Groq);
        var vm = new OnboardingViewModel(services, () => { }) { Step = 1 };
        Assert.False(vm.CanContinue);
        services.Keys.Save(SecretKey.Groq, "gsk_demo");
        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void OnboardingFinishRecordsCompletion()
    {
        var services = Services();
        var done = false;
        var vm = new OnboardingViewModel(services, () => done = true) { Step = 5 };
        vm.FinishCommand.Execute(null);
        Assert.True(done);
        Assert.True(services.Preferences.Current.Onboarded);
    }

    [Fact]
    public void FlowBarShowsTheRightPartsForEachPhase()
    {
        var services = Services();
        var dictation = (DemoDictation)services.Dictation;
        var bar = new FlowBarViewModel(services, runClock: false);
        Assert.False(bar.ShowPill);
        dictation.Set(FlowPhase.Listening);
        Assert.True(bar.ShowPill);
        Assert.False(bar.ShowHandsFree);
        dictation.Set(FlowPhase.HandsFree);
        Assert.True(bar.ShowHandsFree);
        dictation.Set(FlowPhase.Processing);
        Assert.True(bar.Frozen);
        dictation.Set(FlowPhase.Idle, meetingSince: DateTimeOffset.Now);
        Assert.False(bar.ShowPill);
        Assert.True(bar.ShowMeetingChip);
    }

    [Fact]
    public void FlowBarToastActionRuns()
    {
        var services = Services();
        var dictation = (DemoDictation)services.Dictation;
        var bar = new FlowBarViewModel(services, runClock: false);
        var ran = false;
        dictation.Set(FlowPhase.Listening, toast: new FlowToast("Saved", ActionTitle: "Undo", Action: () => ran = true));
        Assert.True(bar.ShowToast);
        Assert.True(bar.HasToastAction);
        bar.RunToastActionCommand.Execute(null);
        Assert.True(ran);
    }

    [Fact]
    public void DebugReportCarriesNoDictatedText()
    {
        var services = Services();
        var report = services.Diagnostics.BuildReport();
        foreach (var entry in services.History.Entries.Take(5))
            Assert.DoesNotContain(entry.Final, report);
    }

    // Integration additions

    [Fact]
    public void FlowBarSitsAtBottomCentreOfTheWorkArea()
    {
        var work = new Avalonia.PixelRect(1920, 0, 2560, 1400);
        var position = Views.FlowBarWindow.BottomCentre(work, new Avalonia.PixelSize(200, 80), 1.5);
        Assert.Equal(1920 + (2560 - 200) / 2, position.X);
        Assert.Equal(1400 - 80 - 9, position.Y);   // margin 6 at 150%
    }

    [Fact]
    public void RestingBarIsHiddenUnlessTheSettingIsOn()
    {
        var d = new DemoDictation();
        var view = new FlowBarViewModel(new ServicesWithDictation(Services(), d), runClock: false);
        Assert.False(view.ShowIdlePill);
        d.ShowWhenIdle = true;
        d.Set(FlowPhase.Idle);
        Assert.True(view.ShowIdlePill);
        d.Set(FlowPhase.Listening);
        Assert.False(view.ShowIdlePill);
    }

    [Fact]
    public void HomeShowsAnUpdateNotice()
    {
        var services = Services();
        var vm = new HomeViewModel(services, Now, () => { });
        Assert.False(vm.HasUpdate);
        services.DemoUpdates.SetAvailable("9.0.0");
        Assert.True(vm.HasUpdate);
        Assert.Contains("9.0.0", vm.UpdateNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutOffersTheReleaseWhenOneIsAvailable()
    {
        var services = Services();
        var about = new SettingsViewModel(services, () => { }).About;
        Assert.False(about.UpdateAvailable);
        services.DemoUpdates.SetAvailable("9.0.0");
        Assert.True(about.UpdateAvailable);
        Assert.Contains("9.0.0", about.UpdateText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpdateSettingCanBeTurnedOff()
    {
        var services = Services();
        var about = new SettingsViewModel(services, () => { }).About;
        Assert.True(about.CheckForUpdates);
        about.CheckForUpdates = false;
        Assert.False(services.Preferences.Current.CheckForUpdates);
    }

    [Fact]
    public void GreetingFollowsTheClockAfterReload()
    {
        var now = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.FromHours(5.5));
        var vm = new HomeViewModel(Services(), () => now, () => { });
        var morning = vm.Greeting;
        now = now.AddHours(11);
        vm.Reload();
        Assert.NotEqual(morning, vm.Greeting);
    }

    [Fact]
    public void MeetingPaneShowsNotesOnceTheyAreWritten()
    {
        var services = Services();
        var vm = new MeetingsViewModel(services, _ => { });
        var pane = Assert.IsType<MeetingDetailViewModel>(vm.Pane);
        pane.ShowTranscript = true;
        var meeting = pane.Meeting;

        meeting.Notes = new MeetingNotes { Summary = ["Freshly written."] };
        services.Meetings.Rename(meeting.Id, meeting.Title);   // raises Changed, as saving does in the real app

        var updated = Assert.IsType<MeetingDetailViewModel>(vm.Pane);
        Assert.NotSame(pane, updated);
        Assert.Equal(["Freshly written."], updated.Summary);
        Assert.True(updated.ShowTranscript);
    }

    [Fact]
    public async Task MeetingPaneStaysPutWhenNothingChanged()
    {
        var services = Services();
        var vm = new MeetingsViewModel(services, _ => { });
        var pane = vm.Pane;
        await services.Meetings.RewriteNotesAsync(Guid.NewGuid());
        services.Meetings.Rename(((MeetingDetailViewModel)pane!).Meeting.Id, "Same meeting, new title");
        Assert.Same(pane, vm.Pane);
    }

    [Fact]
    public async Task LiveTranscriptGrowsAsChunksArrive()
    {
        var services = Services();
        await services.Meetings.StartAsync();
        using var live = new LiveMeetingViewModel(services);
        var before = live.Lines.Count;

        services.Meetings.Live!.Lines.Add(new TranscriptLine(90, Speaker.You, "One more thing."));
        await services.Meetings.StartAsync();   // raises Changed, as each saved chunk does in the real app

        Assert.Equal(before + 1, live.Lines.Count);
    }

    [Fact]
    public void ClearingHistoryAsksFirst()
    {
        var services = Services();
        object? dialog = null;
        var privacy = new SettingsViewModel(services, () => { }, d => dialog = d).Privacy;
        var count = services.History.Entries.Count;
        privacy.AskClearHistoryCommand.Execute(null);
        Assert.Equal(count, services.History.Entries.Count);
        ((ConfirmDialogViewModel)dialog!).ConfirmCommand.Execute(null);
        Assert.Empty(services.History.Entries);
        Assert.Null(dialog);
    }

    [Fact]
    public void ClickingTheMeetingChipOpensMeetings()
    {
        var d = new DemoDictation();
        new FlowBarViewModel(new ServicesWithDictation(Services(), d), runClock: false).OpenMeetingsCommand.Execute(null);
        Assert.Equal(1, d.OpenMeetingsCount);
    }

    [Fact]
    public void SearchFindsNotesAndShowsWhere()
    {
        var vm = new MeetingsViewModel(Services(), _ => { }) { SearchText = "resume crash" };
        var hit = Assert.Single(vm.Items, i => i.Title == "Weekly product sync");
        Assert.True(hit.HasSnippet);
        Assert.Contains("resume crash", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        vm.SearchText = "no such words anywhere";
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void ClearingTheOthersNameGoesBackToOthers()
    {
        var services = Services();
        var meeting = services.Meetings.Meetings[0];
        var vm = new MeetingDetailViewModel(meeting, services, _ => { }) { OthersName = "Rahul" };
        vm.OthersName = "";
        Assert.Equal("Others", meeting.LabelFor(Speaker.Others));
    }

    [Fact]
    public async Task LiveViewShowsTheLanguageOfEachSide()
    {
        var services = Services();
        await services.Meetings.StartAsync();
        using var live = new LiveMeetingViewModel(services);
        Assert.Equal("English", live.MicLanguage);
        Assert.Equal("Telugu", live.CallLanguage);
        Assert.True(live.HasCallLanguage);
    }

    [Fact]
    public void HindiChoiceAppearsWithHindiAndSaves()
    {
        var services = Services();
        var vm = new GeneralSettings(services);
        Assert.True(vm.SpeaksHindi);
        vm.HindiOptions.First(o => o.Script == HindiScript.Hinglish).IsSelected = true;
        Assert.Equal(HindiScript.Hinglish, services.Preferences.Current.HindiScript);
        vm.AllLanguages.First(l => l.Code == "hi").IsSelected = false;
        Assert.False(vm.SpeaksHindi);
    }

    sealed class ServicesWithDictation(DemoServices inner, IDictationService dictation) : IAppServices
    {
        public ISetupService Setup => inner.Setup;
        public IHistoryService History => inner.History;
        public IMeetingsService Meetings => inner.Meetings;
        public IDictionaryService Dictionary => inner.Dictionary;
        public ISnippetsService Snippets => inner.Snippets;
        public IScratchpadService Scratchpad => inner.Scratchpad;
        public IPreferencesService Preferences => inner.Preferences;
        public IMicrophoneService Microphone => inner.Microphone;
        public IKeyService Keys => inner.Keys;
        public IDictationService Dictation => dictation;
        public IShellService Shell => inner.Shell;
        public IUpdateService Updates => inner.Updates;
        public IDiagnosticsService Diagnostics => inner.Diagnostics;
    }
}
