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

Telugu gets special handling. Whisper's auto-detect often hears Telugu as Hindi, Tamil or
English, so when you pick both Telugu and English it transcribes both ways at once and keeps the right one. You
can type it in Telugu script, mixed, or in English letters.

The tradeoffs, so you know before you try it:

- Audio goes to Groq with your key. There are no VoiceFlow servers, accounts or analytics, but it isn't
  on-device. If you want audio to stay on your machine, Handy and VoiceInk do that.
- It costs nothing to run for me because you bring a free Groq key. Groq's free limits decide how much you can
  dictate.
- There's no paid Apple account behind this, so the Mac build isn't notarized. You click Open Anyway in System
  Settings once per version. Windows shows SmartScreen.
- The Windows app is new. It passes automated checks on Windows runners and has had little use on real PCs.

I'd like to hear where it breaks, especially on Windows and for non-English speech.

[Source links for anything you add about other products: see comparison-sources.md.]
