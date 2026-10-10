<h1 align="center">Local AI</h1>

<p align="center">
  <img src="docs/images/icon.png" alt="Local AI icon: a waving blue robot" width="128">
</p>

<p align="center">
  <b>Personal AI assistants that run entirely on your Windows PC.</b><br>
  Chat or talk out loud. Conversations, voice and memories never leave your computer.
</p>

<p align="center">
  <img alt="Windows 11" src="https://img.shields.io/badge/Windows%2011-x64-0078D4?logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="Avalonia UI" src="https://img.shields.io/badge/UI-Avalonia-8B44AC">
  <img alt="Offline" src="https://img.shields.io/badge/cloud%20APIs-none-2EA043">
</p>

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Chatting with an assistant named Diana" width="900">
</p>

- **Your own assistants:** each with a name, personality, language model, voice, conversations and memories.
- **Text or voice:** three reply modes, from silent chat to a hands-free spoken conversation (see below).
- **Always at hand:** it runs in the notification area. Give each assistant a global shortcut to start and stop a
  voice conversation from any application; the tray icon blinks while it listens.
- **Long-term memory:** it learns facts about you and recalls them when relevant. You can see and delete each one.
- **Fully offline:** llama.cpp (Gemma 4), Whisper, Piper and SQLite on your GPU. The Internet is used only to download
  models.
- **Portable:** everything stays in the app folder. Copy it to install, delete it to remove every trace.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/create-assistant.png" alt="Creating an assistant"></td>
    <td width="50%"><img src="docs/screenshots/memory.png" alt="What the assistant remembers"></td>
  </tr>
  <tr>
    <td align="center">Create an assistant</td>
    <td align="center">What it remembers about you</td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/settings-voice.png" alt="Audio and voice settings"></td>
    <td><img src="docs/screenshots/settings-models.png" alt="Model management"></td>
  </tr>
  <tr>
    <td align="center">Voice and audio settings</td>
    <td align="center">Models managed in the app</td>
  </tr>
</table>

## Three ways to talk

Pick a mode with the **Text · Read aloud · Live** switch under the message box. The app remembers your choice.
In **Settings › Assistants › Ways to talk** you can turn Read aloud and Live off; Text is always available.

<table>
  <tr>
    <th width="33%">⌨️ Text</th>
    <th width="33%">🔊 Read aloud</th>
    <th width="33%">🎙️ Live</th>
  </tr>
  <tr valign="top">
    <td>
      <b>Quiet chat.</b> You type and the reply appears as streaming text. Nothing is played and the microphone
      stays closed.
    </td>
    <td>
      <b>Chat with a voice.</b> You type and the reply appears as text while the assistant's voice reads it to you.
      The microphone stays closed.
    </td>
    <td>
      <b>A spoken conversation.</b> The microphone stays open: just talk. The assistant answers aloud and then
      listens again. Speak over it to interrupt it at any time. You can still type, and typed messages are answered
      aloud too.
    </td>
  </tr>
  <tr valign="top">
    <td><i>Best for:</i> work, shared rooms, long answers.</td>
    <td><i>Best for:</i> listening while you do something else.</td>
    <td><i>Best for:</i> hands-free, back-and-forth conversation.</td>
  </tr>
</table>

- **Push-to-talk** works in Text and Read aloud modes. Hold **● Hold to talk** (or **Ctrl+Space**) while you speak.
  The assistant replies aloud.
- **Live from anywhere:** an assistant's global shortcut switches Live mode on and off from any application, and so
  does **Live mode** in the tray icon's menu. The tray icon blinks while the assistant listens. Shortcuts are
  unavailable while Live is turned off.
- **Esc** stops the current reply in every mode. Live mode needs a microphone and the speech recognition model.

## Ask about what's on your screen

**Read selection** lets you point the assistant at anything you are reading, in any application: an error in Visual
Studio, a function in VS Code or Notepad++, a log line in Unity, a paragraph in your browser.

1. Turn Live mode on (switch, tray menu or the assistant's shortcut).
2. Start talking, and select the text with the mouse while you speak: drag over it, or double- or triple-click.
3. When you stop talking, what you said and the selected text are sent together, so you can just ask
   *"why does this fail?"*, *"what does this function do?"* or *"translate this"*.

The chat shows the selected text as a quote above your message, and it stays in the conversation, so follow-up
questions ("and how do I fix it?") still see it. Only the latest selection is sent, once, with your next message
(spoken or typed); a hint under the message box shows when one is waiting. Very long selections are cut at 8,000
characters.

Turn it on in **Settings › Assistants › Ways to talk**, under Live. It is off by default.

How the text is read:

- First silently, through Windows UI Automation: browsers, Word, Notepad and most standard Windows apps.
- Applications that do not share their text that way (Notepad++, Unity, VS Code, parts of Visual Studio) get a
  **Ctrl+C**, and your clipboard is put back right after. Your copied text is restored exactly; if the clipboard holds
  something else (files, an image), the copy is skipped so you never lose it. Formatting of copied text is not
  restored, only the text.
- To stay harmless, no Ctrl+C is sent in terminals (it would stop the running program), while a key or mouse button is
  held, or when you dragged a scroll bar, title bar, tab, button or similar control.
- Password fields and Local AI's own window are ignored. Applications running as administrator cannot be read unless
  Local AI runs as administrator too.

## Getting started

**Download:** get `LocalAI-win-x64.zip` from the [latest release](https://github.com/lluancarlo/LocalAI/releases/tag/latest)
(built from `master` on every push), unzip it anywhere writable and run `LocalAI\LocalAI.exe`.

**Build it yourself:** you need Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), an NVIDIA GPU
(recommended) and about 9 GB of disk space for models.

```powershell
git clone https://github.com/lluancarlo/LocalAI.git
cd LocalAI
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
publish\LocalAI\LocalAI.exe
```

`publish.ps1` also writes `publish\LocalAI.zip`, the same files without `data\`.

On first launch you create your first assistant and the app downloads the models it needs.

Closing the window keeps Local AI running in the notification area. The tray icon's menu opens the window, turns
**Live mode** on and off, and **Exit** quits.
Set an assistant's shortcut in **Settings › Assistants**.

## Privacy

- **Offline:** conversations, voice and memories never leave your PC. The Internet is used only to download models.
- **One folder:** everything the app creates (database, settings, logs, temporary files, models) is in `data\` next
  to `LocalAI.exe`. Delete the folder to remove it all.
- **Per assistant:** each assistant has its own conversations and memories. Deleting an assistant deletes them too.
- **Logs** record what the app did, never what you said, typed or selected.
- **Read selection** is off by default. When on, the app reads selected text only while Live mode is on, and ignores
  password fields. In applications that need the Ctrl+C fallback, the selection briefly passes through the Windows
  clipboard: your clipboard is restored, but Windows clipboard history (Win+V) or a clipboard manager may still record
  the copied selection.
- **Outside the folder, Windows keeps its own records** of any program you run. These contain only the path of
  `LocalAI.exe` and timestamps:
  - the tray icon setting (`HKCU\Control Panel\NotifyIconSettings`);
  - microphone access history (*Settings › Privacy › Microphone*);
  - program launch data (`C:\Windows\Prefetch`).

## Built with

| Area | Technologies |
|---|---|
| Platform | C# on [.NET 10](https://dotnet.microsoft.com/), Windows 11 x64, published self-contained (no .NET install needed) |
| User interface | [Avalonia UI](https://avaloniaui.net/) (Fluent theme, Inter font), MVVM with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| Language model | [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` with CUDA, running Google [Gemma 4](https://huggingface.co/google) models (GGUF, QAT) |
| Memory | [EmbeddingGemma 300M](https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF) embeddings on llama.cpp, stored in [SQLite](https://www.sqlite.org/) ([Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)) |
| Speech recognition | [Whisper.net](https://github.com/sandrohanea/whisper.net) ([whisper.cpp](https://github.com/ggml-org/whisper.cpp)) with Whisper large-v3-turbo on CUDA |
| Speech synthesis and voice detection | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) with [Piper](https://github.com/rhasspy/piper) voices and [Silero VAD](https://github.com/snakers4/silero-vad) |
| Audio | [NAudio](https://github.com/naudio/NAudio) (WASAPI capture and playback, echo cancellation through Windows communications mode) |
| Windows integration | Win32 global hotkeys, low-level mouse hook and UI Automation (Read selection), notification-area icon |
| Infrastructure | Microsoft.Extensions (dependency injection, configuration, options, logging), [Serilog](https://serilog.net/) file logs, [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) for voice archives |
| Tests and delivery | [xUnit](https://xunit.net/), GitHub Actions (tests and a `latest` release on every push to `master`) |

Models are downloaded from inside the app, language models only from [Hugging Face](https://huggingface.co/).
Licenses of bundled components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

No license: all rights reserved. The source code is public to read, but you may not modify, copy or distribute it.
Bundled third-party components keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
