#!/usr/bin/env python3
"""Checks that the Windows app sends the same prompts as the macOS app.

The cleanup prompt is the one that matters most (scripts/eval_cleanup.py grades it); the Command Mode and
meeting-notes prompts are checked too. Each prompt is read from the Swift source the same way
eval_cleanup.py does, and from the C# raw string literal, then compared character for character.

Exit code 0 when every prompt matches, 1 otherwise. Standard library only.
"""
import difflib
import re
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SWIFT = ROOT / "VoiceFlow" / "Sources" / "VoiceFlowCore"
CSHARP = ROOT / "windows" / "VoiceFlow.Core"

PAIRS = [
    ("cleanup", SWIFT / "AI" / "CleanupPrompt.swift", CSHARP / "AI" / "CleanupPrompt.cs"),
    ("command mode", SWIFT / "AI" / "CommandMode.swift", CSHARP / "AI" / "CommandMode.cs"),
    ("meeting notes", SWIFT / "Meetings" / "MeetingNotesWriter.swift", CSHARP / "Meetings" / "MeetingNotesWriter.cs"),
    ("meeting script", SWIFT / "AI" / "CleanupPrompt.swift", CSHARP / "AI" / "CleanupPrompt.cs", "meetingScript", "MeetingScriptText"),
]


def swift_prompt(path: Path, name: str = "system") -> str:
    src = path.read_text(encoding="utf-8").replace("\r\n", "\n")
    m = re.search(rf'static let {name} = """\n(.*?)\n\s*"""', src, re.S)
    if not m:
        sys.exit(f"Could not find the system prompt in {path}")
    return textwrap.dedent(m.group(1))


def csharp_prompt(path: Path, name: str = "SystemText") -> str:
    """Reads `const string SystemText = \"\"\" ... \"\"\";` with C# raw string literal rules: the whitespace
    before the closing quotes is removed from every line; whitespace-only lines become empty."""
    src = path.read_text(encoding="utf-8").replace("\r\n", "\n")
    m = re.search(rf'const string {name} = """\n(.*?)\n([ \t]*)""";', src, re.S)
    if not m:
        sys.exit(f"FAIL  Could not find {name} in {path}")
    indent = m.group(2)
    lines = []
    for line in m.group(1).split("\n"):
        if line.strip() == "":
            lines.append("")
        elif line.startswith(indent):
            lines.append(line[len(indent):])
        else:
            sys.exit(f"{path}: a raw string line is indented less than its closing quotes")
    return "\n".join(lines)


def main() -> int:
    failed = False
    for name, swift_path, cs_path, *names in PAIRS:
        swift_name, cs_name = names or ("system", "SystemText")
        a, b = swift_prompt(swift_path, swift_name), csharp_prompt(cs_path, cs_name)
        if a == b:
            print(f"ok    {name} prompt ({len(a)} characters)")
            continue
        failed = True
        print(f"FAIL  {name} prompt differs between {swift_path.relative_to(ROOT)} and {cs_path.relative_to(ROOT)}")
        for line in difflib.unified_diff(a.split("\n"), b.split("\n"), "swift", "csharp", lineterm="", n=1):
            print("      " + line)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
