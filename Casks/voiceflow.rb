cask "voiceflow" do
  version "0.1.0"
  sha256 "9b3598e7fc7cb98d0f243469df53626601177e4a6e5d5022cf3df3b7d027f1be"

  url "https://github.com/saideepreddy7781/voiceflow/releases/download/v#{version}/VoiceFlow-#{version}-macOS.dmg"
  name "VoiceFlow"
  desc "Voice typing that puts cleaned-up text in any app"
  homepage "https://github.com/saideepreddy7781/voiceflow"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on macos: :sonoma

  app "VoiceFlow.app"

  uninstall quit: "com.saideep.voiceflow"

  zap trash: [
    "~/Library/Application Support/VoiceFlow",
    "~/Library/Preferences/com.saideep.voiceflow.plist",
  ]

  caveats <<~EOS
    VoiceFlow is not notarized, so Gatekeeper blocks the first launch. Open the app once, then go to
    System Settings > Privacy & Security and click Open Anyway.
    Details: https://github.com/saideepreddy7781/voiceflow/blob/main/docs/packaging/homebrew.md
  EOS
end
