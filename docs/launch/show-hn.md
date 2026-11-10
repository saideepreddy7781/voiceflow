# Show HN draft

Follow the [Show HN rules](https://news.ycombinator.com/showhn.html): people have to be able to try it, and
don't ask anyone to upvote. Post from your own account and stay around to answer questions for a few hours.

**Title**

Show HN: VoiceFlow – Free voice typing for macOS and Windows, with your own Groq key

**URL**

https://github.com/saideepreddy7781/voiceflow

**First comment**

I built VoiceFlow, an MIT-licensed app for voice typing on macOS and Windows. You hold a key, speak, let go,
and cleaned-up text appears where your cursor is, in any app.

[One or two sentences in your own words about why you built it.]

It records from the mic, cuts the audio at pauses, uploads segments to Whisper on Groq while you talk, and
sends the transcript through a small cleanup model that removes filler words and applies spoken corrections
like "Tuesday, no actually Wednesday". The macOS app is Swift and the Windows app is C#, with the prompts
checked in CI to be identical on both.

Telugu and Hindi get special handling. Whisper's auto-detect often hears Telugu as Hindi, Tamil or English, so
VoiceFlow checks each result against the languages you listed and asks Whisper again with the language forced
when the text doesn't read right. You can type Telugu or Hindi in its script, mixed with English words, or in
English letters. On 11 generated clips (macOS synthetic voices, 2026-11-10), mean word error rate went from 54%
to 13% for Telugu and from 60% to 19% for Hindi. I don't have figures for real speakers yet.

The tradeoffs, so you know before you try it:

- Audio goes to Groq with your key. There are no VoiceFlow servers, accounts or analytics, but it isn't
  on-device. If you want audio to stay on your machine, Handy and VoiceInk do that.
- It costs nothing to run for me because you bring a free Groq key. Groq's free limits decide how much you can
  dictate.
- There's no paid Apple account behind this, so the Mac build isn't notarized. You click Open Anyway in System
  Settings once per version. Windows shows SmartScreen.
- The Windows app is new. It has passed automated checks on real Windows runners, and real-world testing on
  people's PCs is still early.

I'd like to hear where it breaks, especially on Windows and for non-English speech.

[Source links for anything you add about other products: see comparison-sources.md.]
