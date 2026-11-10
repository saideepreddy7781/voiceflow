# winget manifest (draft)

These files describe VoiceFlow 0.1.0 for the [Windows Package Manager](https://github.com/microsoft/winget-pkgs).
Nothing has been submitted. They were written against manifest schema 1.12.0, which is what recent packages in
`winget-pkgs` use, and the hashes are the published ones from the v0.1.0 release. Only YAML parsing was
checked here, since `winget validate` needs a Windows machine.

To submit a new version: copy the folder to `manifests/s/SaideepReddy/VoiceFlow/<version>/` in a fork of
`microsoft/winget-pkgs`, update the version, URLs and SHA-256 hashes (`SHA256SUMS.txt` in the release has
them), run `winget validate --manifest <folder>` on Windows, and open a pull request.
