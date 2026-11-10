# Awesome list targets

Checked on 2026-11-10 with the GitHub API and each list's contribution rules. VoiceFlow had 4 stars and its
first public commit was on 2026-10-17, so the star and age rules below matter. Read the rules again before
opening a pull request, one pull request per list.

| List | Stars | Status for VoiceFlow today | What the rules ask |
|---|---|---|---|
| [jaywcjlove/awesome-mac](https://github.com/jaywcjlove/awesome-mac) | 118,191 | Can apply now | [Rules](https://github.com/jaywcjlove/awesome-mac/blob/master/docs/CONTRIBUTING.md): one pull request per item, alphabetical in the category, title case for the name. It has a Voice-to-Text section, and entries are kept in the four README languages. |
| [serhii-londar/open-source-mac-os-apps](https://github.com/serhii-londar/open-source-mac-os-apps) | 51,304 | Can apply now | [Rules](https://github.com/serhii-londar/open-source-mac-os-apps/blob/master/CONTRIBUTING.md): edit `applications.json`, not the README, with one pull request per app. Category must come from `categories.json`. Descriptions end with a full stop. |
| [primaprashant/awesome-voice-typing](https://github.com/primaprashant/awesome-voice-typing) | 214 | Wait | [Rules](https://github.com/primaprashant/awesome-voice-typing/blob/main/CONTRIBUTING.md): roughly 50 stars and 3 months since the first public commit, called flexible guidelines, so about January 2027 without other evidence of users. Say you are the author. Mode is `Cloud`, Engine is `Whisper (Groq)`. |
| [0PandaDEV/awesome-windows](https://github.com/0PandaDEV/awesome-windows) | 3,142 | Wait for 50 stars | [Rules](https://github.com/0PandaDEV/awesome-windows/blob/main/CONTRIBUTING.md): at least 50 stars, and either 30 days old or a commit in the last 30 days. It rejects software that doesn't meet its bar. Wait until real Windows users have tried it. |
| [sindresorhus/awesome-whisper](https://github.com/sindresorhus/awesome-whisper) | 2,430 | Wait for 100 stars | [Rules](https://github.com/sindresorhus/awesome-whisper/blob/main/contributing.md): open-source GitHub projects need at least 100 stars and must link to the repository. Pull request title is `Add VoiceFlow`, added at the bottom of its category. No marketing taglines. |
| [iCHAIT/awesome-macOS](https://github.com/iCHAIT/awesome-macOS) | 19,469 | Likely ineligible | [Rules](https://github.com/iCHAIT/awesome-macOS/blob/master/.github/contributing.md): "No AI prompt wrappers or software whose main purpose is interfacing with generative tools". VoiceFlow's cleanup uses a hosted language model, so ask the maintainers before spending time on this one. |

## Entry text

For awesome-mac, under Voice-to-Text, using the list's own icon references:

```markdown
* [VoiceFlow](https://github.com/saideepreddy7781/voiceflow) - Hold a key, speak, and cleaned-up text appears in any app, using your own Groq key. [![Open-Source Software][OSS Icon]](https://github.com/saideepreddy7781/voiceflow) ![Freeware][Freeware Icon]
```

For open-source-mac-os-apps, `applications.json`:

```json
{
  "short_description": "Voice typing that puts cleaned-up text in any app, using your own Groq key.",
  "categories": ["Pick one from categories.json"],
  "repo_url": "https://github.com/saideepreddy7781/voiceflow",
  "title": "VoiceFlow",
  "icon_url": "https://raw.githubusercontent.com/saideepreddy7781/voiceflow/main/docs/assets/icon.png",
  "screenshots": [],
  "official_site": "https://saideepreddy7781.github.io/voiceflow/",
  "languages": ["Swift", "C#"]
}
```

For awesome-whisper, when it qualifies: `[VoiceFlow](https://github.com/saideepreddy7781/voiceflow) - Voice typing for macOS and Windows that sends speech to Whisper on Groq and types the cleaned-up text.`

For awesome-voice-typing, when it qualifies, follow the row format in its CONTRIBUTING.md. Summary: `Voice typing for macOS and Windows with cleanup through your own Groq key, sending audio to Groq.`
