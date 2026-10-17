#!/usr/bin/env python3
"""Runs fixtures/cleanup.json through the cleanup prompt on Groq and grades each case.

    GROQ_API_KEY=... python3 scripts/eval_cleanup.py [--model qwen/qwen3.8-27b] [--only names-1,inject-2]

The system prompt is read from VoiceFlow/Sources/VoiceFlowCore/AI/CleanupPrompt.swift, so the app and
this eval always use the same text.
"""
import json
import re
import sys
import textwrap
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from groq_common import chat  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
PROMPT_FILE = ROOT / "VoiceFlow" / "Sources" / "VoiceFlowCore" / "AI" / "CleanupPrompt.swift"
CASES = json.loads((ROOT / "fixtures" / "cleanup.json").read_text())["cases"]


def load_system_prompt() -> str:
    src = PROMPT_FILE.read_text()
    m = re.search(r'static let system = """\n(.*?)\n\s*"""', src, re.S)
    if not m:
        sys.exit(f"Could not find the system prompt in {PROMPT_FILE}")
    return textwrap.dedent(m.group(1))


def user_message(case: dict) -> str:
    # Mirrors CleanupPrompt.userMessage in Swift.
    parts = []
    if case.get("app"):
        parts.append(f"<app>{case['app']}</app>")
    parts.append(f"<style>{case['style']}</style>")
    if case.get("cleanup", "light") != "light":
        parts.append(f"<cleanup>{case['cleanup']}</cleanup>")
    if case.get("telugu"):
        parts.append(f"<telugu>{case['telugu']}</telugu>")
    if case.get("names"):
        parts.append(f"<names>{', '.join(case['names'])}</names>")
    if case.get("before_cursor", "").strip():
        parts.append(f"<before_cursor>{case['before_cursor'][-120:]}</before_cursor>")
    if case.get("files"):
        parts.append(f"<files>{', '.join(case['files'])}</files>")
        if case.get("tag_files"):
            parts.append("<tag_files>yes</tag_files>")
    parts.append(f"<transcript>{case['transcript']}</transcript>")
    return "\n".join(parts)


def grade(case: dict, out: str) -> list[str]:
    problems = []
    low = out.lower()
    if re.match(r"(here|sure|cleaned|output|the cleaned)\b", low):
        problems.append("starts with a preamble")
    if len(out) > 1 and out[0] in "\"'“" and out[-1] in "\"'”":
        problems.append("wrapped in quotes")
    if "<" in out and re.search(r"</?(transcript|style|app|names)>", out):
        problems.append("contains prompt tags")
    for s in case.get("includes", []):
        if s.lower() not in low:
            problems.append(f"missing {s!r}")
    for s in case.get("includes_exact", []):
        if s not in out:
            problems.append(f"missing exact {s!r}")
    for group in case.get("includes_any", []):
        if not any(s.lower() in low for s in group):
            problems.append(f"missing one of {group}")
    for s in case.get("excludes", []):
        if re.search(rf"(?<!\w){re.escape(s.lower())}(?!\w)", low):
            problems.append(f"should not contain {s!r}")
    if case.get("no_script") == "telugu" and re.search(r"[\u0c00-\u0c7f]", out):
        problems.append("still contains Telugu script")
    word_count = len(re.findall(r"\w+", out))
    if "max_words" in case and word_count > case["max_words"]:
        problems.append(f"too long ({word_count} words > {case['max_words']}): added content?")
    return problems


def main() -> int:
    model = sys.argv[sys.argv.index("--model") + 1] if "--model" in sys.argv else "qwen/qwen3.8-27b"
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
    system = load_system_prompt()
    print(f"model: {model}   prompt: {len(system.split())} words\n")
    cases = [c for c in CASES if not only or c["id"] in only]
    failed, times, by_rule = 0, [], {}
    for case in cases:
        out, secs = chat(model, system, user_message(case))
        times.append(secs)
        problems = grade(case, out)
        failed += bool(problems)
        by_rule.setdefault(case["rule"], [0, 0])[0 if problems else 1] += 1
        shown = out.replace("\n", "⏎")
        print(f"  {'✗' if problems else '✓'} {case['id']:<20} {secs:4.2f}s  {shown[:90]}")
        for p in problems:
            print(f"      - {p}")
    times.sort()
    print(f"\n{len(cases) - failed}/{len(cases)} pass. Median {times[len(times)//2]:.2f}s.")
    print("By rule (pass/fail): " + ", ".join(f"{r}: {v[1]}/{v[0]}" for r, v in sorted(by_rule.items())))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
