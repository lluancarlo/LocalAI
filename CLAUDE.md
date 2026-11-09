# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Local AI: personal AI assistants that run fully offline on Windows 11 x64 (.NET 10, Avalonia UI). It uses llama.cpp
(Gemma 4) for chat and embeddings, Whisper.net for speech recognition, sherpa-onnx Kokoro for speech, Silero VAD
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
  and the embedded data files `catalog.json` (every downloadable model, voice, the llama.cpp runtime and the companies
  listed in the model dialog) and
  `languages.json`.
- **LocalAI.Core**: domain logic and interfaces only, with no native or OS dependencies (`ILanguageModel`,
  `ISpeechToText`, `ITextToSpeech`, `IAudioCapture`/`IAudioPlayer`, the stores, `IGlobalHotkeys`, ...).
- **Adapters** that implement the Core interfaces: `LocalAI.LLM` (llama.cpp), `LocalAI.Speech` (Whisper, Kokoro
  through sherpa-onnx, Silero VAD), `LocalAI.Audio` (WASAPI), `LocalAI.Memory` (SQLite stores, the notes index and the
  note files), `LocalAI.Desktop` (Win32 global hotkeys, reading text selected in other applications), `LocalAI.Models` (catalog, downloads, `ModelLibrary`).
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
  memory (`MemoryVault.RecallAsync`), build the prompt (`PromptBuilder`), stream the LLM reply (and, if needed, speak
  it through `SpeechOutput`: sentence chunks to TTS to queued playback), then save the reply. Only one turn runs at a
  time, and a new turn cancels generation, synthesis and playback. The prompt is laid out for llama-server's prompt
  cache: the system message holds only what rarely changes (prompt, identity, profile), the history keeps the same
  first message from turn to turn (it is cut in jumps, to 3/4 of the budget) and fits the model's context, and what
  changes every turn (date and time, recalled passages) goes in a context block in the last user message. Each
  assistant's `AssistantBehavior` (changeable) sets the temperature, the reply length (a reminder in the last message
  and a token limit) and thinking. Thinking is turned on or off per request (`GenerationOptions.Thinking`: off sends
  both `enable_thinking: false` and a zero thinking budget, since model families differ); llama-server returns it
  apart (`reasoning_content`), and it is never spoken, sent back to the model or written to the notes, only kept in
  the message's metadata for the chat. Live mode never thinks. `AssistantBehavior.ContextSize` (0 = the model's
  default) and `CompactCache` (8-bit KV cache, `--cache-type-k/v q8_0`) start the model; an assistant whose context or
  cache differs from the loaded model's has its AI off (`IsModelLoaded`), and changing them restarts it. The sizes
  offered come from `ContextPlanner`: up to the model's trained context, with the KV cache per token read from the GGUF
  header (`GgufMetadata`) once the model is downloaded. The history gets whatever the context leaves after the rest of
  the prompt and the reply.
- **Memory** (`src/LocalAI.Core/Memory/Vault`): each assistant's memory is Markdown notes, the source of truth, in
  `<root>/local-ai/<assistant>/` (root = `data\memory`, or the Obsidian vault in `Memory:VaultPath`): an identity note
  (who the assistant is: its style plus model and voice as properties; it replaces the style in the system prompt), a
  profile of the user and one note per conversation. Names are kebab-case ASCII (`VaultLayout`). The language model always answers
  JSON and `NoteFormats` renders the Markdown. Generated text sits between `<!-- local-ai:begin/end -->` markers
  (`NoteDocument`); the user's edits win: a block whose hash differs from what the app wrote is never overwritten,
  profile items the user wrote always survive, and text outside the markers is never touched. The SQLite index
  (`SqliteVaultIndex`: notes, passages with embeddings and FTS5, job queue) can be rebuilt from the files. Recall
  ranks passages by meaning and words (reciprocal rank fusion; common words from `languages.json` are left out of
  the word search) within `MemoryTokenBudget`. It searches the user's own notes and only the newest
  `MaxConversationNotes` (100) conversation notes; the passages it searches are cached in memory until the index
  changes. The conversation list marks the conversations whose note is searched.
- **Learned documents** (`MemoryVault.LearnAsync`): each assistant's `learn` folder and its subfolders hold `.md`/`.txt`
  files the user adds (Settings › Assistants lists the files at its top and one line per subfolder). A passage's title
  is its path in the folder (`VaultLayout.PassageTitle`). They are never indexed on their own (scans and the watcher skip `VaultLayout.IsLearnPath`);
  Learn reads them all, embeds every passage, then replaces all the assistant's `NoteKind.Document` notes in one
  transaction (`IVaultIndex.ReplaceDocumentsAsync`), so a failure keeps what was learned before. It needs the embedding
  model (an AI on); replies, Live mode and the memory work wait while it runs. Recall ranks the documents apart from
  the notes, each with its own share (`RetrieveTopKDocuments`, half the budget), and the prompt labels them apart.
- **Reply language**: an assistant always replies in its native language (`AssistantProfile.Language`), whatever the
  user writes, pastes or speaks; the user's detected language is only recorded with the message.
- **Memory work** (`MemoryWorker`): writing conversation notes, merging the profile, writing the identity notes and
  indexing notes, from a queue in the database. It runs only through `FinishAsync`: the sidebar's **Update memory**
  (`UpdateMemoryAsync`, chat blocked) or, with `Memory:AutoUpdate` (off by default), the same work once the active
  assistant has been idle for `ConversationIdleMinutes`, once per idle period. Nothing else triggers it: not leaving a
  conversation, not turning the Brain off, not exiting. When the user sends a message or speaks, an idle update stops
  and its job is requeued, so the assistant always comes first. `WorkActivity` holds the current step of the reply and
  of the memory work for the status bar (no user text in steps); its `SlowMotion` pauses at every step (Diagnostics).
- **Voice** (`VoiceConversationController`): one loop reads microphone frames. VAD and `UtteranceSegmenter` find
  utterances, Whisper transcribes them, and the session answers. `BargeInDetector` lets the user interrupt the
  assistant by talking, without reacting to the assistant's own voice from the speakers. `LiveActivation` and
  `AssistantHotkeys` connect each assistant's global shortcut to live mode. Turn-taking in live mode
  (`TurnTaking.cs`, `ConversationCues`): a transcript that sounds unfinished is held for a longer silence, speech
  before the reply starts is joined into the same message, `ReactionPlanner` plays a short reaction in the user's
  pauses, and a phrase fills a reply that is slow to start. The phrases are pre-synthesized with the active voice
  (from `languages.json`) and are skipped when not ready.
- **Assistants** (`AssistantManager`, `AssistantContext`): each assistant has its own model, voice, conversations and
  memory notes. Name and native language are fixed at creation; the personality (`SetStyleAsync`: the identity
  note is written again at once), the model (with its context) and the voice
  (another of the same language, with its speed and pitch; the picture follows its gender) change in Settings ›
  Assistants (`SetModelAsync`, `SetVoiceAsync`): a new model starts at once when the Brain is on, and the identity
  note is written again. The stores read and write only the active assistant's data. Every voice is a speaker of one Kokoro
  model (`catalog.json`: `tts` is the model, downloaded file by file from a pinned Hugging Face revision; `voices` lists
  its speakers with their `speaker` id and espeak-ng `lang`). `SherpaTextToSpeech` keeps one engine per language,
  shared by that language's voices. A voice has speed and pitch (`VoiceTuning`), changed by its `settings` in
  `catalog.json`. Switching assistants applies the new assistant's settings; its voice loads only if its AI is on.
  Each assistant's picture (`AssistantAvatars`) is chosen from its voice's `gender` in `catalog.json`.
- **LLM process** (`LlamaServerProcess`): `llama-server` runs as a child process on 127.0.0.1 with a random port,
  a random API key passed in its environment, `--offline`, and a Job Object that ties it to the app's lifetime.
  Chat and embeddings each use their own server. `ModelSelector` uses the active assistant's model, or with `auto` the strongest installed catalog model that fits
  in VRAM (catalog order is preference order).
- **Startup and models**: everything heavy follows the AI. `StartupService` opens the database and activates the
  assistant; the models load only when an AI turns on (`AssistantManager.LoadModelAsync`: the language model and the
  voice, then Whisper and the embeddings, `AiCompanions`, once the model is up) and unload when it turns off. At startup
  the AI turns on only with `Assistant:LoadModelAtStartup` (off by default). The sidebar's Controls: Brain (the language
  model and the embeddings), Hearing (Whisper) and Speaker (the voice); each assistant's `Hearing`/`Speaker` are
  remembered and applied whenever its Brain is on (`AssistantManager.SetSensesAsync`). Update memory
  (`MemoryWorker.UpdateMemoryAsync`) runs `FinishAsync` with the chat blocked, like Learn
  (`MemoryVault.TryBeginExclusiveWork`). Memory jobs that need the language model or
  the embeddings (writing and indexing notes) wait for the AI. A failure in one engine never stops the others.
  `ModelService` installs and removes models while the app runs. Downloads the user started stay in
  `data\downloads\pending.json` until installed (cancelling one clears the list) and resume when the app starts.
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
- The only exception: the memory notes move to the Obsidian vault the user picks in Settings › Memory
  (`Memory:VaultPath`, `FileVaultStorage`). The app then writes only inside that vault's `local-ai` folder, and the
  index stays in `data\`. Nothing else may be written outside the application folder.
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

- Language models and the voices are downloaded only from Hugging Face (`ModelLibrary.IsHuggingFace`). Besides the
  catalog's models, the user can pick any GGUF model in the model dialog (`ModelPickerWindow`, `ModelBrowser`) (anonymous Hugging Face API,
  `HuggingFaceApi`); added models are kept in `AddedLanguageModels` (`models\llm\added.json`), come after the catalog's
  and are never the hardware recommendation.
- Do not edit repository files with Windows PowerShell 5.1 `Get-Content`/`Set-Content`: it re-encodes UTF-8 as ANSI.
- Logs record what the app did, never what the user said or typed (no conversation text or audio in log messages).
