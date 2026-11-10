# Sources for comparison statements

Every statement about another product in this folder comes from this table. All pages were read on
2026-11-10, and Wispr Flow, Superwhisper, VoiceInk and Groq were re-read live that day for the README. Prices
change, so read them again on the day you post, and drop any statement you can't re-check.

| Product | Fact | Source |
|---|---|---|
| Wispr Flow | Free plan with a weekly word limit. Pro is $15 per user per month, or $12 billed annually. Dictation on Mac, Windows, iOS and Android. | [wisprflow.ai/pricing](https://wisprflow.ai/pricing) |
| Superwhisper | Free tier, and Pro at $8.49 a month on the Monthly tab. Works offline. macOS, Windows, iOS and Android. | [superwhisper.com](https://superwhisper.com/) |
| VoiceInk | Source on GitHub, transcribes on your device. One-time $25, $39 or $49 for one, two or three Macs. Apple silicon and macOS 14.4 or later. | [tryvoiceink.com](https://tryvoiceink.com/), [source](https://github.com/Beingpax/VoiceInk) |
| Handy | Free and open source (MIT, per the GitHub API), audio stays on your computer. Cross-platform. | [handy.computer](https://handy.computer/), [source](https://github.com/cjpais/Handy) |
| Groq | `whisper-large-v3-turbo` is listed at 20 requests a minute, 2,000 a day, and 7,200 audio seconds an hour. The page calls these Developer plan base limits, and a free key showed the same hourly audio and daily request figures on 2026-10-17. Your own limits are at console.groq.com/settings/limits. | [console.groq.com/docs/rate-limits](https://console.groq.com/docs/rate-limits) |

| Groq | The Services Agreement, section 4.2 (effective 2026-06-22): "Groq is not permitted to use Inputs or Outputs for training or fine-tuning any AI Model Services or other models, unless explicitly granted permission or instructed by Customer." | [Services Agreement](https://console.groq.com/docs/legal/services-agreement) |
| Groq | Inference requests aren't retained by default. Groq may log inputs and outputs for up to 30 days when troubleshooting failures or investigating suspected abuse. Zero Data Retention turns that off. Usage metadata is always kept and has no inputs or outputs. | [Your Data in GroqCloud](https://console.groq.com/docs/your-data) |

Notes on tone: name a competitor only when the comparison helps the reader decide. If someone wants their
audio to stay on their own machine, tell them to use Handy or VoiceInk, because VoiceFlow sends audio to Groq.
