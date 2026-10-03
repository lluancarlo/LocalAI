# Local AI

A fully local, offline AI assistant for Windows with text chat and voice conversation, written in C# (.NET 10, Avalonia).
Everything — the language model, speech recognition, speech synthesis, memory — runs on your machine.
No cloud APIs, no telemetry, no data leaves the computer.

```
┌──────────────────────────────────────────────────────────────────┐
│ Local AI         │ Explique async/await em C#                    │
│ ＋ New           │                                               │
│ [search…]        │ You  01:23 · pt                               │
│                  │ > Explique async/await em C#                  │
│ Explique async…  │                                               │
│ Roman Empire…    │ Diana  01:23 · pt                             │
│                  │ > `async` e `await` permitem…                 │
│                  ├───────────────────────────────────────────────│
│ ◆ Memory         │ [Type a message……………………………………]  [Send]   │
│ ⚙ Diagnostics    │ [● Hold to talk] [Conversation mode] [mic ▾] │
│                  │ ● Ready  GPU: RTX 4080 SUPER  Model: Gemma 4… │
└──────────────────────────────────────────────────────────────────┘
```

## What it does

- **Text chat** with token-by-token streaming, Stop (Esc), and persistent conversation history
  (create, rename, delete, search, continue after restart).
- **Push-to-talk**: hold the *Hold to talk* button or **Ctrl+Space**, speak, release. Whisper transcribes locally, the LLM answers,
  and the answer is spoken while it is still being generated.
- **Conversation mode** (hands-free): voice activity detection finds the end of each utterance, the assistant answers
  aloud and listens again. **Barge-in**: start talking while it speaks and it stops immediately and listens.
- **Automatic language**: Brazilian Portuguese, Italian and English are detected (Whisper for speech, a local detector
  for text); the assistant answers in your language and speaks with a matching voice.
- **Long-term memory**: durable facts about you ("User prefers C# for software development") are extracted locally,
  stored in SQLite with embeddings, and recalled when relevant. View/delete them in the Memory panel.
- **Diagnostics**: GPU, VRAM, model, quantization, context size, GPU layers, estimated and measured VRAM, speech engines.

## Quick start

Requirements: Windows 11 x64 (developed and tested; Windows 10 is untested), .NET 10 SDK, an NVIDIA GPU with a driver
supporting CUDA 13 recommended.
No CUDA Toolkit, CMake or Visual Studio is needed.

```powershell
# 1. One-time download of the runtime and models (~9 GB). The only step that uses the Internet.
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1

# 2. Run
dotnet run --project src\LocalAI.App -c Release
```

Or build a release folder: `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`, then run
`publish\LocalAI\LocalAI.exe`.

The model is chosen automatically for your GPU (see [MODEL_SETUP.md](MODEL_SETUP.md)). On this project's reference
machine (RTX 4080 SUPER 16 GB) that is **Gemma 4 12B Instruct (QAT, Q4_0)**, fully on the GPU.

## Keyboard

| Key | Action |
|---|---|
| Enter / Shift+Enter | Send / new line |
| Esc | Stop generation and speech |
| Hold Ctrl+Space | Push-to-talk |
| Ctrl+N | New conversation |
| F2 | Rename conversation |

## Documentation

| Document | Contents |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Projects, components, data flow, design decisions |
| [MODEL_SETUP.md](MODEL_SETUP.md) | What gets installed, automatic model selection, changing models |
| [VOICE.md](VOICE.md) | Voice pipeline, VAD, barge-in, echo handling, tuning |
| [PERFORMANCE.md](PERFORMANCE.md) | Measured latency, throughput, VRAM and RAM |
| [PRIVACY.md](PRIVACY.md) | What is stored, where, and the offline guarantees |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Building, testing, project conventions |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Common problems and fixes |

## Status

MVP. See "Known limitations" in [ARCHITECTURE.md](ARCHITECTURE.md#known-limitations).
