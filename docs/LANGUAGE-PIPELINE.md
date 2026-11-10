# Language and meeting pipeline

How VoiceFlow turns speech in English, Telugu, Hindi and mixed languages into text on Groq's free tier. This is
the reference for porting: every constant here is in the Swift source named beside it, and the Windows app must
match them. Measurements are dated. "Meetings" below means 14 real meetings used to tune the rules, counted in
aggregate only.

## Languages

`Preferences.languages` is the list of languages you speak, any length. `LanguagePlan` adds who is speaking:
`user` for dictation and your microphone in a meeting, `anyone` for the call, where other people can speak
anything. With one language and `user`, that language is forced. Otherwise Whisper auto-detects and the result is
checked.

`LanguageResolver.doubt` decides whether to trust auto-detect, in this order:

1. The script or language is outside the list. On the call this is accepted when the text reads as its own
   language, or when the language can't be confused with a listed one and confidence is at least -0.45. Hindi on a
   Telugu speaker's call stays Hindi, and Spanish stays Spanish. Everywhere else it is `unlisted`.
2. The text doesn't read as its label, and another listed language exists: `disguised`. This is Telugu that Whisper
   spelled by sound in English letters and labelled English, or Telugu written in Devanagari and labelled Hindi.
3. Confidence below -0.45: `lowConfidence`.

Confusable languages are the Indic set `te hi ta kn ml mr bn gu pa ur ne as or si sa`.

A doubt triggers forced requests for up to 2 listed non-English languages (`maxOpinions`), languages confusable
with the detected one first. `choose` then compares them. A forced result is thrown out if it is empty, looping, or
fails the reading check for its own language. The best remaining one (confidence at least -0.8) replaces auto-detect
under these rules, where `a` is auto's average log-probability and `f` the forced one's:

| Situation | Forced result wins when |
|---|---|
| No doubt, same language as auto (the forced request had the seed) | `f >= a` |
| No doubt, different language | `f > a + 0.1` |
| `disguised` | `f >= a - 0.5` |
| `unlisted`, text reads as real language | `f >= a` |
| `unlisted`, otherwise, on the call | `f >= a - 0.12` |
| `unlisted`, otherwise, your own speech | `f >= a - 0.25` |
| `lowConfidence` | `f > a + 0.1` |

Confidence alone can't separate these cases. Telugu heard as Hindi scored -0.071 against -0.101 for correct forced
Telugu, measured 2026-11-10 on a generated clip.

## Reading checks

`LanguageEvidence` has two pure functions, both returning nil when there is too little text to judge.

English needs 8 words. Tokens are runs of ASCII letters and apostrophes, lowercased. The score is the share of tokens
in `commonEnglish` (about 410 words) minus half the share ending in a, i, o or u. It passes at 0.46. On the meetings,
in 35-second windows with enough words, 4.9% of windows from English meetings failed (14 of 286) and 46% of windows
from meetings containing Telugu failed (63 of 138). Those meetings also have English stretches, which should pass. A
false alarm costs one extra upload and rarely changes the text, because forced Telugu on English speech scores far
worse.

Hindi needs 6 Devanagari words and passes when 18% are in `hindiFunctionWords`. "की" and "तो" are left out because
Telugu heard as Hindi produces them too.

## Prompts for Whisper

`WhisperPrompts` builds the `prompt` field. Terms from your dictionary come first and a seed sentence last, because
Whisper keeps the end of a long prompt. Groq rejects prompts over 896 characters. The builder also keeps the estimate
(ASCII / 3.5 + other x 1.5) under 200 tokens by dropping terms.

Seeds go only on requests that force Telugu or Hindi. The seed follows your writing choice: a script seed for
Telugu script or Devanagari, a code-mixed seed for the others. Auto-detect requests carry dictionary terms only: with
a Telugu seed, clear English speech came back as Telugu gibberish.

Forced requests with and without the seed, on 8 generated clips, 2026-11-10: mean word error rate went from 34% to
13% for Telugu and from 19% to 8% for Hindi.

The whole pipeline against the old one on 11 generated clips (`scripts/eval_languages.py`), 2026-11-10: mean word error
rate for Telugu went from 54% to 13% and for Hindi from 60% to 19%. English stayed at 10%. Requests fell from 22 to 16.
These clips are macOS synthetic voices, so real speech will score differently.

## Loops and invented text

`WhisperSegmentFilter` runs on Whisper's verbose output. A segment is dropped when its `compression_ratio` is above
2.4 and it has at least 20 characters, or when `no_speech_prob` is above 0.6 and `avg_logprob` below -1.0. In a run of
identical segments, the third and later are dropped. A transcript with such a run can't win `choose`.

`TranscriptFilter.collapseRepeats` works on finished lines. For each speaker, a run of 3 or more identical lines (letters
and digits only, lowercased) with gaps of at most 20 s keeps its first line. Lost-audio markers are never collapsed.
It runs on every merge and before writing notes, so older meetings get better notes without their saved text changing.

Applied to the meetings: the worst meeting loses 119 of 270 lines. The two cleanest lose none, and the largest (603
lines) loses 8. Nearly all removed lines are one or two words.

## Two sides of a meeting

The microphone and the call are transcribed separately, so You and Others are real labels. The sides are aligned by
padding whichever one stalls by more than 2 seconds.

`StreamChunker` keeps voiced audio only. It works in 30 ms frames. `VoiceGate` calls a frame voiced when it is above
a threshold: the 10th percentile of the last 15 s of frame levels plus 12 dB, kept between -45 and -35 dBFS, refreshed
each second. Each region keeps 210 ms before and 300 ms after the speech. Removed silences become a 0.25 s gap in the
upload.

A chunk is cut at a pause of 0.6 s once it holds 20 s of speech, forced at 45 s of speech, and cut at a pause after
120 s so a quiet side still appears. Under 0.4 s of speech is not sent.

`TimeMap` converts positions in the upload back to meeting time. Requests ask for word timestamps. A Whisper segment
that spans a removed silence of at least 2 s is split at that point using the word times.

`TranscriptMerge.merge` sorts both sides by time, then removes echoes and collapses repeats. A mic line is an echo when
most of its words were said on the call around the same time. Lines under 3 words are never echoes. Lines of 3 to 5
words need 90% of their words within 2 s. Lines of 6 or more need 60% within 4 s. The call's line is the one kept.

## Audio budget

Groq counts each upload as at least 10 s. A side's cost is its speech plus that minimum, so a two-person call without
much overlap costs about the length of the meeting.

`StreamLanguageTracker` keeps Telugu and Hindi meetings near that figure. After 2 chunks in a row that needed a forced
language, the next chunks force it directly, and every 5th chunk runs auto-detect again. A forced result below -0.2
falls back to the full check, so someone who switches to Hindi on a sticky Telugu call is not written as Telugu. With under 900 s of hourly budget left, meetings skip second opinions.

Playing a call through speakers means the microphone uploads the call as well, so that case costs more. Echoes are
removed from the text after transcription.

## One queue

`GroqGate` is one actor that every Groq request passes through, and `RateBudget` is its arithmetic, per model.

Defaults: Whisper 20 requests/min, 2,000/day, 7,200 audio s/hour, 28,800/day. Chat 30 requests/min, 1,000/day, 8,000
tokens/min, 200,000/day. Response headers `x-ratelimit-limit-requests`, `x-ratelimit-limit-tokens`,
`x-ratelimit-remaining-requests` and `x-ratelimit-reset-requests` replace these where present. Groq sent no audio
headers on 2026-11-10, so audio is counted locally, and that count resets when the app restarts.

Background work (meeting chunks, rewriting, notes) leaves room for dictation: 4 requests/min, 150 requests/day, 600
audio s/hour, 1,800 audio s/day and 1,500 tokens/min. It runs in queue order, and gives up after waiting 90 s so the
caller can hold the chunk. A 429 blocks background work for its `Retry-After`. Interactive requests skip the line and
wait at most 2 s, then go out anyway.

A 429 body naming a per-day limit (TPD, RPD, ASPD) becomes `ProviderError.dailyLimit`.

## Failed chunks

Each chunk gets 2 tries during the meeting. After that it is held in memory as 16-bit samples, oldest evicted past
1,200 s of audio, and never written to disk. At Stop each held chunk gets 3 more tries, for at most 180 s in total. The
marker `[Part of the meeting could not be transcribed]` appears only for what is still lost, and `Meeting.note` says
how much and why. Quitting the app skips the retries.

## Writing choices

Telugu has `telugu`, `mixed`, `roman`. Hindi has `devanagari`, `mixed`, `hinglish`. Dictation sends them in `<telugu>`
and `<hindi>` tags to `CleanupPrompt.system`.

Meeting transcripts use `CleanupPrompt.meetingScript` through `TranscriptScriptWriter`, after Stop and before notes. Only
lines that need it are sent, in batches of about 1,500 estimated tokens, as a JSON object with sorted keys. A reply is
rejected when the line count differs, a line is empty, or a line has more than 1.6 x its words + 2. Rejected batches
stay as heard. Only languages in your list are rewritten, and older meetings are left alone.

## Notes

The notes prompt forbids vague title words. `NotesTitle` catches any that get through and falls back to the start of
the first summary point, then to the existing title. With "Same as the meeting", Telugu and Hindi notes follow your
writing choice. Notes use `commandModel`. If that model's daily limit runs out they use `qwen/qwen3.8-27b`, since Groq
counts models separately. If both run out, the meeting shows the reason and a Try again button, and the transcript is
kept.

## Adding your own clips

`scripts/eval_languages.py` reads `fixtures/language_clips.json`. To add a clip:

1. Record 16 kHz mono WAV and save it as `fixtures/clips/lang/<id>.wav` (git ignores this folder).
2. Add an entry with `"source": "own"`, `id`, `language`, `languages` (the Settings list to test with), `speaker`
   (`user` or `anyone`) and `reference`, the words as said. Use `alternates` for other acceptable spellings, for example
   `office` and `ఆఫీస్`. The best match is scored.
3. Run `GROQ_API_KEY=... python3 scripts/eval_languages.py`. Add `--seed-check` to test the seeds alone, and `--show-text`
   to see transcripts.

The script reads constants, word lists and seeds from the Swift sources. Its decision logic is a hand copy of
`LanguageResolver`, so change both together.
