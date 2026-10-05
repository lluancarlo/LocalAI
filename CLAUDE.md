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
