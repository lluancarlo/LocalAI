# Model setup

The application never downloads anything. A one-time setup script fetches the native runtime and the models while you
are online; afterwards everything runs offline.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
```

Options:

| Switch | Effect |
|---|---|
| `-Llm <id>` | Install a specific LLM from the catalog instead of the automatic choice |
| `-SkipLlm` | Skip the LLM download (e.g. you bring your own GGUF) |
| `-SkipRuntime` | Skip the llama.cpp runtime |
| `-AllVoices` | Install all 16 catalog voices instead of the 3 defaults |
| `-Force` | Re-download everything |

Downloads resume if interrupted (`curl -C -`) and sizes are verified. Everything goes into `runtime\` and `models\`
(both git-ignored). Only `models\catalog.json` is tracked.

## What gets installed

| Component | File | Size | Source |
|---|---|---|---|
| llama.cpp runtime b11381, CUDA 13.4 + CUDA runtime DLLs | `runtime\llama.cpp\` | 711 MB | github.com/ggml-org/llama.cpp releases |
| LLM (auto-selected, see below) | `models\llm\*.gguf` | 5–14 GB | Hugging Face (google / unsloth) |
| EmbeddingGemma 300M Q8_0 (memory) | `models\embedding\` | 334 MB | huggingface.co/ggml-org |
| Whisper large-v3-turbo q8_0 | `models\whisper\` | 874 MB | huggingface.co/ggerganov/whisper.cpp |
| Silero VAD v5 | `models\vad\` | 2 MB | sherpa-onnx releases |
| Piper voices pt_BR faber, it_IT paola, en_US lessac (medium) | `models\tts\` | 232 MB total (incl. espeak-ng data) | sherpa-onnx tts-models |

You do not need the CUDA Toolkit: the llama.cpp archive includes `cudart64_13.dll`, `cublas64_13.dll` and
`cublasLt64_13.dll`, which both llama.cpp and Whisper's CUDA backend use. A driver supporting CUDA 13 is required for
GPU acceleration (`nvidia-smi` shows the supported CUDA version).

## Automatic model selection

`models\catalog.json` lists the LLMs in preference order with the VRAM they need. Both the setup script and the
application pick the first one whose `minVramMb` fits the GPU's total VRAM:

| Catalog id | Model | Quant | File | Chosen when VRAM ≥ |
|---|---|---|---|---|
| `gemma-4-26b-a4b-qat-q4kxl` | Gemma 4 26B-A4B Instruct (MoE) | UD-Q4_K_XL (QAT) | 14.3 GB | 22 GB |
| `gemma-4-12b-qat-q4_0` | Gemma 4 12B Instruct | Q4_0 (QAT, official Google GGUF) | 7.0 GB | 11 GB |
| `gemma-4-e4b-qat-q4_0` | Gemma 4 E4B Instruct | Q4_0 (QAT) | 5.2 GB | any |

**Why Gemma 4 12B QAT for a 16 GB card:** strong reasoning and instruction following with very good Portuguese and
Italian; Google's quantization-aware-trained Q4_0 keeps quality close to bf16 at 7 GB. It runs entirely on the GPU at
~70 tok/s with a 16k context and leaves ~5 GB of VRAM for Whisper, the embedding model and the desktop. The 26B MoE at
14.3 GB would not leave room for Whisper and the KV cache on 16 GB; dense 27B models need ≤ IQ3 quantization to fit.
Thinking mode is disabled (`--reasoning off`) for conversational latency.

The application decides the remaining parameters itself:

- **GPU layers**: all (`-1`) when a GPU is present, `0` without one. If GPU initialization fails it retries on CPU
  (`Llm:CpuFallback`).
- **Context**: the catalog default (16,384 for the 12B), shared by two server slots (chat + background memory work).
- **VRAM estimate**: weights + ~0.05 MB/token of context + 600 MB compute buffers, shown next to the driver-measured
  value in Diagnostics (12B: estimated 8,071 MB, measured 8,091–8,257 MB).

## Changing the model

- Another catalog model: `setup.ps1 -Llm gemma-4-26b-a4b-qat-q4kxl`, then set `"Llm": { "Model": "gemma-4-26b-a4b-qat-q4kxl" }`
  in `appsettings.json` (or leave `"auto"`; auto only picks models that are installed and fit).
- Any GGUF: copy it anywhere and set `"Llm": { "ModelPath": "D:\\Models\\my-model.Q5_K_M.gguf" }`. Quantization is
  inferred from the file name. Set `ContextSize` and `GpuLayers` if the model does not fit entirely in VRAM.
- Adding to the catalog: append an entry to `models\catalog.json` (`id`, `displayName`, `file`, `url`, `sizeBytes`,
  `quantization`, `minVramMb`, `defaultContext`), keeping the list ordered from largest to smallest `minVramMb`.

## Changing voices or the Whisper model

- Voices: install them with `setup.ps1 -AllVoices` and pick them in **Settings → Voices**. Any other `vits-piper-*`
  archive from the sherpa-onnx `tts-models` release also works: add it to the `tts` section of the catalog and run
  `setup.ps1 -AllVoices` again.
- Whisper: add an entry under `whisper` in the catalog (any ggml model from huggingface.co/ggerganov/whisper.cpp)
  and set `SpeechToText:Model`.

## Upgrading llama.cpp

Edit `runtime.llamaCppBuild` and the two URLs in `models\catalog.json` and re-run `setup.ps1` (it replaces
`runtime\llama.cpp` when the build tag changes).
