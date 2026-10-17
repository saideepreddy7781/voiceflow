#!/usr/bin/env python3
"""Generates the 'generated' clips in fixtures/clips.json with macOS `say` and checks every clip.

    python3 scripts/make_fixtures.py          generate missing clips, then check
    python3 scripts/make_fixtures.py --force  regenerate all generated clips
    python3 scripts/make_fixtures.py --check  only check (format, presence)
"""
import json
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CLIPS = ROOT / "fixtures" / "clips"
SPEC = json.loads((ROOT / "fixtures" / "clips.json").read_text())["clips"]


def generate(clip: dict) -> None:
    out = CLIPS / f"{clip['id']}.wav"
    if clip.get("silence_seconds"):
        frames = int(16000 * clip["silence_seconds"])
        with wave.open(str(out), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
            w.writeframes(b"\x00\x00" * frames)
        return
    with tempfile.TemporaryDirectory() as tmp:
        aiff = Path(tmp) / "clip.aiff"
        subprocess.run(["say", "-v", clip["voice"], "-o", str(aiff), clip["spoken"]], check=True)
        subprocess.run(["afconvert", "-f", "WAVE", "-d", "LEI16@16000", "-c", "1", str(aiff), str(out)], check=True)


def check(clip: dict) -> str | None:
    path = CLIPS / f"{clip['id']}.wav"
    if not path.exists():
        return "missing" + (" (optional: a recording of your own voice)" if clip["source"] == "own" else "")
    with wave.open(str(path)) as w:
        if (w.getframerate(), w.getnchannels(), w.getsampwidth()) != (16000, 1, 2):
            return f"wrong format {w.getframerate()} Hz, {w.getnchannels()} ch, {8 * w.getsampwidth()} bit"
        secs = w.getnframes() / 16000
    return f"ok, {secs:.1f}s"


def main() -> int:
    CLIPS.mkdir(parents=True, exist_ok=True)
    force, check_only = "--force" in sys.argv, "--check" in sys.argv
    if not check_only:
        for clip in SPEC:
            if clip["source"] == "generated" and (force or not (CLIPS / f"{clip['id']}.wav").exists()):
                generate(clip)
    problems = 0
    for clip in SPEC:
        status = check(clip)
        bad = not status.startswith("ok")
        problems += bad and clip["source"] == "generated"
        print(f"  {'✗' if bad else '✓'} {clip['id']:<18} {clip['source']:<9} {status}")
    own_missing = sum(1 for c in SPEC if c["source"] == "own" and not (CLIPS / f"{c['id']}.wav").exists())
    print(f"\n{len(SPEC)} clips defined; {own_missing} of your own-voice clips still to record.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
