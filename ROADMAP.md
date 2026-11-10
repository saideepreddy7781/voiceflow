# Roadmap

This is the direction of the project, with no dates attached. VoiceFlow is free and maintained in spare
time, so things land when they are ready. If something here matters to you, say so on an issue or send a
pull request.

## Next

- Windows catching up with macOS: meeting detection, Scratchpad, Insights, text before the cursor, and
  auto-learning from your edits.
- Real screenshots and a walkthrough in the README.
- Automatic updates using a signed update feed, because downloading each release by hand means fixes reach
  people slowly.
- Stronger safety checks: comparing the exact text field before typing, and asking for confirmation when
  Command Mode would replace more than half of a field.

## Later

- Moving the prompts into shared files that both apps embed, in place of the parity check.
- Linux, which is not started. The design notes are in [docs/CROSS-PLATFORM.md](docs/CROSS-PLATFORM.md).
- Windows code signing through a free program for open-source projects, so SmartScreen warns less often.

## Decisions that are settled

- VoiceFlow stays free and open source under the MIT license.
- You bring your own Groq key. There are no accounts, servers or analytics.
- Distribution is through GitHub. The project has no Apple Developer account and is not on the App Store, so
  macOS builds are not notarized. [docs/RELEASING.md](docs/RELEASING.md) has the reasoning.
