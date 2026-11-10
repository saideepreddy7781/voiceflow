## What this changes

<!-- One or two sentences. Link the issue if there is one. -->

## How you tested it

<!-- Which test suites you ran, and what you checked by hand and on which machine.
     Real microphones, global keys and Accessibility can't be tested automatically, so say what you did. -->

## Checklist

- [ ] Tests pass on the platforms I changed (`swift test --package-path VoiceFlow`, `dotnet test windows/VoiceFlow.Core.Tests`).
- [ ] `python3 scripts/check_prompt_parity.py` passes. If I changed a prompt, the eval scores before and after are above.
- [ ] Settings and saved data still decode (new fields have defaults).
- [ ] Nothing logs or records what the user said or typed.
