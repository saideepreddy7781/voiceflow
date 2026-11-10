#!/usr/bin/env python3
"""Fails if tracked files mention private working notes or personal paths.

The list is stored as SHA-256 digests so this file doesn't put the strings it looks for in the repository.
Every file path and every text file from `git ls-files` is split into words, and each word and each pair of
neighbouring words is compared with the digests. Case is ignored.

Exit code 0 when clean, 1 otherwise. Standard library only.
"""
import hashlib
import re
import subprocess
import sys
from pathlib import Path

DIGESTS = {
    "75ac768a4b729a9a642f70f86d4907ef3b34d17f6ce47a229352bcd0690f5129",
    "0bbe5bc4573bcf1caa8ba13b58efe7c468bb397ddfe2f5a83f9c76ba92cec5b4",
    "c4599aab5cbf87f8189071b86ce3c16a7dc83fd6a365466c4374939ef6b04812",
    "bcfa99fa8238618ad7f265f8602b592ce10395c282aad836cee58f1d3f824563",
    "a34966253ddd9a4b15111a32867c4112ba37b383547801b7bb2cf1772b0aceb0",
    "a1d01545e97c960af656f7cc158385be1df89e54b05b0d2423ac54ec02a2ee2d",
    "840e77b30994705b6ba7b16c440c9948758fdaf4a171d4d9c1867862d868f3e6",
    "dbdd60556bd5ca31c42c28e796782b332eaabc5290720bf779fae44230395fe2",
    "11108fa8a883da893ae6e502ffc0736ab7992607627c29fc941a8a97e4abfc21",
    "fce85944eccd1e662a27976645570350a266a1791bcec527467cfea313753d8d",
    "13560189843d8a2669005f48fa96f3bee6310f244b3de70fa09dd908f29257ed",
    "59e9fa4c889789f2240581619f67105168ae9e772a9e98e396659ede44343111",
    "1b41f854c9708ac1d9c31d7b060c0acb32d69fd56c67ae90867fc2c8b712aecd",
}

WORD = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]*")
HOME_PATH = re.compile(r"(?:/Users/|/home/|[A-Za-z]:\\+Users\\+)([A-Za-z0-9._-]+)")
PLACEHOLDER_USERS = {"you", "me", "name", "user", "username", "runner", "example", "shared", "public"}


def digest(text: str) -> str:
    return hashlib.sha256(text.lower().encode()).hexdigest()


def words(text: str) -> list[str]:
    return [m.group(0).rstrip("._-") for m in WORD.finditer(text)]


def findings(text: str) -> list[str]:
    found = []
    tokens = words(text)
    pairs = [f"{a} {b}" for a, b in zip(tokens, tokens[1:])]
    if any(digest(t) in DIGESTS for t in tokens + pairs):
        found.append("private term")
    for match in HOME_PATH.finditer(text):
        if match.group(1).lower() not in PLACEHOLDER_USERS:
            found.append(f"personal path {match.group(0)}")
    return found


def main() -> int:
    paths = subprocess.run(
        ["git", "ls-files", "-z"], check=True, capture_output=True
    ).stdout.decode().split("\0")
    problems = 0
    for name in filter(None, paths):
        hits = findings(name)
        path = Path(name)
        if path.is_file():
            data = path.read_bytes()
            if b"\0" not in data:
                hits += findings(data.decode("utf-8", errors="ignore"))
        for hit in sorted(set(hits)):
            print(f"{name}: {hit}")
            problems += 1
    if problems:
        print(f"{problems} private string(s) found in tracked files.")
        return 1
    print("No private strings in tracked files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
