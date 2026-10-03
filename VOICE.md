# Voice

Two modes, both fully local:

- **Push-to-talk** — hold the *Hold to talk* button or **Ctrl+Space** while speaking, release to send.
- **Conversation mode** — toggle *Conversation mode*; speak naturally, the assistant answers aloud and listens again.
  Interrupt it at any time by talking (barge-in).

The status bar shows the state: `◉ Listening` → `… Transcribing` → `◌ Thinking` → `♪ Speaking`
(`● Recording` while push-to-talk is held). Typed messages can also be spoken (*Speak typed replies*).

## Pipeline

```
Microphone ──WASAPI (communications mode + AEC)──▶ downmix + WDL sinc resample ─▶ 16 kHz mono float frames
  │
  ├─ Silero VAD v5 (sherpa-onnx, 512-sample / 32 ms frames, threshold 0.5)
  ├─ BargeInDetector.Gate — removes frames that look like the assistant's own echo
  ├─ UtteranceSegmenter — 300 ms pre-roll, ≥ 250 ms speech to start, 600 ms silence to end, 30 s max
  ▼
Whisper large-v3-turbo q8_0 (CUDA) — language detection restricted to pt/it/en, then transcription
  ▼
AssistantSession (Speak = true) — LLM streams tokens
  ▼
SentenceChunker — emits a sentence as soon as it ends (the first chunk may break at a comma after ~40 chars);
                  strips markdown, URLs, emoji; never reads fenced code blocks
  ▼
Piper TTS (sherpa-onnx, CPU) — voice chosen by language; sentence N+1 is synthesized while N plays
  ▼
WasapiAudioPlayer — gapless queue, resampled to the device mix format, Stop() clears instantly
```

Audio is processed in memory only. It is never written to disk and never logged.

## Language and voices

| Language | Recognition | Voice (default) |
|---|---|---|
| Brazilian Portuguese | Whisper, `pt` | Piper `pt_BR-faber-medium` |
| Italian | Whisper, `it` | Piper `it_IT-paola-medium` |
| English | Whisper, `en` | Piper `en_US-lessac-medium` |

Whisper's detected language is passed to the conversation (stored with the message, used in the prompt hint and for
the voice). The speech output keeps the user's language unless a reply sentence is confidently in another language
(e.g. "translate this into Italian"), in which case it switches voice. Restricting detection to the configured
languages (`SpeechToText:AllowedLanguages`) avoids Portuguese being misdetected as Galician/Spanish; set it to `[]` to
allow every Whisper language (unsupported languages then use the fallback voice).

## Barge-in and echo

When the assistant speaks through loudspeakers, the microphone hears it, and the VAD classifies that as speech.
Two layers prevent the assistant from interrupting itself:

1. **Windows acoustic echo cancellation.** The mic opens in communications mode with the output device as AEC
   reference (`WithEchoCancellationReferenceEndpoint`, Windows 11 22621+). If the device or driver rejects that, the
   app falls back to communications mode without a reference, then to plain capture (logged as
   `Microphone opened ... mode EchoCancelled|Communications|Plain`).
2. **Adaptive echo gate** (`BargeInDetector`). While audio plays it learns the speaker→mic coupling: the ratio of
   mic RMS to the peak output RMS of the last 250 ms, tracked as a high percentile (fast up, slow down) so pauses
   between the assistant's words do not lower it. A frame counts as the user only if VAD says speech **and** the ratio
   exceeds `coupling × BargeInEchoMargin` (1.5). Barge-in fires when enough such frames (≥ `BargeInMinSpeechMs`, 300 ms
   worth) occur within a ~0.8 s window.

On barge-in the turn is cancelled (LLM generation, TTS, playback), the segmenter keeps only the audio since the
interruption began (earlier buffered audio would contain echo), and that speech is transcribed as the next turn.
Headsets have no echo path: the coupling stays near zero and plain VAD decides.

### How it was validated

`VoiceIntegrationTests` drive the real VAD, Whisper, LLM and Piper through `SimulatedRoom`, a virtual microphone and
speaker on one real-time clock that mixes the assistant's playback back into the microphone at −9 dB
(gain 0.35, loudspeakers without hardware AEC) and adds the user's speech on top. Results on the reference machine:

| Check | Result |
|---|---|
| Continuous mode, Italian question → spoken answer → back to listening | pass; end of speech → audio 1,165 ms |
| Assistant's own echo for 2.5 s + entire second reply | no false barge-in (5/5 runs) |
| User talks over the assistant | barge-in after 557–714 ms of speech (5/5 runs), playback stopped immediately |
| Interrupting utterance transcribed without echo | "Espera, pare. Qual é a capital da Itália?" → "A capital da Itália é Roma." |
| Push-to-talk, English | pass |

Echo-ratio distributions measured during tuning (synthetic speech, gain 0.35): echo-only frames never exceed the
coupling (p99 = max = 0.35), while user+echo frames spread widely (p50 0.36, p75 0.61) because speech contains many
quiet frames — hence a modest margin plus a windowed trigger instead of consecutive-frame counting.

## Tuning (`appsettings.json` → `LocalAI:Voice`)

| Setting | Default | Effect |
|---|---|---|
| `Mode` | `PushToTalk` | Initial mode; the UI toggle persists the last choice |
| `VadThreshold` | 0.5 | Silero speech probability threshold (raise in noisy rooms) |
| `PreRollMs` | 300 | Audio kept before speech onset |
| `MinSpeechMs` | 250 | Shorter sounds are ignored (clicks, coughs) |
| `EndOfSpeechSilenceMs` | 600 | Pause that ends your turn; raise if you get cut off mid-thought |
| `MaxUtteranceSeconds` | 30 | Hard cap per utterance |
| `BargeInEnabled` | true | Allow interrupting by voice in conversation mode |
| `BargeInMinSpeechMs` | 300 | Speech needed to interrupt |
| `BargeInEchoMargin` | 1.5 | How much louder than the learned echo your voice must be |

`LocalAI:Audio:EchoCancellation` (default `true`) controls Windows AEC. Devices are chosen in the UI and saved to
`usersettings.json`.

## Hallucination and silence handling

- Empty transcripts and well-known Whisper artifacts on silence ("Legendas pela comunidade Amara.org",
  "Sottotitoli creati dalla comunità Amara.org", `[Música]`) are discarded.
- A muted microphone produces exact digital silence; the app shows "No sound from the microphone… Is it muted?"
  (after a push-to-talk recording, or after 5 s in conversation mode).
