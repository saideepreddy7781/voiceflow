# Reddit drafts

Read each subreddit's current rules in its sidebar before posting. Rules on self-promotion change, and some
subreddits limit which days or which accounts can post apps. Post in one subreddit per day, say plainly that
you built it, and reply to comments yourself.

## r/opensource

**Title:** I made VoiceFlow, an MIT-licensed voice typing app for macOS and Windows

I've been building VoiceFlow, a voice typing app for macOS and Windows under the MIT license. Hold a key,
speak, let go, and cleaned-up text appears in whatever app you're in.

Both apps are native, Swift on the Mac and C# on Windows, and CI checks that they use the same prompts. It has
no accounts, servers or analytics. It uses your own free Groq API key, so your audio goes to Groq and nowhere
else. The privacy page in the repo lists exactly what is sent.

Contributions I'd especially like: language quality reports (there's an issue form for it), testing on real
Windows PCs, and tests. CONTRIBUTING.md covers building both apps.

https://github.com/saideepreddy7781/voiceflow

## r/macapps

**Title:** VoiceFlow: free, open-source voice typing for Mac, using your own Groq key (I'm the developer)

I built VoiceFlow. Hold Fn, speak, let go, and cleaned-up text appears in any app. It removes filler words,
applies corrections you say out loud, and has a Command Mode for rewriting selected text. It needs macOS 14 or
later and runs on Apple silicon and Intel.

Things to know before you install:

- It's free and MIT licensed, and needs a free key from Groq. Audio is sent to Groq, so it isn't on-device. If
  you want on-device, look at VoiceInk or Handy.
- It isn't notarized, because I don't have a paid Apple developer account. You'll click Open Anyway in System
  Settings once per version.
- It installs from the DMG or with `brew tap saideepreddy7781/voiceflow https://github.com/saideepreddy7781/voiceflow`
  and `brew install --cask saideepreddy7781/voiceflow/voiceflow`.

Bug reports are welcome. https://github.com/saideepreddy7781/voiceflow

## r/SideProject

**Title:** I built a free voice typing app for Mac and Windows

[Two sentences in your own words about why you started it and how long it took.]

VoiceFlow lets you hold a key and speak, and cleaned-up text appears in any app. It's open source and free
because people bring their own Groq key, so there are no accounts or servers for me to run. It also handles
Telugu and Telugu-English, which Whisper's auto-detect often gets wrong.

The hardest parts were [one real thing you struggled with].

Here's the repo if you want to try it or read the code: https://github.com/saideepreddy7781/voiceflow
