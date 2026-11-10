using VoiceFlow.Core.Input;
using Xunit;
using A = VoiceFlow.Core.Input.GestureAction;
using E = VoiceFlow.Core.Input.GestureEvent;
using H = VoiceFlow.Core.Input.HotkeyProcessor;

namespace VoiceFlow.Core.Tests;

public class GestureTests
{
    static List<A> Run(params E[] events)
    {
        var r = new GestureRecognizer();
        return events.SelectMany(r.Handle).ToList();
    }

    static readonly A BeginD = A.Begin(SessionKind.Dictation), BeginC = A.Begin(SessionKind.Command);
    static readonly A FinishD = A.Finish(SessionKind.Dictation), FinishC = A.Finish(SessionKind.Command);

    /// <summary>The hook can miss a release behind a UAC prompt; the real key state ends the session.</summary>
    [Fact]
    public void AReleaseTheHookMissedEndsTheChord()
    {
        var physicallyDown = new HashSet<int> { H.VK_LCONTROL, H.VK_LWIN };
        var p = new H(Hotkey.CtrlWin, new GestureRecognizer(), physicallyDown.Contains);
        p.Handle(new KeyInput(H.VK_LCONTROL, true), 0);
        Assert.Equal([BeginD], p.Handle(new KeyInput(H.VK_LWIN, true), 0.1).Actions);
        Assert.Empty(p.ReleaseChordIfUp(1));
        Assert.True(p.ChordActive);

        physicallyDown.Clear();
        Assert.Equal([FinishD], p.ReleaseChordIfUp(2));
        Assert.False(p.ChordActive);

        Assert.Empty(p.ReleaseChordIfUp(3));
    }

    [Fact]
    public void ChordCheckIgnoresASessionWithoutAKeyStateSource()
    {
        var p = new H(Hotkey.CtrlWin, new GestureRecognizer());
        p.Handle(new KeyInput(H.VK_LCONTROL, true), 0);
        p.Handle(new KeyInput(H.VK_LWIN, true), 0.1);
        Assert.Empty(p.ReleaseChordIfUp(5));
        Assert.True(p.ChordActive);
    }

    [Fact] public void HoldIsPushToTalk() => Assert.Equal([BeginD, FinishD], Run(E.HotkeyDown(0, false), E.HotkeyUp(1.2)));

    [Fact] public void RecordingStartsOnPress() => Assert.Equal([BeginD], new GestureRecognizer().Handle(E.HotkeyDown(0, false)));

    [Fact]
    public void SingleQuickTapIsCancelledAfterTheWindow()
    {
        var r = new GestureRecognizer();
        r.Handle(E.HotkeyDown(0, false));
        Assert.Empty(r.Handle(E.HotkeyUp(0.1)));
        Assert.Equal(0.1 + r.DoubleTapWindow, r.Deadline);
        Assert.Empty(r.Handle(E.Tick(0.2)));          // window not over yet
        Assert.Equal([A.Cancel], r.Handle(E.Tick(0.7)));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void HotkeySpaceLocksHandsFree() => Assert.Equal([BeginD, A.Lock, FinishD], Run(
        E.HotkeyDown(0, false), E.Space(0.1), E.HotkeyUp(0.2),
        E.Space(3),                                   // typing a space later is ignored
        E.HotkeyDown(8, false), E.HotkeyUp(8.1)));

    [Fact]
    public void DoubleTapLocksAndNextPressFinishes() => Assert.Equal([BeginD, A.Lock, FinishD], Run(
        E.HotkeyDown(0, false), E.HotkeyUp(0.1),
        E.HotkeyDown(0.3, false), E.HotkeyUp(0.4),    // release after lock is ignored
        E.HotkeyDown(9.0, false), E.HotkeyUp(9.1)));

    [Fact]
    public void SecondPressAfterWindowStartsFresh() =>
        Assert.Equal([BeginD, A.Cancel, BeginD], Run(E.HotkeyDown(0, false), E.HotkeyUp(0.1), E.HotkeyDown(2.0, false)));

    [Fact] public void ModifierAtPressIsCommandMode() => Assert.Equal([BeginC, FinishC], Run(E.HotkeyDown(0, true), E.HotkeyUp(2)));

    [Fact]
    public void ModifierDuringHoldSwitchesToCommandMode() =>
        Assert.Equal([BeginD, A.BecomeCommand, FinishC], Run(E.HotkeyDown(0, false), E.ControlDown(0.1), E.HotkeyUp(2)));

    [Fact]
    public void CommandDoublePressLocksHandsFree() => Assert.Equal([BeginC, A.Lock, FinishC], Run(
        E.HotkeyDown(0, true), E.HotkeyUp(0.1), E.HotkeyDown(0.4, true), E.HotkeyDown(6, false)));

    [Fact]
    public void BackspaceCancelsCommandMode()
    {
        Assert.Equal([BeginC, A.Cancel], Run(E.HotkeyDown(0, true), E.Backspace(1)));
        Assert.Equal([BeginC, A.Lock, A.Cancel], Run(E.HotkeyDown(0, true), E.HotkeyUp(0.1), E.HotkeyDown(0.3, true), E.Backspace(4)));
    }

    [Fact]
    public void BackspaceDuringHandsFreeDictationIsTyping() => Assert.Equal([BeginD, A.Lock, FinishD], Run(
        E.HotkeyDown(0, false), E.Space(0.1), E.Backspace(2), E.HotkeyDown(5, false)));

    [Fact] public void EscapeCancelsWhileHolding() => Assert.Equal([BeginD, A.Cancel], Run(E.HotkeyDown(0, false), E.Escape(1), E.HotkeyUp(2)));

    [Fact]
    public void EscapeCancelsHandsFree() =>
        Assert.Equal([BeginD, A.Lock, A.Cancel], Run(E.HotkeyDown(0, false), E.HotkeyUp(0.1), E.HotkeyDown(0.3, false), E.Escape(5)));

    [Fact]
    public void OtherKeyWhileHoldingIsAShortcut() =>
        Assert.Equal([BeginD, A.Cancel], Run(E.HotkeyDown(0, false), E.OtherKey(0.2), E.HotkeyUp(0.3)));

    [Fact]
    public void TypingDuringHandsFreeIsIgnored() => Assert.Equal([BeginD, A.Lock, FinishD], Run(
        E.HotkeyDown(0, false), E.HotkeyUp(0.1), E.HotkeyDown(0.3, false), E.OtherKey(3), E.HotkeyDown(6, false)));

    [Fact] public void StrayReleaseWhileIdleDoesNothing() => Assert.Empty(Run(E.HotkeyUp(0), E.Tick(1), E.Escape(2), E.Space(3)));

    [Fact]
    public void HoldJustOverThresholdFinishes()
    {
        var r = new GestureRecognizer();
        r.Handle(E.HotkeyDown(0, false));
        Assert.Equal([FinishD], r.Handle(E.HotkeyUp(r.TapThreshold + 0.01)));
    }

    [Fact]
    public void HandsFreeFromTheBarFinishesOrCancels()
    {
        var r = new GestureRecognizer();
        r.EnterHandsFree();
        Assert.Equal([FinishD], r.Handle(E.HotkeyDown(0, false)));
        var e = new GestureRecognizer();
        e.EnterHandsFree();
        Assert.Equal([A.Cancel], e.Handle(E.Escape(0)));
    }
}

/// <summary>The Windows key-hook decisions: chord forming, swallowing, Start menu masking, shortcuts.</summary>
public class HotkeyProcessorTests
{
    static KeyInput Down(int vk, int scan = 0) => new(vk, true, scan);
    static KeyInput Up(int vk) => new(vk, false);

    static (HotkeyProcessor P, List<A> Actions, Func<KeyInput, double, HookResult> Feed) Make(Hotkey hotkey = Hotkey.CtrlWin)
    {
        var p = new HotkeyProcessor(hotkey, new GestureRecognizer());
        var actions = new List<A>();
        return (p, actions, (k, t) =>
        {
            var r = p.Handle(k, t);
            actions.AddRange(r.Actions);
            return r;
        });
    }

    [Fact]
    public void CtrlWinHoldDictatesAndWinReleaseIsMasked()
    {
        var (_, actions, feed) = Make();
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_LCONTROL), 0).Verdict);
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_LWIN), 0.05).Verdict);      // chord formed: recording starts
        feed(Down(H.VK_LWIN), 0.5);                                                // auto-repeat changes nothing
        Assert.Equal(HookVerdict.SwallowAndReleaseWithMask, feed(Up(H.VK_LWIN), 1.5).Verdict);
        Assert.Equal(HookVerdict.Pass, feed(Up(H.VK_LCONTROL), 1.6).Verdict);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Finish(SessionKind.Dictation)], actions);
    }

    [Fact]
    public void ChordWorksInEitherOrderAndCtrlReleaseFinishes()
    {
        var (_, actions, feed) = Make();
        feed(Down(H.VK_RWIN), 0);
        feed(Down(H.VK_RCONTROL), 0.02);
        Assert.Equal(HookVerdict.Pass, feed(Up(H.VK_RCONTROL), 1).Verdict);
        Assert.Equal(HookVerdict.SwallowAndReleaseWithMask, feed(Up(H.VK_RWIN), 1.1).Verdict);   // Win still masked afterwards
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Finish(SessionKind.Dictation)], actions);
    }

    [Fact]
    public void PlainWinAndPlainCtrlAreLeftAlone()
    {
        var (_, actions, feed) = Make();
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_LWIN), 0).Verdict);
        Assert.Equal(HookVerdict.Pass, feed(Up(H.VK_LWIN), 0.1).Verdict);          // Start menu opens as usual
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_LCONTROL), 1).Verdict);
        Assert.Equal(HookVerdict.Pass, feed(Down(0x43), 1.1).Verdict);              // Ctrl+C
        Assert.Equal(HookVerdict.Pass, feed(Up(0x43), 1.2).Verdict);
        Assert.Equal(HookVerdict.Pass, feed(Up(H.VK_LCONTROL), 1.3).Verdict);
        Assert.Empty(actions);
    }

    [Fact]
    public void SpaceLocksHandsFreeAndIsSwallowedWithItsRepeatsAndRelease()
    {
        var (_, actions, feed) = Make();
        feed(Down(H.VK_LCONTROL), 0);
        feed(Down(H.VK_LWIN), 0);
        Assert.Equal(HookVerdict.Swallow, feed(Down(H.VK_SPACE), 0.1).Verdict);   // else Ctrl+Win+Space switches input language
        Assert.Equal(HookVerdict.Swallow, feed(Down(H.VK_SPACE), 0.6).Verdict);   // auto-repeat
        Assert.Equal(HookVerdict.Swallow, feed(Up(H.VK_SPACE), 0.7).Verdict);
        feed(Up(H.VK_LWIN), 0.8);
        feed(Up(H.VK_LCONTROL), 0.8);
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_SPACE), 3).Verdict);        // typing while hands-free
        feed(Up(H.VK_SPACE), 3.1);
        feed(Down(H.VK_LCONTROL), 8);
        feed(Down(H.VK_LWIN), 8);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Lock, A.Finish(SessionKind.Dictation)], actions);
    }

    [Fact]
    public void EscapeCancelsAndIsSwallowedOnlyWhenItDidSomething()
    {
        var (_, actions, feed) = Make();
        Assert.Equal(HookVerdict.Pass, feed(Down(H.VK_ESCAPE), 0).Verdict);       // idle: Esc belongs to the app
        feed(Up(H.VK_ESCAPE), 0.1);
        feed(Down(H.VK_LCONTROL), 1);
        feed(Down(H.VK_LWIN), 1);
        Assert.Equal(HookVerdict.Swallow, feed(Down(H.VK_ESCAPE), 1.5).Verdict);
        Assert.Equal(HookVerdict.Swallow, feed(Up(H.VK_ESCAPE), 1.6).Verdict);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Cancel], actions);
    }

    [Fact]
    public void AltIsCommandModeAndItsReleaseIsMasked()
    {
        var (_, actions, feed) = Make();
        feed(Down(H.VK_LCONTROL), 0);
        feed(Down(H.VK_LMENU), 0);
        feed(Down(H.VK_LWIN), 0.01);
        Assert.Equal(HookVerdict.SwallowAndReleaseWithMask, feed(Up(H.VK_LMENU), 2).Verdict);   // no menu-bar focus
        feed(Up(H.VK_LWIN), 2.1);
        Assert.Equal([A.Begin(SessionKind.Command), A.Finish(SessionKind.Command)], actions);

        var (_, later, feed2) = Make();
        feed2(Down(H.VK_LCONTROL), 0);
        feed2(Down(H.VK_LWIN), 0);
        feed2(Down(H.VK_RMENU), 0.3);
        feed2(Up(H.VK_LWIN), 2);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.BecomeCommand, A.Finish(SessionKind.Command)], later);
    }

    [Fact]
    public void ShortcutWhileHoldingCancelsAndPassesThrough()
    {
        var (_, actions, feed) = Make();
        feed(Down(H.VK_LCONTROL), 0);
        feed(Down(H.VK_LWIN), 0);
        Assert.Equal(HookVerdict.Pass, feed(Down(0x27), 0.1).Verdict);            // Ctrl+Win+Right: next desktop
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Cancel], actions);
    }

    [Fact]
    public void OwnInjectedKeysAreIgnored()
    {
        var (p, actions, feed) = Make();
        Assert.Equal(HookVerdict.Pass, feed(new KeyInput(H.VK_LCONTROL, true, OwnInjection: true), 0).Verdict);
        feed(new KeyInput(H.VK_LWIN, true, OwnInjection: true), 0);
        feed(new KeyInput(H.MaskVk, true, OwnInjection: true), 0);
        Assert.Empty(actions);
        Assert.False(p.ChordActive);
    }

    [Fact]
    public void RightAltHotkeyIgnoresAltGrFakeCtrlAndUsesShiftForCommand()
    {
        var (_, actions, feed) = Make(Hotkey.RightAlt);
        feed(Down(H.VK_LCONTROL, 0x21D), 0);                                      // AltGr's fake Left Ctrl
        feed(Down(H.VK_RMENU), 0);
        feed(Down(H.VK_LSHIFT), 0.2);
        feed(Up(H.VK_LSHIFT), 0.9);
        Assert.Equal(HookVerdict.SwallowAndReleaseWithMask, feed(Up(H.VK_RMENU), 1).Verdict);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.BecomeCommand, A.Finish(SessionKind.Command)], actions);
    }

    [Fact]
    public void RightCtrlHotkey()
    {
        var (_, actions, feed) = Make(Hotkey.RightCtrl);
        feed(Down(H.VK_LCONTROL), 0);                                             // left Ctrl is not the hotkey
        feed(Up(H.VK_LCONTROL), 0.1);
        feed(Down(H.VK_RCONTROL), 1);
        Assert.Equal(HookVerdict.Pass, feed(Up(H.VK_RCONTROL), 2).Verdict);
        Assert.Equal([A.Begin(SessionKind.Dictation), A.Finish(SessionKind.Dictation)], actions);
    }

    [Fact]
    public void PasteLastShortcutIsAltShiftV()
    {
        var (_, _, feed) = Make();
        feed(Down(H.VK_LMENU), 0);
        feed(Down(H.VK_LSHIFT), 0);
        var r = feed(Down(H.VK_V), 0.1);
        Assert.Equal(HotkeyShortcut.PasteLast, r.Shortcut);
        Assert.Equal(HookVerdict.Swallow, r.Verdict);
        Assert.Equal(HookVerdict.Swallow, feed(Up(H.VK_V), 0.2).Verdict);
        var (_, _, plain) = Make();
        plain(Down(H.VK_LMENU), 0);
        Assert.Null(plain(Down(H.VK_V), 0.1).Shortcut);                            // Alt+V is the app's
    }

    [Fact]
    public void StaleModifiersAreDroppedWhenTheHookMissedTheirRelease()
    {
        var physicallyDown = new HashSet<int>();
        var p = new HotkeyProcessor(Hotkey.CtrlWin, new GestureRecognizer(), vk => physicallyDown.Contains(vk));
        physicallyDown.Add(H.VK_LWIN);
        p.Handle(Down(H.VK_LWIN), 0);
        physicallyDown.Clear();                                                    // Win+L: the release never reached the hook
        physicallyDown.Add(H.VK_LCONTROL);
        var r = p.Handle(Down(H.VK_LCONTROL), 10);                                // a plain Ctrl press later
        Assert.Empty(r.Actions);
        Assert.False(p.ChordActive);
    }
}
