# Development

## Prerequisites

- Windows 11 x64, .NET SDK 10.0.x (shared build settings in `Directory.Build.props`: net10.0, nullable, x64)
- NVIDIA driver with CUDA 13 support for GPU tests (no CUDA Toolkit needed)
- Runtime and models: `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`

Package versions are managed centrally in `Directory.Packages.props`.

## Build and run

```powershell
dotnet build LocalAI.sln
dotnet run --project src\LocalAI.App            # Debug
dotnet run --project src\LocalAI.App -c Release
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1   # → publish\LocalAI\LocalAI.exe
```

## Tests

| Project | Kind | Needs |
|---|---|---|
| `LocalAI.Core.Tests` | unit | nothing |
| `LocalAI.Memory.Tests` | unit (real SQLite files in %TEMP%) | nothing |
| `LocalAI.Infrastructure.Tests` | unit (DI graph, configuration, model selection, llama-server protocol) | nothing |
| `LocalAI.Integration.Tests` | integration | GPU, installed models, ~10 GB free VRAM; `Hardware` tests need audio devices |

```powershell
# Unit tests only
dotnet test tests\LocalAI.Core.Tests; dotnet test tests\LocalAI.Memory.Tests; dotnet test tests\LocalAI.Infrastructure.Tests

# Integration tests (real LLM/Whisper/Piper/embeddings, simulated room for voice)
dotnet test tests\LocalAI.Integration.Tests --filter "Category=Integration"
# Real microphone / speaker (plays only silence)
dotnet test tests\LocalAI.Integration.Tests --filter "Category=Hardware"
# Benchmarks (print timings)
dotnet test tests\LocalAI.Integration.Tests --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

Integration tests use a throw-away data directory, never your real database. Close the app before running them if
VRAM is tight (both would load the 8 GB model).

`SimulatedRoom` (in the integration tests) implements both `IAudioCapture` and `IAudioPlayer` on a 20 ms real-time
clock: it mixes queued assistant audio back into the microphone at a configurable echo gain and adds scripted user
speech (synthesized with Piper). This is how continuous mode and barge-in are tested end to end without a person.

## Conventions

- Core contains only abstractions and engine-agnostic logic; engines live in their own projects behind interfaces.
- Every long-running operation takes a `CancellationToken`. UI updates are marshalled to the Avalonia dispatcher.
- Subsystem failures are contained (log + degraded feature), never fatal. `StartupService` reports per-subsystem state.
- Never log conversation text or audio.
- Model output is untrusted data: displayed, spoken, stored — never executed.
- Source files are UTF-8; avoid tools that re-encode files with the ANSI code page (e.g. Windows PowerShell 5.1
  `Get-Content | Set-Content` without `-Encoding utf8`).

## Adding a new engine

Implement the Core interface in a new project (e.g. `ILanguageModel` → `MyEngineLanguageModel`), register it in
`LocalAiHost.AddLocalAi`, and add integration tests next to the existing ones. Nothing else needs to change: the UI,
session, voice pipeline and memory only see the interface.

## Repository hygiene

`.gitignore` excludes `runtime/`, `models/*` (except `catalog.json`), `publish/`, build output, databases, logs, WAV
files and local settings. Do not commit model files or user data.
