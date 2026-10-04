# Architecture

The C# application is the **orchestrator**. Neural inference runs in mature native engines (llama.cpp, whisper.cpp,
ONNX Runtime via sherpa-onnx); C# owns state, streaming, the voice pipeline, persistence, configuration, UI,
logging, cancellation and error handling.

## Solution layout

```
LocalAI.sln
  src/
    LocalAI.Configuration   Options POCOs, path resolution, model catalog, user settings store   (no dependencies)
    LocalAI.Core            Domain + abstractions + engine-agnostic orchestration                (→ Configuration)
    LocalAI.LLM             llama.cpp: llama-server supervisor, chat model, embeddings           (→ Core)
    LocalAI.Speech          Whisper.net STT, Piper TTS + Silero VAD via sherpa-onnx               (→ Core)
    LocalAI.Audio           WASAPI capture/playback (NAudio), resampling, device enumeration      (→ Core)
    LocalAI.Memory          SQLite conversations + memories, semantic retriever                   (→ Core)
    LocalAI.Infrastructure  Composition root (DI), logging, GPU info, startup orchestration       (→ all of the above)
    LocalAI.App             Avalonia UI (MVVM)                                                     (→ Infrastructure)
  tests/
    LocalAI.Core.Tests            unit: session, prompt, chunker, segmenter, barge-in, language, memory service
    LocalAI.Memory.Tests          unit: SQLite stores (real database files), retriever
    LocalAI.Infrastructure.Tests  unit: DI graph, configuration layering, model selection, llama-server protocol
    LocalAI.Integration.Tests     real models on the GPU, simulated room for voice, real audio devices
```

The UI never touches engines, SQLite or native libraries: it talks to `AssistantSession`,
`VoiceConversationController` and store interfaces.

## Core abstractions (LocalAI.Core)

| Interface | Implementation | Notes |
|---|---|---|
| `ILanguageModel` | `LlamaCppLanguageModel` | `LoadAsync`, `UnloadAsync`, `StreamAsync` (+ `GenerateAsync` extension), `Info` |
| `IEmbeddingService` | `LlamaCppEmbeddingService` | EmbeddingGemma 300M on a second llama-server |
| `ISpeechToText` | `WhisperSpeechToText` | 16 kHz mono float in, text + language out |
| `ITextToSpeech` | `SherpaTextToSpeech` | Piper (VITS) voices, one per language |
| `IVoiceActivityDetectorFactory` | `SileroVoiceActivityDetectorFactory` | Silero VAD v5 |
| `IAudioCapture` / `IAudioPlayer` / `IAudioDeviceProvider` | `WasapiAudio*` | Windows; `NullAudio*` elsewhere |
| `IConversationStore` | `SqliteConversationStore` | |
| `IMemoryStore` / `IMemoryRetriever` | `SqliteMemoryStore` / `MemoryRetriever` | |
| `ILanguageDetector` | `HeuristicLanguageDetector` | pt / it / en |
| `IGpuInfoProvider` | `NvidiaSmiGpuInfoProvider` | |
| `IAiTool`, `IWebSearchService` | — (none) | Reserved extension points, not used in the MVP |

Engine-agnostic orchestration also lives in Core: `AssistantSession`, `PromptBuilder`, `MemoryService`,
`SpeechOutput`, `SentenceChunker`, `UtteranceSegmenter`, `BargeInDetector`, `VoiceConversationController`.
All of it is unit-tested with fakes.

## Text turn

```
UI ─▶ AssistantSession.SubmitAsync(text)
        ├─ cancel any running turn (LLM + TTS + playback)
        ├─ detect language (text) / take Whisper's language (voice)
        ├─ create conversation if needed, persist user message (SQLite)
        ├─ recall memories (embed query → cosine over stored vectors)
        ├─ PromptBuilder: system prompt + memories + language hint + history within token budget
        ├─ ILanguageModel.StreamAsync ─▶ AssistantDelta events ─▶ UI (batched per frame)
        │                              └▶ SpeechOutput (if speaking)
        ├─ persist reply (+ stats, interrupted flag) — also when stopped
        └─ background: MemoryService.ExtractAndStoreAsync (LLM on a separate slot)
```

## Voice turn

```
Mic (WASAPI, AEC) ─▶ 16 kHz frames ─▶ Silero VAD (512-sample frames)
   ─▶ BargeInDetector.Gate (echo-aware) ─▶ UtteranceSegmenter (pre-roll, min speech, end-of-speech silence)
   ─▶ Whisper (language restricted to pt/it/en) ─▶ AssistantSession.SubmitAsync(..., Speak: true)
   ─▶ token stream ─▶ SentenceChunker ─▶ Piper TTS ─▶ gapless queued playback
While thinking/speaking the loop keeps listening; sustained user speech ⇒ CancelCurrentTurn() ⇒
the interrupting speech becomes the next utterance. Details: VOICE.md.
```

## Key design decisions

**llama.cpp via `llama-server` child process (not LLamaSharp, not Ollama).**
The official llama.cpp CUDA 13.4 Windows builds are published daily and support new model architectures immediately.
LLamaSharp's latest NuGet package (0.27) tracks a llama.cpp revision ~1,100 builds older, which risks not loading
current models. Running the engine as a separate process also isolates native crashes from the UI. Ollama would add a
service and model registry we don't need. The server is bound to `127.0.0.1` on a random port, protected by a random
API key passed via environment, started with `--offline`, web UI disabled, and placed in a Windows Job Object with
kill-on-close so it never outlives the app. The HTTP client is wrapped in `LoopbackOnlyHandler`, which refuses any
non-loopback request and bypasses system proxies.

**Two slots, unified KV.** `--parallel 2 --kv-unified`: interactive chat is pinned to slot 0, background memory
extraction to slot 1, so extraction never evicts the chat's prompt cache (cached TTFT ≈ 90 ms).

**`--load-mode none` when fully offloaded.** Measured: faster load and ~5.8 GB less resident RAM than mmap.

**Whisper.net in-process (CUDA, CPU fallback).** Its CUDA backend needs only `cublas64_13.dll`, which ships in the
llama.cpp CUDA runtime archive; the app prepends `runtime\llama.cpp` to the DLL search path, so no CUDA Toolkit is
required. Language detection is restricted to the configured languages (`DetectLanguageWithProbability` with
candidates), which prevents Portuguese being detected as Galician/Spanish.

**sherpa-onnx for TTS and VAD.** One NuGet package, in-process, cross-platform, runs Piper voices and Silero VAD on
the CPU (TTS real-time factor ≈ 0.035).

**NAudio 3 WASAPI with Windows AEC.** The capture stream opens in communications mode with the selected output device
as acoustic-echo-cancellation reference (Windows 11 22621+), falling back to plain capture if the device does not
support it. An adaptive echo gate (`BargeInDetector`) is the second line of defence.

**SQLite + brute-force cosine for semantic memory.** No vector database: thousands of 768-d float vectors are compared
in microseconds. Embeddings are stored as float32 BLOBs together with the embedding model id; vectors from another
model are ignored and re-computed (`BackfillEmbeddingsAsync`).

**Startup is per-subsystem and failure-isolated.** `StartupService` brings up the database, LLM, TTS, STT and
embeddings independently. Missing microphone, voice or embedding model degrades that feature only; text chat works
whenever the LLM loads (or reports "Model not installed" clearly).

## Configuration

At runtime there is a single shared `LocalAiOptions` instance (behind both `IOptions` and `IOptionsMonitor`); the
Settings menu changes it, applies the change to the affected component (player device, microphone, TTS voice/rate)
and persists it to `usersettings.json`.

`appsettings.json` (next to the executable) → `%LOCALAPPDATA%\LocalAI\usersettings.json` (written by the UI:
devices, voice mode) → `LOCALAI_*` environment variables (e.g. `LOCALAI_LocalAI__Llm__ContextSize=8192`).
All options are in `src/LocalAI.Configuration/Options.cs` with comments.

Paths: models and runtime are found under the "LocalAI home" — `LOCALAI_HOME`, else the nearest ancestor folder of the
executable containing `localai.home` (the repository root), else `%LOCALAPPDATA%\LocalAI`. User data
(database, logs, user settings) lives in `%LOCALAPPDATA%\LocalAI`.

## Extension points (not implemented in the MVP)

- `IAiTool` — future tools (filesystem, terminal, Godot, Visual Studio...). Model output is untrusted: a future tool
  host must validate arguments and require user confirmation; nothing generated is ever executed automatically.
- `IWebSearchService` — must live in a separate, optional assembly so that not loading it guarantees offline
  operation. No web code exists in the MVP.

## Known limitations

- **Voice was verified with synthetic speech, not a live human voice.** The full voice pipeline (VAD, endpointing,
  Whisper, LLM, TTS, barge-in with simulated speaker echo) is exercised by integration tests that feed Piper-generated
  speech through a simulated room; the real microphone and speaker were opened and streamed in hardware tests. During
  development the headset microphone delivered digital silence (likely muted), so a live spoken round trip still has to
  be confirmed by the user. The app warns when the microphone is silent.
- **Offline operation was verified by inspection, not by unplugging.** With the app running, its processes had only two
  listening sockets, both on 127.0.0.1, and no outbound TCP or UDP. A physical network-disconnected run was not
  performed (see PRIVACY.md for a 30-second check).
- Barge-in on loudspeakers depends on Windows AEC support of the audio device; without it, the echo gate needs your
  voice to be clearly louder at the mic than the assistant (headsets have no such limitation).
- Opening the microphone in communications mode can trigger Windows' "reduce other sounds" ducking (configured in
  the Sound control panel → Communications). Set `Audio:EchoCancellation` to `false` to avoid it.
- Replies are shown as plain text (markdown is not rendered).
- Speech recognition is restricted to pt/it/en by default (`SpeechToText:AllowedLanguages`; empty = any language).
- Text language detection covers pt/it/en; other languages rely on the model following the base instruction.
- One Piper voice per language, synthesized on the CPU.
- Memory extraction is model-based and can miss or over-generalize facts; review it in the Memory panel.
- The audio backend is Windows-only (WASAPI). On other OSes the app builds and text chat would work, but voice reports
  "no audio backend".
