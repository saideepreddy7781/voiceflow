using System.Text;
using Page = VoiceFlow.Desktop.ViewModels.Page;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceFlow.Core;
using VoiceFlow.Desktop;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.Services.Demo;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;

// Renders every screen to PNG on any OS: dotnet run --project ... -- --screenshots <dir>
if (args.Length < 2 || args[0] != "--screenshots")
{
    Console.Error.WriteLine("usage: --screenshots <dir>");
    return 2;
}

var dir = args[1];
Directory.CreateDirectory(dir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, OverlayPopups = true })
    .SetupWithoutStarting();

var issues = new StringBuilder();
foreach (var (theme, label) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
{
    Application.Current!.RequestedThemeVariant = theme;
    ScreenRunner.All(dir, label, issues);
}

File.WriteAllText(Path.Combine(dir, "clip-report.txt"), issues.Length > 0 ? issues.ToString() : "No clipped or trimmed text found.\n");
Console.WriteLine(issues.Length > 0 ? issues.ToString() : "No clipped or trimmed text found.");
return 0;

static class ScreenRunner
{
    static readonly DateTimeOffset Clock = new(2026, 11, 10, 18, 42, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 10)));

    static DemoServices NewServices(bool empty = false) => new(Clock, animated: false, empty: empty);

    public static void All(string dir, string theme, StringBuilder issues)
    {
        Shots(dir, theme, issues, 1180, 800, "");
        Shots(dir, theme, issues, 960, 560, "-narrow");
        Onboarding(dir, theme, issues);
        FlowBar(dir, theme, issues);
    }

    static void Shots(string dir, string theme, StringBuilder issues, double width, double height, string suffix)
    {
        void Capture(string name, Action<ShellViewModel, DemoServices> arrange, Action<MainWindow, ShellViewModel>? after = null, bool empty = false)
        {
            var services = NewServices(empty);
            var shell = new ShellViewModel(services, Clock);
            var window = new MainWindow { DataContext = shell, Width = width, Height = height };
            arrange(shell, services);
            Host.Show(window);
            after?.Invoke(window, shell);
            Host.Save(window, Path.Combine(dir, $"{name}{suffix}-{theme}.png"), issues, $"{name}{suffix}-{theme}");
            Host.AllowTransitions = false;
            window.Close();
        }

        Capture("home", (s, _) => s.Navigate(Page.Home), PreviewHover);
        Capture("home-setup", (s, d) =>
        {
            d.DemoSetup.Set(new SetupProblem("Groq key missing.", "Add your free key to start dictating."),
                new SetupProblem("Microphone blocked.", "Allow microphone access in Windows Settings."));
            s.Navigate(Page.Home);
        });
        Capture("home-update", (s, d) => { d.DemoUpdates.SetAvailable("0.2.0"); s.Navigate(Page.Home); });
        Capture("home-empty", (s, _) => { s.Navigate(Page.Home); s.Home.SearchText = "zzzz no matches"; });
        Capture("meetings", (s, _) => s.Navigate(Page.Meetings));
        Capture("meetings-transcript", (s, _) => { s.Navigate(Page.Meetings); ((MeetingDetailViewModel)s.Meetings.Pane!).ShowTranscript = true; });
        Capture("meetings-live", (s, d) => { ((DemoMeetingsAccess)new DemoMeetingsAccess(d)).Begin(); s.Navigate(Page.Meetings); });
        Capture("insights", (s, _) => s.Navigate(Page.Insights));
        Capture("dictionary", (s, _) => s.Navigate(Page.Dictionary));
        Capture("snippets", (s, _) => s.Navigate(Page.Snippets));
        Capture("style", (s, _) => s.Navigate(Page.Style));
        Capture("scratchpad", (s, _) => s.Navigate(Page.Scratchpad));
        Capture("help", (s, _) => s.Navigate(Page.Help));
        for (int i = 0; i < 7; i++)
        {
            var section = i;
            Capture("settings-" + new[] { "general", "notetaker", "system", "keys", "privacy", "advanced", "about" }[i],
                (s, d) => { d.Microphone.StartTest(""); s.Navigate(Page.Settings); s.Settings.Sections[section].IsSelected = true; });
        }
        Capture("dialog-delete", (s, _) =>
        {
            s.Navigate(Page.Dictionary);
            s.Dictionary.Rows[0].DeleteCommand.Execute(null);
        });
        Capture("dialog-add-word", (s, _) => { s.Navigate(Page.Dictionary); s.Dictionary.AddCommand.Execute(null); });

        // Empty states
        foreach (var (name, page) in new[] { ("empty-home", Page.Home), ("empty-meetings", Page.Meetings), ("empty-insights", Page.Insights),
                     ("empty-dictionary", Page.Dictionary), ("empty-snippets", Page.Snippets), ("empty-scratchpad", Page.Scratchpad) })
            Capture(name, (s, _) => s.Navigate(page), empty: true);

        // Focus ring and an open dropdown, the states a static capture would otherwise miss.
        Capture("focus-ring", (s, _) => s.Navigate(Page.Dictionary), (w, _) =>
        {
            var button = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary"));
            button.Focus(NavigationMethod.Tab);
        });
        Capture("dropdown-open", (s, _) => s.Navigate(Page.Settings), (w, _) =>
        {
            var combo = w.GetVisualDescendants().OfType<ComboBox>().First();
            combo.IsDropDownOpen = true;
        });
        Capture("menu-open", (s, _) => { s.Navigate(Page.Home); s.Meetings.Selected = s.Meetings.Items[0]; }, (w, _) =>
        {
            var row = w.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row"));
            row.Classes.Add("previewHover");
            row.GetVisualDescendants().OfType<Button>().Last().Flyout?.ShowAt(row);
        });

        // Runs the real page transition so its animation is exercised, then captures the settled page.
        Capture("transition", (s, _) => s.Navigate(Page.Home), (w, s) =>
        {
            Host.AllowTransitions = true;
            s.Navigate(Page.Insights);
        });
    }

    static void PreviewHover(MainWindow w, ShellViewModel s)
    {
        var row = w.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row"));
        row.Classes.Add("previewHover");
    }

    static void Onboarding(string dir, string theme, StringBuilder issues)
    {
        var names = new[] { "1-welcome", "2-key", "3-mic", "4-hotkey", "5-languages", "6-done" };
        for (int i = 0; i < names.Length; i++)
        {
            var services = NewServices();
            var vm = new OnboardingViewModel(services, () => { }) { Step = i };
            var window = new OnboardingWindow { DataContext = vm };
            Host.Show(window);
            Host.Save(window, Path.Combine(dir, $"onboarding-{names[i]}-{theme}.png"), issues, $"onboarding-{names[i]}-{theme}");
            window.Close();
        }
    }

    static void FlowBar(string dir, string theme, StringBuilder issues)
    {
        var services = NewServices();
        var bars = new float[] { 0.2f, 0.4f, 0.75f, 0.55f, 0.95f, 0.7f, 0.5f, 0.85f, 0.45f, 0.25f, 0.15f };
        var states = new (string Name, Action<DemoDictation> Set)[]
        {
            ("Listening", d => d.Set(FlowPhase.Listening, bars)),
            ("Hands-free", d => d.Set(FlowPhase.HandsFree, bars)),
            ("Command Mode", d => d.Set(FlowPhase.Command, bars)),
            ("Processing", d => d.Set(FlowPhase.Processing, bars)),
            ("Toast while listening", d => d.Set(FlowPhase.Listening, bars, new FlowToast("Pasted into Notepad", "Copied to the clipboard too.", "Undo", () => { }))),
            ("Meeting chip", d => d.Set(FlowPhase.Idle, null, null, DateTimeOffset.Now.AddMinutes(-12))),
            ("Resting bar", d => d.ShowWhenIdle = true),
        };
        var stack = new WrapPanel { Margin = new Thickness(32), Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var (name, set) in states)
        {
            var dictation = new DemoDictation();
            set(dictation);
            var one = new DemoServicesView(services, dictation);
            var cell = new StackPanel { Spacing = 10, Width = 400, Margin = new Thickness(0, 0, 0, 16) };
            cell.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Brushes.White });
            cell.Children.Add(new FlowBarView { DataContext = new FlowBarViewModel(one, runClock: false), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
            stack.Children.Add(cell);
        }
        var window = new Window
        {
            Width = 880,
            Height = 420,
            Content = new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#355C7D"), 0), new GradientStop(Color.Parse("#C06C84"), 1) },
                },
                Child = stack,
            },
        };
        Host.Show(window);
        Host.Save(window, Path.Combine(dir, $"flowbar-{theme}.png"), issues, $"flowbar-{theme}");
        window.Close();
    }
}

sealed class DemoMeetingsAccess(DemoServices services)
{
    // The recording state lives on the demo service; the method is not on the interface the app sees.
    public void Begin()
    {
        var method = services.Meetings.GetType().GetMethod("BeginDemoRecording")!;
        method.Invoke(services.Meetings, [0.72, 0.38]);
    }
}

/// <summary>Demo services with a single replaced dictation source, so each Flow bar state is independent.</summary>
sealed class DemoServicesView(DemoServices inner, IDictationService dictation) : IAppServices
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

static class Host
{
    public static bool AllowTransitions { get; set; }

    public static void Show(Window window)
    {
        window.Show();
        Pump(window);
    }

    /// <summary>Runs layout and timers, and takes transitions out so captures are stable.</summary>
    static void Pump(Window window)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!AllowTransitions)
            {
                foreach (var host in window.GetVisualDescendants().OfType<TransitioningContentControl>())
                    host.PageTransition = null;
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(8);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static void Save(Window window, string path, StringBuilder issues, string label)
    {
        Pump(window);
        window.SetRenderScaling(2);
        Pump(window);
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        using (var stream = File.Create(path))
        {
#pragma warning disable CS0618 // The replacement overload needs an encoder options type this tool does not use.
            frame.Save(stream);
#pragma warning restore CS0618
        }
        Audit(window, issues, label);
        Console.WriteLine(path);
    }

    /// <summary>Flags text that does not fit its box, so 200% scaling (half the DIPs) stays honest.</summary>
    static void Audit(Window window, StringBuilder issues, string label)
    {
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text)) continue;
            var layout = text.TextLayout;
            var trimmed = text.MaxLines == 0 && text.TextTrimming != TextTrimming.None && layout.TextLines.Any(l => l.HasCollapsed);
            var overflow = text.TextWrapping == TextWrapping.NoWrap && layout.WidthIncludingTrailingWhitespace > text.Bounds.Width + 1;
            var outside = !IsInsideWindow(window, text);
            if (trimmed || overflow || outside)
            {
                var snippet = text.Text.Length > 60 ? text.Text[..60] + "..." : text.Text;
                issues.AppendLine($"{label}: {(trimmed ? "trimmed" : overflow ? "overflow" : "outside")}: \"{snippet}\"");
            }
        }
    }

    static bool IsInsideWindow(Window window, Control control)
    {
        var p = control.TranslatePoint(new Point(0, 0), window);
        if (p is null) return false;
        var r = new Rect(p.Value, control.Bounds.Size);
        // Scrolled-away content is fine; only flag what sticks out past the window horizontally.
        return r.Left >= -1 && r.Right <= window.Bounds.Width + 1;
    }
}
