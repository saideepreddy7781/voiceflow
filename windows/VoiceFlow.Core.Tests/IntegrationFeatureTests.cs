using System.Net;
using System.Text;
using VoiceFlow.Core;
using VoiceFlow.Core.Input;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Output;
using VoiceFlow.Core.Updates;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "v0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.1", "0.1.0", false)]
    [InlineData("0.1.0+abc123", "0.1.0", false)]
    [InlineData("0.2.0-beta.1", "0.1.0", true)]
    [InlineData("0.2.0", "0.2.0-beta.1", true)]
    [InlineData("0.2.0-beta.1", "0.2.0", false)]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.1", true)]
    [InlineData("0.2.0-beta.10", "0.2.0-beta.9", true)]
    [InlineData("0.2.0-rc.1", "0.2.0-beta.9", true)]
    [InlineData("nonsense", "0.1.0", false)]
    [InlineData("0.2.0", "nonsense", false)]
    [InlineData("", "0.1.0", false)]
    [InlineData(null, "0.1.0", false)]
    public void Compares(string? candidate, string? current, bool expected) => Assert.Equal(expected, AppVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData(" V0.1.0 ", "0.1.0")]
    public void DisplayDropsPrefixAndBuild(string input, string expected) => Assert.Equal(expected, AppVersion.Display(input));
}

public class UpdateCheckerTests
{
    sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    const string Release = """{"tag_name":"v9.9.9","html_url":"https://github.com/saideepreddy7781/voiceflow/releases/tag/v9.9.9","draft":false,"prerelease":false}""";

    static Task<UpdateCheckResult> Check(Stub stub, string current = "0.1.0") => new UpdateChecker(new HttpClient(stub)).CheckAsync(current);

    [Fact]
    public async Task ANewerTagIsOffered()
    {
        var stub = new Stub(HttpStatusCode.OK, Release);
        var result = await Check(stub);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("9.9.9", result.Release!.Version);
        Assert.EndsWith("/releases/tag/v9.9.9", result.Release.Url, StringComparison.Ordinal);
        Assert.Equal(UpdateChecker.LatestReleaseApi, stub.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SameVersionIsUpToDate() => Assert.Equal(UpdateCheckStatus.UpToDate, (await Check(new Stub(HttpStatusCode.OK, Release), "9.9.9")).Status);

    [Fact]
    public async Task NoPublishedReleaseIsNotAnError() => Assert.Equal(UpdateCheckStatus.UpToDate, (await Check(new Stub(HttpStatusCode.NotFound, "{}"))).Status);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"tag_name":"latest"}""")]
    [InlineData(HttpStatusCode.OK, """{"tag_name":"v2.0.0","draft":true}""")]
    [InlineData(HttpStatusCode.OK, """{"tag_name":"v2.0.0","prerelease":true}""")]
    public async Task BadAnswersFail(HttpStatusCode status, string body) => Assert.Equal(UpdateCheckStatus.Failed, (await Check(new Stub(status, body))).Status);

    [Theory]
    [InlineData("https://github.com/saideepreddy7781/voiceflow/releases/tag/v1.2.3", true)]
    [InlineData("https://github.com/saideepreddy7781/voiceflow/releases", true)]
    [InlineData("https://github.com/saideepreddy7781/voiceflow/issues/1", false)]
    [InlineData("https://github.com/saideepreddy7781/voiceflow/../../attacker/repo/releases", false)]
    [InlineData("https://github.com/saideepreddy7781/voiceflow/releases/%2e%2e/%2e%2e/%2e%2e/attacker", false)]
    [InlineData("https://github.com/saideepreddy7781/voiceflow-evil/releases/tag/v1", false)]
    [InlineData("https://github.com.attacker.example/saideepreddy7781/voiceflow/releases/tag/v1", false)]
    [InlineData("https://user@github.com/saideepreddy7781/voiceflow/releases/tag/v1", false)]
    [InlineData("http://github.com/saideepreddy7781/voiceflow/releases/tag/v1", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    public void TheUpdateLinkOnlyOpensThisRepositorysReleases(string url, bool accepted) =>
        Assert.Equal(accepted ? url : UpdateChecker.ReleasesUrl, UpdateChecker.SafeUrl(url));

    [Fact]
    public async Task ALinkOutsideTheRepositoryFallsBackToReleases()
    {
        var result = await Check(new Stub(HttpStatusCode.OK, """{"tag_name":"v2.0.0","html_url":"https://evil.example/app.exe"}"""));
        Assert.Equal(UpdateChecker.ReleasesUrl, result.Release!.Url);
    }
}

public class MeetingDetectorTests
{
    static WindowSnapshot W(string process, string title = "") => new(process, title);

    [Fact]
    public void ZoomWithTheMicOpenIsACall() => Assert.Equal("Zoom", MeetingDetector.Detect([W("Zoom.exe", "Zoom Workplace")], true));

    [Fact]
    public void ZoomWithoutTheMicIsNot() => Assert.Null(MeetingDetector.Detect([W("Zoom.exe", "Zoom Workplace")], false));

    [Fact]
    public void GoogleMeetTitleInABrowser() => Assert.Equal("Google Meet", MeetingDetector.Detect([W("chrome.exe", "Meet - abc-defg-hij - Google Chrome")], true));

    [Fact]
    public void MeetTitleOutsideABrowserIsIgnored() => Assert.Null(MeetingDetector.Detect([W("notepad.exe", "Meet - notes")], true));

    [Fact]
    public void ChatAppsDoNotCount() => Assert.Null(MeetingDetector.Detect([W("Slack.exe", "general"), W("Discord.exe", "voice")], true));

    [Fact]
    public void UnknownMicNeedsACallTitle()
    {
        Assert.Null(MeetingDetector.Detect([W("Zoom.exe", "Zoom Workplace")], null));
        Assert.Equal("Zoom", MeetingDetector.Detect([W("Zoom.exe", "Zoom Meeting")], null));
        Assert.Equal("Microsoft Teams", MeetingDetector.Detect([W("ms-teams.exe", "Weekly sync | Meeting | Microsoft Teams")], null));
    }

    [Fact]
    public void DedicatedAppsOutrankEachOtherInOrder() =>
        Assert.Equal("Zoom", MeetingDetector.Detect([W("skype.exe"), W("zoom.exe")], true));

    [Fact]
    public void NothingRunning() => Assert.Null(MeetingDetector.Detect([], true));
}

public class WorkspaceFilesTests
{
    const string Storage = """{"openedPathsList":{"entries":[{"folderUri":"file:///c%3A/code/voiceflow"},{"folderUri":"file:///c%3A/code/other"}]}}""";

    [Fact]
    public void FindsTheProjectFromTheTitle()
    {
        var folder = WorkspaceFiles.ProjectFolder(Storage, "Program.cs - voiceflow - Cursor", _ => true);
        Assert.NotNull(folder);
        Assert.EndsWith("voiceflow", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMatchingProject() => Assert.Null(WorkspaceFiles.ProjectFolder(Storage, "Untitled - Cursor", _ => true));

    [Fact]
    public void AMissingFolderIsSkipped() => Assert.Null(WorkspaceFiles.ProjectFolder(Storage, "x - voiceflow - Cursor", _ => false));

    [Fact]
    public void MatchesSpokenFileNames()
    {
        var files = new[] { "src/AuthController.cs", "src/app.cs", "docs/Readme.md", "lib/user_service.py" };
        Assert.Equal(["AuthController.cs"], WorkspaceFiles.Mentioned("check the auth controller please", files));
        Assert.Equal(["user_service.py"], WorkspaceFiles.Mentioned("open user service", files));
        Assert.Empty(WorkspaceFiles.Mentioned("the app is fine", files));   // too short to be a confident match
    }

    [Fact]
    public void StorageFolders()
    {
        Assert.Equal("Cursor", WorkspaceFiles.StorageFolder("Cursor.exe"));
        Assert.Equal("Code", WorkspaceFiles.StorageFolder(@"C:\apps\Code.exe"));
        Assert.Null(WorkspaceFiles.StorageFolder("notepad.exe"));
    }
}

public class LeadingSpaceTests
{
    [Theory]
    [InlineData("world", "hello", " world")]
    [InlineData("world", "hello ", "world")]
    [InlineData("world", "(", "world")]
    [InlineData(", then", "hello", ", then")]
    [InlineData("world", "", "world")]
    [InlineData("", "hello", "")]
    [InlineData("next", "line\n", "next")]
    public void Spacing(string text, string before, string expected) => Assert.Equal(expected, OutputSafety.WithLeadingSpace(text, before));
}

public class ImportAndPreferenceTests
{
    [Fact]
    public void DictionaryWordsSplitOnLinesAndCommas() =>
        Assert.Equal(["Saideep", "Groq", "Hyderabad"], LibraryImport.DictionaryWords("Saideep, Groq\r\nHyderabad\n\n"));

    [Fact]
    public void SnippetsSplitOnTabOrComma()
    {
        var parsed = LibraryImport.Snippets("sign off\tThanks, Sai\r\nmy city,Hyderabad\r\nno separator here\n");
        Assert.Equal([("sign off", "Thanks, Sai"), ("my city", "Hyderabad")], parsed);
    }

    [Fact]
    public void NewPreferencesDefaultOnAndRoundTrip()
    {
        var defaults = Preferences.FromJson("{}");
        Assert.True(defaults.AutoLearnWords);
        Assert.True(defaults.MeetingsDetect);
        Assert.True(defaults.CheckForUpdates);
        Assert.False(defaults.ShowBarWhenIdle);
        var p = new Preferences { AutoLearnWords = false, MeetingsDetect = false, CheckForUpdates = false };
        var back = Preferences.FromJson(p.ToJson());
        Assert.False(back.AutoLearnWords);
        Assert.False(back.MeetingsDetect);
        Assert.False(back.CheckForUpdates);
    }

    [Fact]
    public void ExistingSettingsKeepLoading()
    {
        var old = """{"hotkey":"rightCtrl","languages":["en","te"],"onboarded":true,"historyRetentionDays":7}""";
        var p = Preferences.FromJson(old);
        Assert.Equal(Hotkey.RightCtrl, p.Hotkey);
        Assert.True(p.Onboarded);
        Assert.Equal(7, p.HistoryRetentionDays);
        Assert.True(p.CheckForUpdates);
    }
}

public class ScratchpadShortcutTests
{
    const int LMenu = HotkeyProcessor.VK_LMENU, LShift = HotkeyProcessor.VK_LSHIFT, S = HotkeyProcessor.VK_S;

    static HookResult Press(HotkeyProcessor p, params int[] keys)
    {
        var last = HookResult.PassNothing;
        foreach (var k in keys) last = p.Handle(new KeyInput(k, true), 0);
        return last;
    }

    [Fact]
    public void AltShiftSIsSwallowedAndReported()
    {
        var r = Press(new HotkeyProcessor(Hotkey.CtrlWin, new GestureRecognizer()), LMenu, LShift, S);
        Assert.Equal(HookVerdict.Swallow, r.Verdict);
        Assert.Equal(HotkeyShortcut.Scratchpad, r.Shortcut);
    }

    [Fact]
    public void PlainAltSPassesToTheAppMenu()
    {
        var r = Press(new HotkeyProcessor(Hotkey.CtrlWin, new GestureRecognizer()), LMenu, S);
        Assert.Equal(HookVerdict.Pass, r.Verdict);
        Assert.Null(r.Shortcut);
    }

    [Fact]
    public void AltShiftWithoutCtrlOnly()
    {
        var r = Press(new HotkeyProcessor(Hotkey.CtrlWin, new GestureRecognizer()), HotkeyProcessor.VK_LCONTROL, LMenu, LShift, S);
        Assert.Null(r.Shortcut);
    }
}
