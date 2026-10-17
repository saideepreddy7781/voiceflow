#!/usr/bin/env python3
"""Transcribes every recorded fixture clip with Groq and scores it.

    GROQ_API_KEY=... python3 scripts/eval_transcription.py [--model whisper-large-v3]

Word error rate ignores case, punctuation and filler words, so "Um, so I" and "So I" score the same.
Clips with wer_max null are printed but not scored (silence, Telugu).
"""
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from groq_common import transcribe  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SPEC = json.loads((ROOT / "fixtures" / "clips.json").read_text())["clips"]
FILLERS = {"um", "uh", "er", "ah", "hmm", "mm"}
NUMBER_WORDS = {"three": "3", "four": "4", "eight": "8", "ten": "10", "one": "1"}


def words(text: str) -> list[str]:
    text = text.lower().replace("%", " percent").replace("₹", " rupees ")
    text = re.sub(r"(\d)(am|pm)\b", r"\1 \2", text)  # "3pm" and "3 PM" are the same words
    tokens = re.findall(r"[\w:']+", text)
    return [NUMBER_WORDS.get(t, t) for t in tokens if t not in FILLERS]


def wer(ref: str, hyp: str) -> float:
    r, h = words(ref), words(hyp)
    if not r:
        return 0.0 if not h else 1.0
    d = list(range(len(h) + 1))
    for i, rw in enumerate(r, 1):
        prev, d[0] = d[0], i
        for j, hw in enumerate(h, 1):
            prev, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, prev + (rw != hw))
    return d[len(h)] / len(r)


def main() -> int:
    model = sys.argv[sys.argv.index("--model") + 1] if "--model" in sys.argv else "whisper-large-v3-turbo"
    failures, scored, times = 0, 0, []
    print(f"model: {model}\n")
    for clip in SPEC:
        path = ROOT / "fixtures" / "clips" / f"{clip['id']}.wav"
        if not path.exists():
            continue
        prompt = ", ".join(clip.get("names", [])) or None
        text, secs = transcribe(str(path), model=model, language=clip.get("language", "en"), prompt=prompt)
        times.append(secs)
        if clip["wer_max"] is None:
            print(f"  · {clip['id']:<18} {secs:4.2f}s  (not scored) → {text!r}")
            continue
        score = wer(clip["transcript"], text)
        ok = score <= clip["wer_max"]
        known = clip.get("known_issue")
        failures += not ok and not known
        scored += 1
        mark = "✓" if ok else ("~" if known else "✗")
        print(f"  {mark} {clip['id']:<18} {secs:4.2f}s  WER {score:4.0%} (max {clip['wer_max']:.0%})"
              + ("" if ok else f"\n      expected: {clip['transcript']}\n      got:      {text}"
                     + (f"\n      known issue: {known}" if known else "")))
    times.sort()
    print(f"\n{scored - failures}/{scored} scored clips pass or are known issues. Median request {times[len(times)//2]:.2f}s, "
          f"slowest {times[-1]:.2f}s.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
