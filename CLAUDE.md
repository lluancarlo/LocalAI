# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Local AI: personal AI assistants that run fully offline on Windows 11 x64 (.NET 10, Avalonia UI). It uses llama.cpp
(Gemma 4) for chat and embeddings, Whisper.net for speech recognition, sherpa-onnx Piper voices for speech, Silero VAD
and SQLite. The Internet is used only to download models, and only when the user starts the download.

## Commands

```powershell
dotnet build LocalAI.sln

# Unit tests (what CI runs). "Desktop" tests need an interactive Windows session.
dotnet test tests/LocalAI.Core.Tests/LocalAI.Core.Tests.csproj --filter "Category!=Desktop"
dotnet test tests/LocalAI.Memory.Tests/LocalAI.Memory.Tests.csproj
dotnet test tests/LocalAI.Infrastructure.Tests/LocalAI.Infrastructure.Tests.csproj --filter "Category!=Desktop"

# A single test or class (xUnit)
dotnet test tests/LocalAI.Core.Tests/LocalAI.Core.Tests.csproj --filter "FullyQualifiedName~SentenceChunkerTests"

# Self-contained app in publish\LocalAI, plus publish\LocalAI.zip without data\.
# On first run it downloads the llama.cpp runtime (version in catalog.json) into runtime\.
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
publish\LocalAI\LocalAI.exe
```

- Close the app before you publish: its files are locked while it runs.
- `LocalAI.Integration.Tests` (traits `Integration`, `Hardware`, `Benchmark`) runs the real stack on the GPU. It
  uses the runtime and downloaded models of the published app in `publish\LocalAI` (`LocalAiFixture`,
  `TestPaths.PublishedApp`), so publish first and download the models in the app. CI does not run it.
- `.github/workflows/release.yml`: every push to `master` runs the unit tests, publishes the app and replaces the
  `latest` GitHub pre-release with `LocalAI-win-x64.zip`.
- Package versions are managed centrally in `Directory.Packages.props`. Nullable warnings are errors
  (`Directory.Build.props`).

## Architecture

Project dependencies point inward to `LocalAI.Core`:

- **LocalAI.Configuration**: `LocalAiPaths`, `LocalAiOptions` (the `LocalAI` config section), `UserSettingsStore`,
  and the embedded data files `catalog.json` (every downloadable model, voice and the llama.cpp runtime) and
  `languages.json`.
- **LocalAI.Core**: domain logic and interfaces only, with no native or OS dependencies (`ILanguageModel`,
  `ISpeechToText`, `ITextToSpeech`, `IAudioCapture`/`IAudioPlayer`, the stores, `IGlobalHotkeys`, ...).
- **Adapters** that implement the Core interfaces: `LocalAI.LLM` (llama.cpp), `LocalAI.Speech` (Whisper, Piper
  through sherpa-onnx, Silero VAD), `LocalAI.Audio` (WASAPI), `LocalAI.Memory` (SQLite stores), `LocalAI.Desktop`
  (Win32 global hotkeys), `LocalAI.Models` (catalog, downloads, `ModelLibrary`).
- **LocalAI.Infrastructure**: `LocalAiHost` is the composition root. It layers configuration (`appsettings.json`,
  then `data\usersettings.json`, then `LOCALAI_` environment variables), sets up Serilog file logging, points
  TEMP/TMP to `data\temp`, adds the llama.cpp folder to PATH (Whisper's CUDA backend uses llama.cpp's CUDA DLLs) and
  registers every service as a singleton. There is only one `LocalAiOptions` instance: the settings UI changes it
  in place and saves it to `usersettings.json`.
- **LocalAI.App**: Avalonia MVVM with CommunityToolkit.Mvvm and compiled bindings. The app runs in the tray and as a
  single instance per folder. Closing the window hides it; only **Exit** in the tray menu quits. ViewModels are
  thin layers over the Core services.

Main runtime flows:

- **Turn pipeline** (`AssistantSession.SubmitAsync`, the same for text and voice): save the user message, recall
  memories, build the prompt (`PromptBuilder`), stream the LLM reply (and, if needed, speak it through
  `SpeechOutput`: sentence chunks to TTS to queued playback), save the reply, then extract memories in the
  background (`MemoryService`). Only one turn runs at a time, and a new turn cancels generation, synthesis and
  playback.
- **Voice** (`VoiceConversationController`): one loop reads microphone frames. VAD and `UtteranceSegmenter` find
  utterances, Whisper transcribes them, and the session answers. `BargeInDetector` lets the user interrupt the
  assistant by talking, without reacting to the assistant's own voice from the speakers. `LiveActivation` and
  `AssistantHotkeys` connect each assistant's global shortcut to live mode.
- **Assistants** (`AssistantManager`, `AssistantContext`): each assistant has its own model, voice, conversations and
  memories. The stores read and write only the active assistant's data. Switching assistants applies the new
  assistant's model and voice.
- **LLM process** (`LlamaServerProcess`): `llama-server` runs as a child process on 127.0.0.1 with a random port,
  a random API key passed in its environment, `--offline`, and a Job Object that ties it to the app's lifetime.
  Chat and embeddings each use their own server. `ModelSelector` uses the active assistant's model, or with `auto` the strongest installed catalog model that fits
  in VRAM (catalog order is preference order).
- **Startup and models**: `StartupService` starts each subsystem on its own, so a failure in one (for example a
  missing microphone) does not stop text chat. `ModelService` installs and removes models while the app runs.
- **Database**: `SqliteDatabase` uses WAL mode and forward-only migrations (`Migrations` array plus
  `PRAGMA user_version`). Add a new array entry for each schema change; never edit an existing migration.
- `LocalAI.App.csproj` copies `runtime\llama.cpp` next to the executable, keeping only `llama-server` and the ggml
  backends. Publishing removes `.pdb` files and native files for other platforms.

# Project rules

## Everything stays in the application folder

The application never creates or changes files outside the folder of its executable (`LocalAiPaths.Home`).
Everything it creates or keeps for the user goes into `data\` next to the executable: database, settings, logs,
temporary files, downloaded models and partial downloads. Copying the folder installs the app; deleting it removes
everything the software ever created. `scripts\publish.ps1` replaces the program files and never touches `data\`.

- Get every path from `LocalAiPaths`. Add a property there for any new kind of file.
- Never use `Environment.SpecialFolder`, `GetFolderPath`, `Path.GetTempPath`/`GetTempFileName`, `%LOCALAPPDATA%`,
  `%APPDATA%`, `%USERPROFILE%`, the registry or isolated storage in `src/`. `SelfContainedFolderTests` fails if you do.
- TEMP/TMP point to `data\temp` for the app and its child processes (`LocalAiHost.ConfigureTempDirectory`), so
  third-party and native libraries stay inside the folder too. When adding a library, check it has no cache of its own
  elsewhere (e.g. `~/.cache`) and configure it to use a `LocalAiPaths` folder.
- Tests follow the same idea: use `TestPaths` (test output folder), not the system temp folder.

## Everything is written in English

Code, identifiers, comments, UI text, log messages, examples, tests and documentation are English.

- Text in other languages is data: put it in `src/LocalAI.Configuration/languages.json` (`LanguageData`), never in
  `.cs` or `.axaml`. `EnglishOnlyCodeTests` fails if application code contains non-ASCII letters.
- Tests are English too. Portuguese or Italian text is allowed only where the test verifies that language
  (language detection, speech recognition, voices, replies in the user's language).

## Other conventions

- Language models are downloaded only from Hugging Face (`ModelLibrary.IsHuggingFace`).
- Do not edit repository files with Windows PowerShell 5.1 `Get-Content`/`Set-Content`: it re-encodes UTF-8 as ANSI.
- Logs record what the app did, never what the user said or typed (no conversation text or audio in log messages).
