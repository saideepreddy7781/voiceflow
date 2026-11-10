# VoiceFlow for Windows

The Windows version of VoiceFlow: hold a hotkey, speak, and clean text is typed where your cursor is.
It runs in the notification area, uses your own Groq key, and works on Windows 10 and 11 (x64 and Arm64).

## Projects

| Project | Target | What it holds |
| --- | --- | --- |
| `VoiceFlow.Core` | `net8.0` | Everything that does not need Windows: Groq transcription and cleanup, the cleanup prompt, language resolution, audio maths, resampling, the gesture state machine and hotkey decisions, dictionary, snippets, history, stats, meeting recording and notes. |
| `VoiceFlow.Core.Tests` | `net8.0` | xUnit tests. They run on any OS. Live Groq tests run only when `GROQ_API_KEY` is set. |
| `VoiceFlow.Desktop` | `net8.0` | The Avalonia UI as a library: pages, Flow bar, onboarding, theme, view models and the service interfaces in `Services/IAppServices.cs`. It has no Windows-only code, so it builds and renders on any OS. |
| `VoiceFlow.Desktop.Tests` | `net8.0` | View-model tests on demo data. |
| `VoiceFlow.Desktop.Screenshots` | `net8.0` | Renders every screen to PNG, on any OS. |
| `VoiceFlow.Windows` | `net8.0-windows` | The app people run. It implements the service interfaces: keyboard hook, clipboard, SendInput, UI Automation, WASAPI capture (NAudio), Credential Manager, the tray icon and the native Flow bar window. |

Dependencies: Avalonia (MIT) for the UI, CommunityToolkit.Mvvm (MIT), NAudio.Wasapi 2.2.1 (MIT) for audio capture, Microsoft.Win32.SystemEvents (MIT) for lock and resume, xUnit for tests.

## Build and test

Install the .NET 8 SDK. On macOS or Linux the Windows app still builds (`EnableWindowsTargeting`), but it only runs on Windows. To see the UI on any OS, render it:

```sh
dotnet run --project windows/VoiceFlow.Desktop.Screenshots -c Release -- --screenshots /tmp/voiceflow-screens
```

```sh
dotnet build windows/VoiceFlow.sln -c Release
dotnet test windows/VoiceFlow.Core.Tests
dotnet test windows/VoiceFlow.Desktop.Tests
python3 scripts/check_prompt_parity.py     # the Windows prompts must match the macOS ones exactly
```

Live tests call Groq with the audio clips in `fixtures/clips` (generated on a Mac by `scripts/make_fixtures.py`):

```sh
GROQ_API_KEY=... dotnet test windows/VoiceFlow.Core.Tests
```

## Publish

One self-contained `VoiceFlow.exe` per architecture (about 47 MB for x64, 46 MB for Arm64; it carries the .NET
runtime and Avalonia, so nothing needs to be installed):

```sh
dotnet publish windows/VoiceFlow.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish windows/VoiceFlow.Windows -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Output: `windows/VoiceFlow.Windows/bin/Release/net8.0-windows/<rid>/publish/VoiceFlow.exe`.

## Self-test (for CI on real Windows)

`VoiceFlow.exe --self-test` checks the Windows-specific parts without touching your data or keys. It runs the real
Avalonia shell, so CI exercises the exe that ships: Avalonia starting under the single-file host, the icons and theme
found inside the exe, Telugu and Hindi text shaped and rendered, every page, settings section and onboarding step built
off-screen, and the real Flow bar window with `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` read back from its handle and shown
only while there is a toast. It also covers struct layouts, prompt line endings, atomic JSON writes in a temp folder,
Credential Manager with a dummy secret, a clipboard round trip with the history-exclusion formats, the keyboard hook on
its own thread seeing VoiceFlow's own injected key and ignoring it, UI Automation against plain Win32 password and edit
boxes (including the text before the caret), audio device listing and the Groq request body (built, never sent). It
leaves no window or tray icon behind.

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
- **Scratchpad:** **Alt + Shift + S** opens it, and pressing it again while it is in front hides it. The macOS shortcut is Option + S, but Alt + S drives app menus on Windows, so this uses the same Alt + Shift family as Paste last.
- **Flow bar:** appears at the bottom centre of the active monitor only while you dictate, while a toast is up, or as a small chip during a meeting. It never takes focus, and clicks pass through it except on its buttons and toasts. Settings → System can keep a resting bar on screen; it is off by default.
- **Meetings:** when Zoom, Teams, Meet, Webex or a similar app is in a call, a toast offers to take notes. VoiceFlow only asks; recording starts only when you click. Turn it off in Settings → Notetaker.
- **Text before the cursor** (about 120 characters, read through UI Automation with a timeout) improves cleanup and adds a space when you dictate mid-sentence. It is never read from password fields, password managers or terminals.
- **Learning:** if you fix a dictated word within 15 seconds and it looks like a name or term, it joins your dictionary, with Undo. Turn it off in Settings → Advanced.
- **File tags:** in Cursor and Windsurf, a spoken file name from the open project becomes an @file tag.
- **Updates:** VoiceFlow asks GitHub for the latest release at launch and once a day and shows a notice on Home and in Settings → About with a link to the release. It never downloads anything. Turn it off in Settings → About.
- Settings offer **Right Alt** or **Right Ctrl** instead of Ctrl + Win. With Right Alt, Command Mode adds Shift. Do not choose Right Alt if your keyboard layout uses AltGr for typing characters.
- Releasing Win normally opens the Start menu. VoiceFlow sends an unassigned key (VK 0xE8) just before the release, which Windows treats as "Win was used in a shortcut".

Dictations finish automatically at 20 minutes. Recordings under half a second are dropped, and silent recordings are never uploaded.

## Privacy and security

- Your Groq (and optional Gemini) key is stored in Windows Credential Manager as `VoiceFlow/groq` and `VoiceFlow/gemini`. It is never written to a file or the log.
- Settings, dictionary, snippets, history, meetings and Scratchpad notes (`notes.json`) are JSON files in `%APPDATA%\VoiceFlow`, written atomically. History is kept for 30 days by default.
- The log in `%LOCALAPPDATA%\VoiceFlow\logs` records outcomes and timings, not what you said. Debug logging (off by default) can include dictated text.
- VoiceFlow never types into password fields (UI Automation), password managers, or Windows credential prompts.
- If you switch windows while a dictation is processing, the text is not pasted into the new window. It is kept for Paste last and placed on the clipboard.
- The text VoiceFlow pastes is marked to stay out of Win+V clipboard history and cloud clipboard, and your previous clipboard is put back about 500 ms later.
- In terminals, line breaks are turned into spaces, so dictated text never runs a command. Control characters and bidirectional overrides are removed everywhere.
- Update checks ask `api.github.com` for the latest release; GitHub sees your IP address and nothing else about you. Turn them off in Settings → About.
- Windows blocks synthetic keys sent to apps that run as administrator. VoiceFlow detects this and leaves the text on the clipboard instead.
