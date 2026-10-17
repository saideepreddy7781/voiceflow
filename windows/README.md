# VoiceFlow for Windows

The Windows version of VoiceFlow: hold a hotkey, speak, and clean text is typed where your cursor is.
It runs in the notification area, uses your own Groq key, and works on Windows 10 and 11 (x64 and Arm64).

## Projects

| Project | Target | What it holds |
| --- | --- | --- |
| `VoiceFlow.Core` | `net8.0` | Everything that does not need Windows: Groq transcription and cleanup, the cleanup prompt, language resolution, audio maths, resampling, the gesture state machine and hotkey decisions, dictionary, snippets, history, stats, meeting recording and notes. |
| `VoiceFlow.Core.Tests` | `net8.0` | xUnit tests. They run on any OS. Live Groq tests run only when `GROQ_API_KEY` is set. |
| `VoiceFlow.Windows` | `net8.0-windows` | The WinForms app: keyboard hook, clipboard, SendInput, UI Automation, WASAPI capture (NAudio), Credential Manager, tray icon, Flow bar and windows. Code-only UI. |

Dependencies: NAudio.Wasapi 2.2.1 (MIT) for audio capture, xUnit for tests. Nothing else.

## Build and test

Install the .NET 8 SDK. On macOS or Linux the Windows app still builds (`EnableWindowsTargeting`), but it only runs on Windows.

```sh
dotnet build windows/VoiceFlow.sln -c Release
dotnet test windows/VoiceFlow.Core.Tests
python3 scripts/check_prompt_parity.py     # the Windows prompts must match the macOS ones exactly
```

Live tests call Groq with the audio clips in `fixtures/clips` (generated on a Mac by `scripts/make_fixtures.py`):

```sh
GROQ_API_KEY=... dotnet test windows/VoiceFlow.Core.Tests
```

## Publish

One self-contained `VoiceFlow.exe` per architecture (about 66 MB for x64, 63 MB for Arm64; it carries the .NET
runtime, so nothing needs to be installed):

```sh
dotnet publish windows/VoiceFlow.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish windows/VoiceFlow.Windows -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Output: `windows/VoiceFlow.Windows/bin/Release/net8.0-windows/<rid>/publish/VoiceFlow.exe`.

## Self-test (for CI on real Windows)

`VoiceFlow.exe --self-test` checks the Windows-specific parts without showing UI or touching your data or keys:
struct layouts, prompt line endings, atomic JSON writes in a temp folder, Credential Manager with a dummy secret,
a clipboard round trip with the history-exclusion formats, installing the keyboard hook and seeing VoiceFlow's own
injected key ignored, UI Automation against its own password box, audio device listing, the Groq request body
(built, never sent), and building every window off-screen.

It prints one line per check (`PASS`, `SKIP` or `FAIL`) and exits with 0 when nothing failed. Checks that need an
interactive desktop or a microphone report `SKIP` when there is none.

`VoiceFlow.exe` is a GUI-subsystem program, so PowerShell does not wait for it and has no console to print to.
The reliable way is a results file plus `Start-Process -Wait`:

```powershell
$p = Start-Process .\VoiceFlow.exe -ArgumentList '--self-test','--self-test-out','selftest.txt' -Wait -PassThru
Get-Content selftest.txt
exit $p.ExitCode
```

Standard output also works when the caller redirects it (for example `-RedirectStandardOutput`), and the exe
attaches to the parent console when run from `cmd.exe`.

## Using it

- **Dictate:** hold **Ctrl + Win**, speak, release. The text is pasted where you were typing.
- **Hands-free:** tap **Ctrl + Win** twice, or press **Ctrl + Win + Space**. Press the hotkey again to finish, **Esc** to cancel. The Flow bar also shows ✕ and ✓.
- **Command Mode:** select text, hold **Ctrl + Win + Alt** (or add Alt while holding) and say what to do, such as "make this formal". Backspace cancels.
- **Paste the last result:** **Alt + Shift + V**, or the tray menu.
- Settings offer **Right Alt** or **Right Ctrl** instead of Ctrl + Win. With Right Alt, Command Mode adds Shift. Do not choose Right Alt if your keyboard layout uses AltGr for typing characters.
- Releasing Win normally opens the Start menu. VoiceFlow sends an unassigned key (VK 0xE8) just before the release, which Windows treats as "Win was used in a shortcut".

Dictations finish automatically at 20 minutes. Recordings under half a second are dropped, and silent recordings are never uploaded.

## Privacy and security

- Your Groq (and optional Gemini) key is stored in Windows Credential Manager as `VoiceFlow/groq` and `VoiceFlow/gemini`. It is never written to a file or the log.
- Settings, dictionary, snippets, history and meetings are JSON files in `%APPDATA%\VoiceFlow`, written atomically. History is kept for 30 days by default.
- The log in `%LOCALAPPDATA%\VoiceFlow\logs` records outcomes and timings, not what you said. Debug logging (off by default) can include dictated text.
- VoiceFlow never types into password fields (UI Automation), password managers, or Windows credential prompts.
- If you switch windows while a dictation is processing, the text is not pasted into the new window. It is kept for Paste last and placed on the clipboard.
- The text VoiceFlow pastes is marked to stay out of Win+V clipboard history and cloud clipboard, and your previous clipboard is put back about 500 ms later.
- In terminals, line breaks are turned into spaces, so dictated text never runs a command. Control characters and bidirectional overrides are removed everywhere.
- Windows blocks synthetic keys sent to apps that run as administrator. VoiceFlow detects this and leaves the text on the clipboard instead.
