# models/

Model files are downloaded here by `scripts/setup.ps1` and are not tracked by Git (only `catalog.json` and this file are).

```
models/
  catalog.json        the models setup.ps1 can install and the app can select from (tracked)
  llm/                GGUF language models
  embedding/          GGUF embedding model (long-term memory)
  whisper/            whisper.cpp ggml models
  vad/                Silero VAD (ONNX)
  tts/                Piper voices (sherpa-onnx format, one folder per voice)
```

See [MODEL_SETUP.md](../MODEL_SETUP.md).
