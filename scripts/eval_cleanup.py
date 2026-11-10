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

SCRIPT_RANGES = {"telugu": r"[\u0c00-\u0c7f]", "devanagari": r"[\u0900-\u097f]"}
ROOT = Path(__file__).resolve().parent.parent
PROMPT_FILE = ROOT / "VoiceFlow" / "Sources" / "VoiceFlowCore" / "AI" / "CleanupPrompt.swift"
CASES = json.loads((ROOT / "fixtures" / "cleanup.json").read_text())["cases"]


def load_system_prompt(name: str = "system") -> str:
    src = PROMPT_FILE.read_text()
    m = re.search(rf'static let {name} = """\n(.*?)\n\s*"""', src, re.S)
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
    if case.get("hindi"):
        parts.append(f"<hindi>{case['hindi']}</hindi>")
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


def script_user_message(case: dict) -> str:
    # Mirrors TranscriptScriptWriter.userMessage in Swift.
    return json.dumps({"hindi": case.get("hindi"), "lines": case["lines"], "telugu": case.get("telugu")},
                      ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def grade_script(case: dict, out: str) -> list[str]:
    try:
        lines = json.loads(out)["lines"]
    except (ValueError, KeyError, TypeError):
        return ["reply is not the JSON object the app expects"]
    if len(lines) != len(case["lines"]):
        return [f"{len(lines)} lines for {len(case['lines'])}"]
    problems = []
    for i, line in enumerate(lines):
        low = line.lower()
        for s in (case.get("line_includes") or [[]] * len(lines))[i]:
            if s.lower() not in low:
                problems.append(f"line {i} missing {s!r}")
        for s in (case.get("line_excludes") or [[]] * len(lines))[i]:
            if s.lower() in low:
                problems.append(f"line {i} should not contain {s!r}")
        for group in (case.get("line_includes_any") or [[]] * len(lines))[i]:
            if not any(s.lower() in low for s in group):
                problems.append(f"line {i} missing one of {group}")
        if "max_growth" in case and len(line.split()) > len(case["lines"][i].split()) * case["max_growth"] + 1:
            problems.append(f"line {i} grew: added words?")
        if case.get("no_script") and re.search(SCRIPT_RANGES[case["no_script"]], line):
            problems.append(f"line {i} still contains {case['no_script']} script")
    return problems


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
    if case.get("no_script") and re.search(SCRIPT_RANGES[case["no_script"]], out):
        problems.append(f"still contains {case['no_script']} script")
    # \\w splits Indic words at their vowel signs, so those are counted by spaces.
    word_count = len(out.split()) if re.search(r"[\u0900-\u0d7f]", out) else len(re.findall(r"\w+", out))
    if "max_words" in case and word_count > case["max_words"]:
        problems.append(f"too long ({word_count} words > {case['max_words']}): added content?")
    return problems


def main() -> int:
    model = sys.argv[sys.argv.index("--model") + 1] if "--model" in sys.argv else "qwen/qwen3.8-27b"
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
    system = load_system_prompt()
    script_system = load_system_prompt("meetingScript")
    print(f"model: {model}   prompt: {len(system.split())} words\n")
    cases = [c for c in CASES if not only or c["id"] in only]
    failed, times, by_rule = 0, [], {}
    for case in cases:
        if case.get("kind") == "script":
            out, secs = chat(model, script_system, script_user_message(case), json_mode=True)
            problems = grade_script(case, out)
        else:
            out, secs = chat(model, system, user_message(case))
            problems = grade(case, out)
        times.append(secs)
        failed += bool(problems)
        by_rule.setdefault(str(case["rule"]), [0, 0])[0 if problems else 1] += 1
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
