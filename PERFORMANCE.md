# Performance

All numbers were measured on the reference machine during development (2026-10-04). Nothing is estimated unless
labelled as such.

**Machine:** AMD Ryzen 9 7950X, 64 GB DDR5, NVIDIA GeForce RTX 4080 SUPER 16 GB (driver 617.14, CUDA 13.4,
compute 8.9), Samsung 990 PRO, Windows 11 Pro 26200. Desktop baseline ~1.2–1.5 GB VRAM in use (browser, OBS running).

**Stack:** Gemma 4 12B IT QAT Q4_0, ctx 16,384, all layers on GPU, flash attention, 2 slots / unified KV;
llama.cpp b11381 (CUDA 13.4); Whisper large-v3-turbo q8_0 via Whisper.net 1.9.1 (CUDA);
Piper medium voices via sherpa-onnx 1.13.8 (CPU, 4 threads); EmbeddingGemma 300M Q8_0 (GPU).

Sources: application log (`%LOCALAPPDATA%\LocalAI\logs`), `PerformanceBenchmarks` and `BackendIntegrationTests`
(`dotnet test tests/LocalAI.Integration.Tests --filter "Category=Benchmark"`), `nvidia-smi`, process counters.

## Startup

| Metric | Value |
|---|---|
| Process start → window responsive | 429 ms |
| Process start → all subsystems ready (LLM, Whisper, TTS, embeddings, DB) | 2.9 s (Debug build), 3.3 s (Release publish, separate run) |
| LLM load (llama-server start → healthy, model file in OS cache) | 1.97–2.11 s |
| Whisper load | 670–850 ms |
| Piper (3 voices) load | 2.4–2.7 s (in parallel with the LLM) |
| Embedding server load | 670 ms |

## Language model

| Metric | Value |
|---|---|
| Generation speed | 65–77 tok/s (70.0 tok/s on a 437-token answer) |
| Time to first token, short prompt (25–128 tokens, cold) | 89–134 ms |
| Time to first token, 1,455-token prompt, cold | 412 ms (prompt processing 382 ms ≈ 3,800 tok/s) |
| Time to first token, same prompt cached | 87–95 ms |
| Cancel → stream stopped | 3 ms |

## Speech

| Metric | Value |
|---|---|
| Whisper, 1.6–1.9 s utterance (detect language + transcribe) | 107–119 ms (median of 3) |
| Whisper, 7.1–8.8 s utterance | 146–163 ms |
| Whisper, first call after load | 417 ms |
| Piper TTS, short sentence (1.6–1.9 s of audio) | 59–68 ms (real-time factor 0.035) |
| Piper TTS, long sentence (7.1–8.8 s of audio) | 250–310 ms (RTF 0.035) |
| Submit spoken turn → first audio queued (LLM first sentence + TTS) | 256–340 ms |

## Voice round trip

End of user speech → assistant audio starts, continuous mode, measured in `VoiceIntegrationTests` with real models:

| Stage (one run) | Time |
|---|---|
| End-of-speech detection (configured silence) | 600 ms + VAD hangover |
| Whisper | ~220 ms (state Transcribing → Thinking) |
| LLM first sentence + TTS | ~320 ms (Thinking → Speaking) |
| **Total** | **1,165 ms** |

Before tuning the same test measured ~1.27 s (700 ms end-of-speech silence). The end-of-speech silence is the
largest component; it is a UX trade-off (`Voice:EndOfSpeechSilenceMs`).

Barge-in reaction (user starts talking over the assistant → playback stopped): 557–714 ms in 5 runs with simulated
loudspeaker echo at −9 dB.

## Memory (VRAM and RAM)

| Metric | Value |
|---|---|
| LLM VRAM (driver-measured delta) | 8,091–8,257 MB (estimate shown in app: 8,071 MB) |
| All models loaded (LLM + Whisper + embeddings), VRAM delta | 9,761 MB → ~5 GB of the 16 GB remains free |
| App process (UI, Whisper, ONNX Runtime) | 731 MB working set |
| llama-server (chat) | 1,311 MB working set |
| llama-server (embeddings) | 632 MB working set |

## Optimizations applied (measured before/after)

| Change | Before | After |
|---|---|---|
| `--load-mode none` instead of mmap for fully offloaded models | load 3.75 s, chat server 7,200 MB resident | load 2.41 s, 1,352 MB resident; tok/s unchanged (69.9 → 69.3) |
| End-of-speech silence 700 → 600 ms | round trip ~1.27 s | 1.165 s |
| Chat pinned to slot 0, memory extraction to slot 1 (`id_slot`, unified KV) | extraction would evict the chat prompt cache | cached TTFT stays ~90 ms |
| Token deltas batched to one UI update per dispatcher frame | — | UI stays responsive at ~75 tok/s |

## Reproducing

```powershell
dotnet test tests/LocalAI.Integration.Tests --filter "Category=Benchmark" --logger "console;verbosity=detailed"
dotnet test tests/LocalAI.Integration.Tests --filter "Category=Integration" --logger "console;verbosity=detailed"
```
Each turn's TTFT and tok/s are also logged (`Turn Completed: N tokens, TTFT … ms, … tok/s`), and the last reply's
numbers are shown in Diagnostics.
