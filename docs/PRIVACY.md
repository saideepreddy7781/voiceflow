# What VoiceFlow sends, stores and reads

VoiceFlow has no servers, no accounts and no analytics. It talks to Groq with your own API key, and to
Perplexity only when you ask Command Mode to search.

## Sent to Groq (`api.groq.com`, over HTTPS)

| When | What | Why |
|---|---|---|
| While you hold the key or use hands-free mode | Microphone audio, in segments of up to 30 s | Speech recognition (Whisper) |
| With each audio segment | Up to ~180 tokens of names: your dictionary words and capitalized names found in the field you're typing in. Telugu and Hindi requests also carry a fixed sample sentence from the app | Correct spelling and script |
| After you finish speaking | Your transcript, the app's name, the style, up to 120 characters before your cursor, up to 40 names, and file names you mentioned (code editors only) | AI cleanup |
| Command Mode only | Your spoken instruction, the selected text, and the text of the focused field (up to 12,000 characters) | Editing or drafting |
| When you press Test in Settings | Your API key, to list Groq's models | Checking the key |

Groq's handling of API data is described in Groq's own privacy policy and terms. VoiceFlow does not
choose Groq's retention settings.

## Notetaker (meeting notes)

Notetaker records only after you start it (from the Meetings page, the menu, or the "Take notes" button
VoiceFlow shows when a call starts). A red chip with a timer stays on screen the whole time.

| What | Where it goes |
|---|---|
| Your microphone and the meeting audio (the other people), uploaded separately. Only the parts with speech go, in pieces of up to 45 s of speech | Groq, for transcription |
| Telugu and Hindi transcript lines, if you listed those languages | Groq, to rewrite them in the writing you chose |
| The transcript, the meeting title and attendee names | Groq, to write the notes |
| The transcript and notes | `meetings.json` on this Mac (owner-only permissions) |
| A Markdown copy of each meeting | Only the folder you choose under Settings → Notetaker → Sync |
| Calendar: the title and attendee names of the event happening now | Read on this Mac, only if you turn on "Use my calendar" |

When you've listed more than one language, the same piece of audio can be sent up to three times, so Whisper
can compare languages. Pieces that fail to upload stay in memory (at most about 20 minutes of audio) for one more
try when the meeting ends, then they are discarded.

If you dictate during a meeting, those words are part of your microphone audio, so they also appear in
the meeting transcript. Audio is never written to disk. Recording other people can require their consent where you live:
tell everyone on the call. VoiceFlow reminds you each time unless you turn the reminder off.

## Sent to Perplexity

Only when Command Mode decides your instruction is a web search ("search for…"): your browser opens
`https://www.perplexity.ai/search?q=…` with the query.

## Never read or sent

- **Password fields** (macOS secure text fields) and **secure input** (password prompts, Terminal's
  Secure Keyboard Entry): VoiceFlow stops listening, uploads nothing, types nothing, copies nothing and
  saves nothing to history.
- **Excluded apps**: password managers (1Password, Bitwarden, Dashlane, LastPass, KeePassXC, Keychain
  Access, Apple Passwords) are treated like password fields.
- **Terminal contents**: in terminal apps no screen text is sent at all (it can be anyone's output),
  and typed text never contains a line break, so dictation can never run a command.
- **Your typing**: the keyboard listener looks only at the dictation key, Esc, Space, Backspace and the
  ⌃⌘V / ⌃⌘C shortcuts. It never records or logs other keys.

## The clipboard

When VoiceFlow has to paste, it puts the text on the clipboard for a moment, presses ⌘V, then puts your
previous clipboard back after 500 ms. After pasting it reads the field once or twice to see whether the
text arrived. That read is compared on this Mac and never saved or sent. That temporary item stays on this Mac (it is not sent to your
other devices by Universal Clipboard) and is marked so clipboard-history apps skip it. If your
clipboard held a password copied from a password manager, VoiceFlow does not put it back.

## Stored on your Mac (macOS)

All files are in `~/Library/Application Support/VoiceFlow/`, readable only by your user account
(permissions `0600`; folder `0700`).

| File | Contents | Control |
|---|---|---|
| `history.json` | Dictations: date, app name, what you said, what was typed | Settings → Privacy: keep forever, 30 days (default), 7 days, or don't keep |
| `dictionary.json` | Your words and auto-learned names | Hub → Dictionary |
| `snippets.json` | Snippet cues and text | Hub → Snippets |
| `notes.json` | Scratchpad notes | Hub → Scratchpad |
| `meetings.json` | Meeting transcripts and notes | Hub → Meetings |
| `diagnostics.json` | The last 200 sessions as metadata only: date, result code, app bundle ID, audio length, timing, microphone restarts. Never what you said, typed or saw | Emptied by "Delete all history"; not written at all when you choose "Never store my data" |

Settings → Data & Privacy → **Export my data** writes all of these except `diagnostics.json` to one JSON
file you choose.

Settings → Advanced → **Copy debug report** puts a text report on your clipboard for bug reports: app and
macOS versions, audio device names (with any owner name removed), result codes, timings and microphone
restart counts. It has no transcripts, window titles or API keys, and nothing is sent anywhere unless you paste it.

Settings live in `~/Library/Preferences/com.saideep.voiceflow.plist`. The Groq key is in your login
Keychain under "com.saideep.voiceflow". Audio is never written to disk.

## Removing everything

Quit VoiceFlow, then:

```bash
rm -rf ~/Library/Application\ Support/VoiceFlow
defaults delete com.saideep.voiceflow
security delete-generic-password -s com.saideep.voiceflow -a groq
security delete-generic-password -s com.saideep.voiceflow -a gemini
rm -rf /Applications/VoiceFlow.app
```

Then remove VoiceFlow from System Settings → Privacy & Security → Accessibility and Microphone.

## On Windows

The Windows app sends the same data to the same services as the Mac app. It stores:

| Where | Contents |
|---|---|
| `%APPDATA%\VoiceFlow\` | Settings, history, dictionary, snippets and meetings as JSON files |
| `%LOCALAPPDATA%\VoiceFlow\logs\` | Outcomes and timings, not what you said (unless you turn on debug logging) |
| Windows Credential Manager, `VoiceFlow/groq` and `VoiceFlow/gemini` | Your API keys |

Text it pastes is marked to stay out of Win+V clipboard history and cloud clipboard, and your previous
clipboard is put back about 500 ms later. To remove everything, quit VoiceFlow from the tray, turn off
Start with Windows in Settings, then in PowerShell:

```powershell
Remove-Item -Recurse "$env:APPDATA\VoiceFlow", "$env:LOCALAPPDATA\VoiceFlow"
cmdkey /delete:VoiceFlow/groq
cmdkey /delete:VoiceFlow/gemini
```

and delete `VoiceFlow.exe`.
