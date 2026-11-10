# Roadmap

This is the direction of the project, with no dates attached. VoiceFlow is free and maintained in spare
time, so things land when they are ready. If something here matters to you, say so on an issue or send a
pull request.

## Done in 0.2.0

- Windows caught up with macOS: a new interface, meeting offers, Scratchpad, Insights, text before the cursor,
  auto-learning and `@file` tags.
- Each side of a meeting is transcribed separately, and Telugu, Hindi and mixed speech got a checked language
  pipeline.
- Real screenshots in the README and on the landing page.
- A notice when a new release is out.

## Next

- Real-world testing on Windows PCs, and a manual pass of the rebuilt Mac interface on real machines.
- Accuracy figures for Telugu, Hindi and mixed speech from real speakers, in place of generated voices.
- A screen recording of a real dictation for the README.
- Automatic updates using a signed update feed, because downloading each release by hand means fixes reach
  people slowly.
- Stronger safety checks: comparing the exact text field before typing, and asking for confirmation when
  Command Mode would replace more than half of a field.
- Calendar titles and Export my data on Windows.

## Later

- Moving the prompts into shared files that both apps embed, in place of the parity check.
- Linux, which is not started. The design notes are in [docs/CROSS-PLATFORM.md](docs/CROSS-PLATFORM.md).
- Windows code signing through a free program for open-source projects, so SmartScreen warns less often.

## Decisions that are settled

- VoiceFlow stays free and open source under the MIT license.
- You bring your own Groq key. There are no accounts, servers or analytics.
- Distribution is through GitHub. The project has no Apple Developer account and is not on the App Store, so
  macOS builds are not notarized. [docs/RELEASING.md](docs/RELEASING.md) has the reasoning.
