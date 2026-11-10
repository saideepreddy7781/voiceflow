#!/usr/bin/env python3
"""Runs the language pipeline on clips with known text and reports word error rate, before and after.

    GROQ_API_KEY=... python3 scripts/eval_languages.py [--only te-office,hi-office] [--show-text] [--seed-check]

--seed-check compares a forced Telugu or Hindi request with and without its seeded prompt, and skips the rest.

"Before" is the old pipeline: auto-detect plus one forced language, no seeded prompt, and any result outside
the list replaced by the forced one. "After" is the current pipeline. The rule constants, word lists and
prompt seeds are read from the Swift sources, so this script can't drift from the app on those. The decision
logic below mirrors LanguageResolver.swift by hand: change both together.

Transcripts are printed only with --show-text. See docs/LANGUAGE-PIPELINE.md for how to add your own clips.
"""
import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from groq_common import transcribe_verbose  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SPEECH = ROOT / "VoiceFlow" / "Sources" / "VoiceFlowCore" / "Speech"
CLIPS = json.loads((ROOT / "fixtures" / "language_clips.json").read_text())["clips"]
PAUSE = 3.2  # seconds between requests: 20 per minute on the free tier


def swift_words(name: str) -> set[str]:
    src = (SPEECH / "LanguageEvidence.swift").read_text()
    return set(re.findall(r'"([^"]+)"', re.search(rf"{name}: Set<String> = \[(.*?)\]", src, re.S).group(1)))


def swift_number(file: str, name: str) -> float:
    return float(re.search(rf"{name}(?:: \w+)? = (-?[\d.]+)", (SPEECH / file).read_text()).group(1))


def swift_seed(name: str) -> str:
    return re.search(rf'{name} = "([^"]+)"', (SPEECH / "WhisperPrompt.swift").read_text()).group(1)


COMMON = swift_words("commonEnglish")
HINDI = swift_words("hindiFunctionWords")
ENGLISH_PASS = swift_number("LanguageEvidence.swift", "englishPass")
HINDI_PASS = swift_number("LanguageEvidence.swift", "hindiPass")
MIN_ENGLISH = int(swift_number("LanguageEvidence.swift", "minimumEnglishWords"))
MIN_HINDI = int(swift_number("LanguageEvidence.swift", "minimumHindiWords"))
LOW = swift_number("LanguageResolver.swift", "lowConfidence")
FLOOR = swift_number("LanguageResolver.swift", "forcedFloor")
MARGIN = swift_number("LanguageResolver.swift", "margin")
MAX_OPINIONS = int(swift_number("LanguageResolver.swift", "maxOpinions"))
SEEDS = {"te": swift_seed("teluguMixedSeed"), "hi": swift_seed("hindiMixedSeed")}
INDIC = {"te", "hi", "ta", "kn", "ml", "mr", "bn", "gu", "pa", "ur", "ne", "as", "or", "si", "sa"}
NAMES = {"english": "en", "telugu": "te", "hindi": "hi", "tamil": "ta", "kannada": "kn", "malayalam": "ml",
         "marathi": "mr", "bengali": "bn", "gujarati": "gu", "punjabi": "pa", "urdu": "ur"}
RANGES = {"te": "ఀ-౿", "hi": "ऀ-ॿ", "ta": "஀-௿", "kn": "ಀ-೿", "ml": "ഀ-ൿ"}


def script_of(text: str) -> str:
    """The dominant script, as a language code for Indic scripts and 'latin' for English letters."""
    counts = {code: len(re.findall(f"[{r}]", text)) for code, r in RANGES.items()}
    counts["latin"] = len(re.findall(r"[A-Za-z]", text))
    best = max(counts, key=counts.get)
    return best if counts[best] else ""


def request(path: str, language: str | None, prompt: str | None) -> dict:
    data = transcribe_verbose(path, language=language, prompt=prompt)
    time.sleep(PAUSE)
    segments = data.get("segments") or []
    total = sum(max(0.01, s["end"] - s["start"]) for s in segments)
    confidence = sum(s["avg_logprob"] * max(0.01, s["end"] - s["start"]) for s in segments) / total if total else None
    name = (data.get("language") or "").lower()
    return {"text": (data.get("text") or "").strip(), "language": NAMES.get(name, name[:3] or None), "confidence": confidence}


def reads_as(language: str | None, text: str) -> bool | None:
    if language == "en" and script_of(text) == "latin":
        words = re.findall(r"[a-z']+", text.lower())
        if len(words) < MIN_ENGLISH:
            return None
        common = sum(w in COMMON for w in words) / len(words)
        vowel = sum(w[-1] in "aiou" for w in words) / len(words)
        return common - 0.5 * vowel >= ENGLISH_PASS
    if language == "hi" and script_of(text) == "hi":
        words = re.split(r"[\s।॥,.!?;:\"'()-]+", text.strip())
        words = [w for w in words if w]
        if len(words) < MIN_HINDI:
            return None
        return sum(w in HINDI for w in words) / len(words) >= HINDI_PASS
    return None


def allowed_script(script: str, languages: list[str]) -> bool:
    return script == "" or any(script == (lang if lang in RANGES else "latin") for lang in languages)


def doubt(auto: dict, languages: list[str], open_world: bool):
    if not auto["text"]:
        return None
    label, confidence = auto["language"], auto["confidence"] or 0
    others = [l for l in languages if l != "en"]
    if not allowed_script(script_of(auto["text"]), languages) or label not in languages:
        if label is None:
            return None if open_world else ("unlisted", None)
        real = reads_as(label, auto["text"])
        if open_world:
            if real is True:
                return None
            if not any(l in INDIC and label in INDIC for l in languages) and confidence >= LOW:
                return None
        return ("unlisted", real)
    if reads_as(label, auto["text"]) is False and any(o != label for o in others):
        return ("disguised", None)
    return ("low", None) if confidence < LOW else None


def choose(auto: dict, opinions: list[dict], languages: list[str], open_world: bool) -> dict:
    viable = [o for o in opinions if o["text"] and reads_as(o["language"], o["text"]) is not False]
    best = max(viable, key=lambda o: o["confidence"] if o["confidence"] is not None else -10, default=None)
    if not auto["text"]:
        return best or auto
    if best is None:
        return auto
    a = auto["confidence"] or 0
    f = best["confidence"] if best["confidence"] is not None else FLOOR
    if f < FLOOR:
        return auto
    d = doubt(auto, languages, open_world)
    if d is None:
        if auto["language"] == best["language"]:
            return best if f >= a else auto
        return best if f > a + MARGIN else auto
    if d[0] == "disguised":
        return best if f >= a - 0.5 else auto
    if d[0] == "unlisted":
        tolerance = 0 if d[1] is True else (0.12 if open_world else 0.25)
        return best if f >= a - tolerance else auto
    return best if f > a + MARGIN else auto


def after(path: str, clip: dict) -> tuple[dict, int]:
    languages, open_world = clip["languages"], clip["speaker"] == "anyone"
    if len(languages) == 1 and not open_world:
        return request(path, languages[0], SEEDS.get(languages[0])), 1
    auto = request(path, None, None)
    if doubt(auto, languages, open_world) is None:
        return auto, 1
    candidates = [l for l in languages if l != "en" and (l != auto["language"] or script_of(auto["text"]) == "latin")]
    ranked = sorted(candidates, key=lambda l: (not (auto["language"] in INDIC and l in INDIC), candidates.index(l)))[:MAX_OPINIONS]
    if not ranked:
        return auto, 1
    opinions = [dict(request(path, l, SEEDS.get(l)), language=l) for l in ranked]
    return choose(auto, opinions, languages, open_world), 1 + len(ranked)


def before(path: str, clip: dict) -> tuple[dict, int]:
    languages = clip["languages"]
    if len(languages) == 1:
        return request(path, languages[0], None), 1
    parallel = next((l for l in languages if l != "en"), languages[0])
    auto = request(path, None, None)
    forced = request(path, parallel, None)
    script_ok = allowed_script(script_of(auto["text"]), languages)
    if not script_ok or auto["language"] not in languages:
        return forced, 2
    if auto["language"] == parallel or auto["confidence"] is None or forced["confidence"] is None:
        return auto, 2
    return (forced if forced["confidence"] > auto["confidence"] + 0.1 else auto), 2


def words(text: str) -> list[str]:
    # Split on spaces and punctuation only: Python's \\w treats Indic vowel signs as non-word characters.
    return [w for w in re.split(r"[\s,.!?;:।॥\"'()\-]+", text.lower()) if w]


def wer(reference: str, hypothesis: str) -> float:
    r, h = words(reference), words(hypothesis)
    if not r:
        return 0.0 if not h else 1.0
    d = list(range(len(h) + 1))
    for i, rw in enumerate(r, 1):
        prev, d[0] = d[0], i
        for j, hw in enumerate(h, 1):
            prev, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, prev + (rw != hw))
    return d[len(h)] / len(r)


def score(clip: dict, text: str) -> float:
    return min(wer(ref, text) for ref in [clip["reference"], *clip.get("alternates", [])])


def clip_path(clip: dict, workdir: Path) -> Path | None:
    if clip["source"] == "own":
        path = ROOT / "fixtures" / "clips" / "lang" / f"{clip['id']}.wav"
        return path if path.exists() else None
    aiff, wav = workdir / f"{clip['id']}.aiff", workdir / f"{clip['id']}.wav"
    subprocess.run(["say", "-v", clip["voice"], "-o", str(aiff), clip["spoken"]], check=True)
    subprocess.run(["afconvert", "-f", "WAVE", "-d", "LEI16@16000", "-c", "1", str(aiff), str(wav)], check=True)
    return wav


def seed_check(clips: list[dict], workdir: Path, show: bool) -> int:
    totals: dict[str, list[float]] = {}
    for clip in clips:
        seed = SEEDS.get(clip["language"])
        path = clip_path(clip, workdir) if seed else None
        if path is None:
            continue
        plain = request(str(path), clip["language"], None)
        seeded = request(str(path), clip["language"], seed)
        a, b = score(clip, plain["text"]), score(clip, seeded["text"])
        totals.setdefault(clip["language"], []).extend([a, b])
        print(f"  {clip['id']:<20} no seed {a:5.0%} ({plain['confidence']:.3f})   seeded {b:5.0%} ({seeded['confidence']:.3f})")
        if show:
            print(f"      no seed: {plain['text']}\n      seeded:  {seeded['text']}")
    for language, values in sorted(totals.items()):
        olds, news = values[::2], values[1::2]
        print(f"{language}: {len(olds)} clips, mean WER no seed {sum(olds)/len(olds):.0%}, seeded {sum(news)/len(news):.0%}")
    return 0


def main() -> int:
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
    show = "--show-text" in sys.argv
    totals: dict[str, list[float]] = {}
    requests = [0, 0]
    with tempfile.TemporaryDirectory() as tmp:
        if "--seed-check" in sys.argv:
            return seed_check([c for c in CLIPS if not only or c["id"] in only], Path(tmp), show)
        for clip in CLIPS:
            if only and clip["id"] not in only:
                continue
            path = clip_path(clip, Path(tmp))
            if path is None:
                print(f"  - {clip['id']:<20} skipped (no recording yet)")
                continue
            old, n_old = before(str(path), clip)
            new, n_new = after(str(path), clip)
            requests[0] += n_old
            requests[1] += n_new
            a, b = score(clip, old["text"]), score(clip, new["text"])
            totals.setdefault(clip["language"], []).extend([a, b])
            print(f"  {clip['id']:<20} before {a:5.0%} [{old['language']}]   after {b:5.0%} [{new['language']}]")
            if show:
                print(f"      before: {old['text']}\n      after:  {new['text']}")
    print()
    for language, values in sorted(totals.items()):
        olds, news = values[::2], values[1::2]
        print(f"{language}: {len(olds)} clips, mean WER before {sum(olds)/len(olds):.0%}, after {sum(news)/len(news):.0%}")
    print(f"Requests: before {requests[0]}, after {requests[1]}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
