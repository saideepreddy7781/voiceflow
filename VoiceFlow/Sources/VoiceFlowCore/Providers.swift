import Foundation

/// Groq API facts, verified with live calls and Groq's docs on 2026-10-17.
///
/// - Endpoints are OpenAI-compatible: `/audio/transcriptions` (multipart) and `/chat/completions`.
/// - Upload limit: 25 MB on the free tier, 100 MB on the dev tier.
/// - Every transcription request is billed as at least 10 seconds of audio.
/// - Whisper `prompt` is limited to 224 tokens.
/// - Rate limits on the current key: 7,200 audio seconds and 2,000 requests for transcription;
///   8,000 tokens per minute and 1,000 requests for `qwen/qwen3.8-27b`. Free tier.
public enum Groq {
    public static let baseURL = URL(string: "https://api.groq.com/openai/v1")!

    public static let transcriptionModels = ["whisper-large-v3-turbo", "whisper-large-v3"]

    /// Cleanup and Command Mode candidates, fastest first. Median cleanup latency on 6 test cases:
    /// qwen3.8-27b 0.23 s, gpt-oss-20b 0.54 s, gpt-oss-120b 0.56 s.
    public static let textModels = ["qwen/qwen3.8-27b", "openai/gpt-oss-20b", "openai/gpt-oss-120b"]

    public static let whisperPromptTokenLimit = 224
    public static let minimumBilledSeconds = 10.0

    /// `gpt-oss` models take `low`; Qwen takes `none` to turn reasoning off.
    public static func reasoningEffort(for model: String) -> String {
        model.hasPrefix("openai/gpt-oss") ? "low" : "none"
    }
}

/// Gemini API facts. No key exists on this Mac yet, so the model ID is chosen in Settings
/// after checking Google's current model list.
public enum Gemini {
    public static let baseURL = URL(string: "https://generativelanguage.googleapis.com/v1beta")!
}
