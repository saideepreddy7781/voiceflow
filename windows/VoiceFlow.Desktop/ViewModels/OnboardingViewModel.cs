using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class OnboardingStep : ObservableObject
{
    public OnboardingStep(string name) => Name = name;
    public string Name { get; }
    [ObservableProperty] bool _reached;
}

public sealed partial class OnboardingViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action _done;

    public OnboardingViewModel(IAppServices services, Action done)
    {
        _services = services;
        _done = done;
        Steps = ["Welcome", "Groq key", "Microphone", "Hotkey", "Languages", "Done"];
        Pills = Steps.Select(s => new OnboardingStep(s)).ToList();
        Groq = new KeyField(SecretKey.Groq, "Groq", "console.groq.com/keys", Core.Groq.KeysUrl, services);
        General = new GeneralSettings(services);
        Groq.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(KeyField.IsStored) or nameof(KeyField.ResultOk)) OnPropertyChanged(nameof(CanContinue));
        };
        services.Dictation.Changed += (_, _) => OnPropertyChanged(nameof(HotkeyDetected));
        UpdatePills();
    }

    public IReadOnlyList<string> Steps { get; }
    public IReadOnlyList<OnboardingStep> Pills { get; }
    public KeyField Groq { get; }
    public GeneralSettings General { get; }
    public string HotkeyName => HotkeyInfo.DisplayName(_services.Preferences.Current.Hotkey);
    public string HandsFreeName => HotkeyInfo.HandsFree(_services.Preferences.Current.Hotkey);
    public bool HotkeyDetected => _services.Dictation.Phase != FlowPhase.Idle;
    public string HotkeyPrompt => HotkeyDetected ? "Got it. Release to finish." : $"Try it: hold {HotkeyName} now. The bar appears at the bottom of your screen.";

    [ObservableProperty] int _step;

    public bool IsWelcome => Step == 0;
    public bool IsKey => Step == 1;
    public bool IsMic => Step == 2;
    public bool IsHotkey => Step == 3;
    public bool IsLanguages => Step == 4;
    public bool IsDone => Step == 5;
    public bool ShowBack => Step > 0 && !IsDone;
    public bool ShowContinue => !IsDone;
    public string ContinueLabel => "Continue";
    public string StepCaption => $"Step {Step + 1} of {Steps.Count}  ·  {Steps[Step]}";

    /// <summary>The key step is the only one that must be finished before moving on.</summary>
    public bool CanContinue => !IsKey || Groq.IsStored;

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(string.Empty);
        UpdatePills();
    }

    void UpdatePills()
    {
        for (int i = 0; i < Pills.Count; i++) Pills[i].Reached = i <= Step;
    }

    [RelayCommand]
    void Next()
    {
        if (Step < Steps.Count - 1) Step++;
    }

    [RelayCommand]
    void Back()
    {
        if (Step > 0) Step--;
    }

    [RelayCommand]
    void Finish()
    {
        _services.Preferences.Update(p => p.Onboarded = true);
        _done();
    }
}
