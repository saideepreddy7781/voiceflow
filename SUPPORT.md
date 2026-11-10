# Getting help

Search [existing issues](https://github.com/saideepreddy7781/voiceflow/issues) first, since someone may
have hit the same problem. VoiceFlow is maintained in spare time, so replies can take a few days.

## Reporting a problem

Open an [issue](https://github.com/saideepreddy7781/voiceflow/issues/new/choose) and use the form that fits.
For a bug, click **Copy debug report** in Settings and paste it into the form. If your version doesn't have
that button yet, write your VoiceFlow version and your OS instead. The debug report never includes what you
dictated or typed.

For wrong words, wrong language or bad cleanup, use the language quality form. An example of what you said
and what came out helps much more than a description.

## Common questions

**macOS says the app can't be opened.** VoiceFlow isn't notarized, because that needs a paid Apple account.
Open the app once, then go to System Settings > Privacy & Security and click Open Anyway. Apple describes
this at [support.apple.com/102445](https://support.apple.com/en-us/102445).

**macOS asks for Microphone or Accessibility again after an update.** Ad hoc signed builds do this, because
macOS sees each one as a new app. Check the release notes to see how your build was signed. If the
permission is listed but dictation doesn't work, remove VoiceFlow from the list in System Settings and add it
again.

**Windows says "Windows protected your PC".** The executable isn't signed. Click More info, then Run anyway.

**VoiceFlow says my key doesn't work or I hit a limit.** Keys come from
[console.groq.com/keys](https://console.groq.com/keys) and Settings has a Test button. Rate limits are set by
Groq, and your account's limits are at [console.groq.com/settings/limits](https://console.groq.com/settings/limits).

## Security

If you found a vulnerability, don't open an issue. Follow [SECURITY.md](SECURITY.md).
