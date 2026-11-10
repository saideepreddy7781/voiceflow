# Contributing to VoiceFlow

Thanks for helping. VoiceFlow is a small project with two native apps, one for macOS and one for Windows.
Bug fixes, language improvements and tests are the most useful contributions right now. For anything
larger, open an issue first so we can agree on the shape before you spend time on it.

## Build and test

### macOS

You need macOS 14 or later and Xcode 26 (Swift 6.2).

```bash
swift test --package-path VoiceFlow     # unit tests, no network needed
scripts/build-app.sh release            # builds build/VoiceFlow.app
open build/VoiceFlow.app
```

`scripts/install-app.sh` builds, copies the app to `/Applications` and launches it. A local build is ad hoc
signed, so macOS treats each new build as a new app and asks for Microphone and Accessibility again. To keep
those permissions between builds, generate your own identity with `scripts/make-signing-cert.sh` (it only
writes outside the repository) and sign with `SIGN_IDENTITY`. See [docs/RELEASING.md](docs/RELEASING.md).

### Windows

You need the .NET 8 SDK. The solution builds on any OS, and `VoiceFlow.Windows` only runs on Windows.

```bash
dotnet build windows/VoiceFlow.sln -c Release
dotnet test windows/VoiceFlow.Core.Tests -c Release
```

On a Windows machine you can also run the app's own checks. `VoiceFlow.exe --self-test` is described in
[windows/README.md](windows/README.md).

### Live tests and your Groq key

The normal test suites never touch the network. Tests marked Live call Groq and run only when `GROQ_API_KEY`
is set in the environment, using a key you create at [console.groq.com/keys](https://console.groq.com/keys):

```bash
GROQ_API_KEY=... swift test --package-path VoiceFlow --filter Live
GROQ_API_KEY=... dotnet test windows/VoiceFlow.Core.Tests -c Release
GROQ_API_KEY=... python3 scripts/eval_cleanup.py     # grades the cleanup prompt against fixtures/
```

The audio clips the live tests read are generated on a Mac by `python3 scripts/make_fixtures.py`, in
`fixtures/clips/`, which is ignored by git.

Never put a key in a file that git tracks, in a commit message, or in a log. CI scans for key patterns and
fails if it finds one.

## The prompt parity rule

The cleanup, Command Mode and meeting-notes prompts must be identical on macOS and Windows, because both
apps should turn the same speech into the same text. If you edit a prompt in
`VoiceFlow/Sources/VoiceFlowCore/`, make the same edit in `windows/VoiceFlow.Core/`, and then run:

```bash
python3 scripts/check_prompt_parity.py
```

CI fails when the two differ. Prompt changes also need evidence: run `scripts/eval_cleanup.py` before and
after, and put the two scores in the pull request.

## How the code is organised

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) has the pipeline diagram and the file map for both platforms.

## Adding a speech engine

VoiceFlow ships one engine today: Whisper on Groq, with the user's own key. An engine belongs in this project
if it has a free tier that normal daily use fits inside and its terms don't allow training on the audio or
text it receives. Paid-only engines and on-device models are out of scope for now, so please open an issue
before writing one.

At a high level, adding an engine means:

1. Implement the `Transcriber` protocol in `VoiceFlow/Sources/VoiceFlowCore/Speech/` and `ITranscriber` in
   `windows/VoiceFlow.Core/Speech/`, on both platforms, with the same behaviour on each.
2. Add unit tests with a fake network layer, following `PipelineTests` on each platform. Add a Live test
   behind an environment variable if the engine has a free key.
3. Store its key with the existing secret storage (`SecretStore` on macOS, Credential Manager on Windows).
   Never log it.
4. Add the choice to preferences with a default that keeps today's behaviour. Add fields with defaults so
   existing settings keep decoding.
5. Run `scripts/eval_transcription.py` against it and put the results in the pull request, covering Telugu
   and mixed Telugu-English as well as English.
6. Update [docs/PRIVACY.md](docs/PRIVACY.md) with exactly what is sent, and to whom.

## Pull request expectations

- Keep each pull request to one change. Small ones get reviewed faster.
- The tests pass on the platforms you touched, and `python3 scripts/check_prompt_parity.py` passes.
- New behaviour comes with a test. Things that can't be tested automatically (real microphones, global keys,
  Accessibility) get a note in the pull request saying what you checked by hand and on which machine.
- User data has to survive upgrades. `meetings.json`, `history.json` and settings must keep decoding after
  your change.
- Never log, or put in diagnostics, what the user said or typed. Password fields, secure input and password
  managers stay protected.
- Comments are short and say why. UI strings are short and plain.
- GitHub Actions stay pinned to full commit SHAs.

## Reporting security problems

Please don't open a public issue. See [SECURITY.md](SECURITY.md).

By contributing you agree that your work is released under the [MIT License](LICENSE), and that you will
follow the [Code of Conduct](CODE_OF_CONDUCT.md).
