"""Shared helpers for the evaluation scripts: Groq requests with rate-limit handling.

The key comes from the GROQ_API_KEY environment variable and is never printed.
"""
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = "https://api.groq.com/openai/v1"

# The python.org build of Python ships without the macOS certificate store.
_SSL = ssl.create_default_context(cafile="/etc/ssl/cert.pem" if os.path.exists("/etc/ssl/cert.pem") else None)


def api_key() -> str:
    key = os.environ.get("GROQ_API_KEY", "").strip()
    if not key:
        sys.exit("Set GROQ_API_KEY first.")
    return key


def _send(req: urllib.request.Request, timeout: float = 60) -> tuple[dict, float]:
    """Sends a request, waiting and retrying on 429. Returns (json, seconds)."""
    for attempt in range(6):
        start = time.time()
        try:
            with urllib.request.urlopen(req, timeout=timeout, context=_SSL) as resp:
                return json.load(resp), time.time() - start
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < 5:
                wait = float(e.headers.get("retry-after") or 5)
                print(f"    rate limited, waiting {wait:.0f}s", file=sys.stderr)
                time.sleep(wait + 0.5)
                continue
            raise RuntimeError(f"HTTP {e.code}: {e.read()[:300]!r}") from None
    raise RuntimeError("gave up after repeated rate limits")


def transcribe_verbose(wav_path: str, language: str | None = None, prompt: str | None = None,
                       model: str = "whisper-large-v3-turbo") -> dict:
    """Whisper's verbose_json for a clip, with the same fields the app sends."""
    boundary = uuid.uuid4().hex
    fields = {"model": model, "response_format": "verbose_json", "temperature": "0"}
    if language:
        fields["language"] = language
    if prompt:
        fields["prompt"] = prompt
    body = b""
    for name, value in fields.items():
        body += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
    with open(wav_path, "rb") as f:
        audio = f.read()
    body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\n"
             f"Content-Type: audio/wav\r\n\r\n").encode() + audio + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(f"{BASE}/audio/transcriptions", body, {
        "Authorization": f"Bearer {api_key()}",
        "Content-Type": f"multipart/form-data; boundary={boundary}",
        "User-Agent": "voiceflow-eval",
    })
    data, _ = _send(req)
    return data


def transcribe(wav_path: str, model: str = "whisper-large-v3-turbo", language: str | None = "en",
               prompt: str | None = None) -> tuple[str, float]:
    boundary = uuid.uuid4().hex
    fields = {"model": model, "response_format": "json"}
    if language:
        fields["language"] = language
    if prompt:
        fields["prompt"] = prompt
    body = b""
    for name, value in fields.items():
        body += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
    with open(wav_path, "rb") as f:
        audio = f.read()
    body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\n"
             f"Content-Type: audio/wav\r\n\r\n").encode() + audio + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(f"{BASE}/audio/transcriptions", body, {
        "Authorization": f"Bearer {api_key()}",
        "Content-Type": f"multipart/form-data; boundary={boundary}",
        "User-Agent": "voiceflow-eval",
    })
    data, secs = _send(req)
    return data.get("text", "").strip(), secs


def chat(model: str, system: str, user: str, max_tokens: int = 600, json_mode: bool = False) -> tuple[str, float]:
    body = {"model": model, "temperature": 0, "max_completion_tokens": max_tokens,
            "reasoning_effort": "low" if model.startswith("openai/gpt-oss") else "none",
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}]}
    if json_mode:
        body["response_format"] = {"type": "json_object"}
    req = urllib.request.Request(f"{BASE}/chat/completions", json.dumps(body).encode(), {
        "Authorization": f"Bearer {api_key()}", "Content-Type": "application/json", "User-Agent": "voiceflow-eval",
    })
    data, secs = _send(req)
    return (data["choices"][0]["message"].get("content") or "").strip(), secs
