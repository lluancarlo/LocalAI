# Troubleshooting

Logs: `%LOCALAPPDATA%\LocalAI\logs\localai-YYYYMMDD.log`. The **Diagnostics** panel shows the state of every
subsystem. Each subsystem fails independently; text chat keeps working when voice or memory is unavailable.

## Status bar says "Model not installed. Run scripts\setup.ps1 …"

No catalog model was found under `models\llm\`. Run `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`.
If you run a published build outside the repository, set `LOCALAI_HOME` to the folder that contains `models\` and
`runtime\`, or set `Paths:ModelsDirectory` / `Paths:RuntimeDirectory` in `appsettings.json`.

## "Failed to load model: llama.cpp runtime not installed"

`runtime\llama.cpp\llama-server.exe` is missing. Run `setup.ps1` (without `-SkipRuntime`).

## Status is yellow: "GPU initialization failed (…); running on CPU (slower)"

llama-server could not start with GPU offload and the app retried on CPU (`Llm:CpuFallback`). Common causes:

- Driver too old for CUDA 13 — check `nvidia-smi` (needs "CUDA Version: 13.x"); update the NVIDIA driver.
- Not enough free VRAM (games, other AI apps). Close them, or lower `Llm:ContextSize`, or set `Llm:GpuLayers` to a
  number below the model's layer count, or install a smaller model (`setup.ps1 -Llm gemma-4-e4b-qat-q4_0`).

The log contains the last llama-server output lines for the failed attempt.

## Voice buttons are disabled / "Voice: unavailable"

- `Voice: no microphone found` — connect a microphone and click ↻ in **Settings → Audio devices**.
- `Voice: unavailable (Speech recognition unavailable: …)` — the Whisper model is missing (`setup.ps1`) or failed to
  load; see the log. Whisper is configured to fall back to its CPU backend if the CUDA backend cannot be loaded
(Diagnostics shows which backend is active).

## "No sound from the microphone … Is it muted?"

The microphone delivers pure digital silence (the level meter shows "silence"). Use **Settings → Test microphone** to
check it without talking to the assistant. Unmute it (many headsets mute when the boom is raised), check Windows
Settings → Privacy → Microphone ("Let desktop apps access your microphone"), and pick the right device in the
microphone list.

## The assistant interrupts itself when speaking through speakers

Its own voice is being picked up as barge-in. In order of preference:

1. Use a headset, or lower the speaker volume.
2. Check the log for `Microphone opened: … mode EchoCancelled`. If it says `Plain`, the device does not support
   Windows echo cancellation.
3. Raise `Voice:BargeInEchoMargin` (e.g. 2.0) or `Voice:BargeInMinSpeechMs` (e.g. 500), or set
   `Voice:BargeInEnabled` to `false` (you can still stop it with Esc or the Stop button).

## Barge-in does not react when I talk over it

Lower `Voice:BargeInEchoMargin` (minimum 1.05) or speak closer to the microphone. Esc / Stop always works.

## Other sounds get quieter while the microphone is open

Windows ducks other audio when it detects communications activity (the mic opens in communications mode for echo
cancellation). Change Sound control panel → Communications → "Do nothing", or set `Audio:EchoCancellation` to `false`.

## I get cut off when I pause / it waits too long after I finish

Adjust `Voice:EndOfSpeechSilenceMs` (default 600). Higher = more patient, lower = faster replies.

## Wrong language detected

Speech: make sure `SpeechToText:AllowedLanguages` lists the languages you speak. Text: very short messages
("ok", "C#?") are ambiguous; for those the app sends no explicit language hint and the model follows the conversation
(the spoken voice uses the language of your previous message).

## Memory panel says "keyword" instead of "semantic"

The embedding model did not start (missing file or failure, see log). Memories still work with keyword matching;
once the embedding model is available, missing vectors are computed automatically at startup.

## Reset everything

Close the app and delete `%LOCALAPPDATA%\LocalAI` (database, settings, logs). Models in `models\` are untouched.
