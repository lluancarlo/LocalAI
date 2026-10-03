namespace LocalAI.Configuration;

/// <summary>Root of the "LocalAI" configuration section. Bound from appsettings.json + usersettings.json.</summary>
public sealed class LocalAiOptions
{
    public const string SectionName = "LocalAI";

    public PathOptions Paths { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
    public AssistantOptions Assistant { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public SpeechToTextOptions SpeechToText { get; set; } = new();
    public TextToSpeechOptions TextToSpeech { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
}

public sealed class PathOptions
{
    /// <summary>Directory with model files. Empty = auto (see <see cref="LocalAiPaths"/>).</summary>
    public string ModelsDirectory { get; set; } = "";
    /// <summary>Directory with native runtimes (llama.cpp). Empty = auto.</summary>
    public string RuntimeDirectory { get; set; } = "";
    /// <summary>Directory for database, logs and user settings. Empty = %LOCALAPPDATA%\LocalAI.</summary>
    public string DataDirectory { get; set; } = "";
}

public sealed class LlmOptions
{
    /// <summary>Catalog id of the model, or "auto" to pick the best installed model for the GPU.</summary>
    public string Model { get; set; } = "auto";
    /// <summary>Explicit GGUF path; overrides <see cref="Model"/> when set.</summary>
    public string ModelPath { get; set; } = "";
    /// <summary>Context size in tokens. 0 = model catalog default.</summary>
    public int ContextSize { get; set; }
    /// <summary>Layers to offload to the GPU. -1 = all, 0 = CPU only.</summary>
    public int GpuLayers { get; set; } = -1;
    /// <summary>Retry on CPU only if GPU initialization fails.</summary>
    public bool CpuFallback { get; set; } = true;
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.95f;
    public int MaxTokens { get; set; } = 1024;
    /// <summary>Server slots. 2 lets memory extraction run without evicting the chat prompt cache.</summary>
    public int ParallelSlots { get; set; } = 2;
    public bool FlashAttention { get; set; } = true;
    public int StartupTimeoutSeconds { get; set; } = 120;
}

public sealed class AssistantOptions
{
    public string Name { get; set; } = "Diana";
    public string SystemPrompt { get; set; } =
        "You are {name}, a helpful, precise assistant running fully offline on the user's computer. " +
        "Always answer in the same language as the user's last message (Brazilian Portuguese, Italian, English, or other). " +
        "Be concise and direct. The user is an experienced software engineer.";
    /// <summary>Extra instruction appended when the answer will be spoken aloud.</summary>
    public string VoiceStyleHint { get; set; } =
        "Your reply will be spoken aloud: answer conversationally in short sentences; avoid markdown, lists, tables and code blocks unless explicitly asked.";
    /// <summary>Approximate token budget for conversation history sent to the model.</summary>
    public int HistoryTokenBudget { get; set; } = 6000;
}

public sealed class EmbeddingOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "embeddinggemma-300m-q8_0";
    public int GpuLayers { get; set; } = -1;
    public string QueryPrefix { get; set; } = "task: search result | query: ";
    public string DocumentPrefix { get; set; } = "title: none | text: ";
}

public sealed class SpeechToTextOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "large-v3-turbo-q8_0";
    public bool UseGpu { get; set; } = true;
    /// <summary>Spoken languages to choose from during auto-detection (ISO 639-1). Empty = any.</summary>
    public List<string> AllowedLanguages { get; set; } = [];
    public int Threads { get; set; } = 8;
}

public sealed class TextToSpeechOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Voice id per language (catalog ids). Missing languages fall back to <see cref="FallbackLanguage"/>.</summary>
    public Dictionary<string, string> Voices { get; set; } = [];
    public string FallbackLanguage { get; set; } = "en";
    public float Speed { get; set; } = 1.0f;
    public int Threads { get; set; } = 4;
}

public sealed class AudioOptions
{
    /// <summary>Device id of the microphone; empty = system default.</summary>
    public string InputDeviceId { get; set; } = "";
    /// <summary>Device id of the speaker; empty = system default.</summary>
    public string OutputDeviceId { get; set; } = "";
    /// <summary>
    /// Open the microphone in Windows communications mode with acoustic echo cancellation referenced to the output
    /// device, so the assistant's own voice is removed from the mic signal (needed for barge-in on speakers).
    /// </summary>
    public bool EchoCancellation { get; set; } = true;
}

public enum VoiceMode { PushToTalk, Continuous }

public sealed class VoiceOptions
{
    public VoiceMode Mode { get; set; } = VoiceMode.PushToTalk;
    /// <summary>Silero speech probability threshold.</summary>
    public float VadThreshold { get; set; } = 0.5f;
    /// <summary>Audio kept before speech onset so the first syllable is not clipped.</summary>
    public int PreRollMs { get; set; } = 300;
    /// <summary>Minimum speech length for an utterance to count.</summary>
    public int MinSpeechMs { get; set; } = 250;
    /// <summary>Silence that ends an utterance.</summary>
    public int EndOfSpeechSilenceMs { get; set; } = 700;
    public int MaxUtteranceSeconds { get; set; } = 30;
    public bool BargeInEnabled { get; set; } = true;
    /// <summary>Sustained speech needed to interrupt the assistant.</summary>
    public int BargeInMinSpeechMs { get; set; } = 300;
    /// <summary>How much louder (RMS ratio) the mic must be than the learned speaker echo to count as barge-in.</summary>
    public float BargeInEchoMargin { get; set; } = 1.5f;
}

public sealed class MemoryOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Extract long-term facts from conversations automatically (local LLM).</summary>
    public bool AutoExtract { get; set; } = true;
    public int RetrieveTopK { get; set; } = 5;
    public float MinRelevance { get; set; } = 0.45f;
    /// <summary>Cosine similarity above which a new memory is considered a duplicate.</summary>
    public float DuplicateThreshold { get; set; } = 0.88f;
}
