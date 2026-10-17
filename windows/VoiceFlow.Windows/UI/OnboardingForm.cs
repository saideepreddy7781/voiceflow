using VoiceFlow.Core;
using VoiceFlow.Windows.Dictation;

namespace VoiceFlow.Windows.UI;

/// <summary>First run: paste the Groq key, learn the hotkey, done.</summary>
internal sealed class OnboardingForm : Form
{
    readonly AppModel _model;
    readonly TextBox _key = Ui.Input(360, "gsk_…");
    readonly Label _status = Ui.Hint("", 480);
    readonly Button _done;

    public OnboardingForm(AppModel model, Icon? icon)
    {
        _model = model;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Welcome to VoiceFlow";
        Icon = icon;
        Font = Theme.Body;
        BackColor = Theme.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(28, 22, 28, 22);

        _key.UseSystemPasswordChar = true;
        var column = Ui.Column();
        column.Controls.Add(Ui.Heading("Speak, and it types."));
        column.Controls.Add(Ui.Hint("VoiceFlow turns your voice into clean text in any app. It uses your own Groq key: the free tier is plenty for daily dictation.", 520));

        column.Controls.Add(Ui.Section("1. Add your Groq key"));
        var link = new LinkLabel { Text = "Get a free key at console.groq.com/keys", AutoSize = true, LinkColor = Theme.Black, ActiveLinkColor = Theme.Muted, Font = Theme.Body };
        link.LinkClicked += (_, _) => DictationController.OpenUri(Groq.KeysUrl);
        column.Controls.Add(link);
        column.Controls.Add(Ui.Row(_key, Ui.Primary("Save and test", async (_, _) => await SaveAsync())));
        column.Controls.Add(_status);
        column.Controls.Add(Ui.Hint("The key is kept in Windows Credential Manager on this PC. Audio goes only to Groq.", 520));

        var hotkey = model.Preferences.Hotkey;
        column.Controls.Add(Ui.Section("2. Dictate anywhere"));
        column.Controls.Add(Ui.Text($"Click into any text box, hold {HotkeyInfo.DisplayName(hotkey)}, speak, and let go. The text appears where you were typing.", 520));
        column.Controls.Add(Ui.Hint($"Hands-free: tap {HotkeyInfo.DisplayName(hotkey)} twice, or press {HotkeyInfo.HandsFree(hotkey)}. Press the hotkey again to finish, Esc to cancel. " +
                                    $"Command Mode: select text, hold {HotkeyInfo.DisplayName(hotkey)} + {HotkeyInfo.CommandModifier(hotkey)}, and say what to change. " +
                                    "You can switch to Right Alt or Right Ctrl in Settings.", 520));
        column.Controls.Add(Ui.Section("3. Your languages"));
        column.Controls.Add(Ui.Hint("English is set. If you also speak Telugu or another language, add it in Settings → My languages.", 520));

        _done = Ui.Primary("Start using VoiceFlow", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        var later = Ui.Secondary("Later", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var buttons = Ui.Row(_done, later);
        buttons.Margin = new Padding(0, 18, 0, 0);
        column.Controls.Add(buttons);
        Controls.Add(column);
        ResumeLayout(true);
        UpdateState();
    }

    async Task SaveAsync()
    {
        var error = _model.SaveKey(SecretKey.Groq, _key.Text);
        _key.Clear();
        if (error != null) { _status.Text = error; return; }
        _status.Text = "Saved. Checking the key…";
        var result = await _model.CheckKeyAsync(SecretKey.Groq);
        _status.Text = result.Status switch
        {
            KeyCheckStatus.Ok => "The key works.",
            KeyCheckStatus.InvalidKey => "Groq rejected this key. Copy it again from console.groq.com/keys.",
            _ => $"Saved, but it could not be checked: {result.Detail}",
        };
        UpdateState();
    }

    void UpdateState() => _done.Enabled = _model.HasKey(SecretKey.Groq);
}
