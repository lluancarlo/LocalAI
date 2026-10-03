# Privacy

Local AI is designed so that **no conversation, audio, memory or telemetry ever leaves your computer.**

## What the application does not do

- No cloud AI APIs (OpenAI, Anthropic, Google, …), no cloud speech recognition, no cloud TTS.
- No telemetry, analytics, usage statistics, update checks or crash reporting.
- No runtime downloads. Models are installed once by `scripts/setup.ps1`, the only component that uses the Internet.
- No tools or computer control: the assistant cannot run commands, open files or browse. Model output is shown as
  text and never executed.

## How this is enforced

| Mechanism | Where |
|---|---|
| The inference engines (llama-server) listen on `127.0.0.1` only, on a random port, protected by a random per-launch API key passed via environment (not on the command line). | `LlamaServerProcess` |
| Engines are started with `--offline` (and `LLAMA_ARG_OFFLINE=1`), web UI disabled. | `LlamaServerProcess.BuildArguments` (unit-tested) |
| Every HTTP client in the app is wrapped in `LoopbackOnlyHandler`, which throws for any non-loopback host and ignores system proxies. | `LoopbackOnlyHandler` (unit-tested) |
| Whisper and Piper run in-process; audio is kept in memory only. | `WhisperSpeechToText`, `SherpaTextToSpeech` |
| Engine processes are tied to the app with a Windows Job Object (kill-on-close), so no server keeps running after exit. | `ChildProcessJob` |
| There is no web/search code in the MVP. A future `IWebSearchService` must be a separate optional assembly. | `ARCHITECTURE.md` |

**Verification performed:** with the app running (LLM, embeddings, Whisper, TTS loaded) the app and its child
processes had exactly two TCP sockets, both `LISTEN` on `127.0.0.1`, and no outbound TCP connections or UDP endpoints
(`Get-NetTCPConnection` / `Get-NetUDPEndpoint` filtered by process). A run with the network physically disconnected
was not performed during development.

**Check it yourself in 30 seconds:** enable airplane mode (or unplug the network), start the app, send a message and
use push-to-talk. Everything works the same.

## What is stored, and where

| Data | Location | Notes |
|---|---|---|
| Conversations and messages (text, timestamps, detected language, model id, generation stats) | `%LOCALAPPDATA%\LocalAI\localai.db` (SQLite) | Delete a conversation in the sidebar (messages are removed with it). |
| Long-term memories (short facts + embedding vectors) | same database | View and delete in the Memory panel. Memories outlive the conversation they came from. |
| User settings (selected devices, voice mode) | `%LOCALAPPDATA%\LocalAI\usersettings.json` | |
| Logs | `%LOCALAPPDATA%\LocalAI\logs\localai-YYYYMMDD.log`, 14 days retained | Lifecycle, timings and errors only. Conversation text and audio are **never** logged. |
| Microphone audio | not stored | Processed in memory and discarded. |

To erase everything: close the app and delete `%LOCALAPPDATA%\LocalAI`.

The database is not encrypted; it is protected by your Windows account like any other file in your profile.
