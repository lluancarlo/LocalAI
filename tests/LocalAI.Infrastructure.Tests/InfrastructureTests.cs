using LocalAI.Tests;
using System.Text.Json;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using LocalAI.LLM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task All_services_resolve_and_core_abstractions_map_to_implementations()
    {
        var paths = new LocalAiPaths(TestPaths.New("di"));
        var configuration = LocalAiHost.BuildConfiguration(paths);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalAi(configuration, paths);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<LlamaCppLanguageModel>(provider.GetRequiredService<ILanguageModel>());
        Assert.IsType<LocalAI.Speech.WhisperSpeechToText>(provider.GetRequiredService<ISpeechToText>());
        Assert.IsType<LocalAI.Speech.SherpaTextToSpeech>(provider.GetRequiredService<ITextToSpeech>());
        Assert.IsType<LocalAI.Memory.SqliteConversationStore>(provider.GetRequiredService<IConversationStore>());
        Assert.IsType<LocalAI.Memory.SqliteVaultIndex>(provider.GetRequiredService<LocalAI.Core.Memory.Vault.IVaultIndex>());
        Assert.IsType<LocalAI.Memory.FileVaultStorage>(provider.GetRequiredService<LocalAI.Core.Memory.Vault.IVaultStorage>());
        Assert.NotNull(provider.GetRequiredService<LocalAI.Core.Memory.Vault.MemoryWorker>());
        // The notes stay in the application folder unless the user picks a vault.
        Assert.StartsWith(paths.DataDirectory, provider.GetRequiredService<LocalAI.Core.Memory.Vault.IVaultStorage>().Root, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<LlamaCppEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.NotNull(provider.GetRequiredService<IAudioCapture>());
        Assert.NotNull(provider.GetRequiredService<AssistantSession>());
        Assert.NotNull(provider.GetRequiredService<VoiceConversationController>());
        Assert.Same(provider.GetRequiredService<ILanguageModel>(), provider.GetRequiredService<LlamaCppLanguageModel>());
        Assert.NotEmpty(provider.GetRequiredService<ModelService>().Packages);
    }

    [Fact]
    public async Task A_catalog_model_found_on_hugging_face_is_the_catalogs_and_an_added_one_can_be_taken_back()
    {
        var paths = new LocalAiPaths(TestPaths.New("catalog-match"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalAi(LocalAiHost.BuildConfiguration(paths), paths);
        await using var provider = services.BuildServiceProvider();
        var models = provider.GetRequiredService<ModelService>();
        var browser = provider.GetRequiredService<LocalAI.Models.ModelBrowser>();

        // Google's own Gemma 4 12B repository and file: the catalog's Gemma 4 12B.
        var gemma = browser.CreateEntry(new LocalAI.Models.FoundModel(
                new LocalAI.Models.HuggingFaceModel("google/gemma-4-12B-it-qat-q4_0-gguf", 1, 0, 0, "gemma4", "", false), true),
            "abc", new LocalAI.Models.Quantization("Q4_0", ["gemma-4-12b-it-qat-q4_0.gguf"], 1));
        Assert.Equal("gemma-4-12b-qat-q4_0", models.CatalogModelFor(gemma)?.Id);

        // Another repository: not the catalog's. Added for an assistant that could not be created, it is taken back.
        var other = gemma with { Id = "hf-x--y-q4", Repo = "x/y", Dir = "llm/hf/x--y/q4", File = "llm/hf/x--y/q4/y.gguf", Files = ["y.gguf"] };
        Assert.Null(models.CatalogModelFor(other));
        var added = models.AddLanguageModel(other);
        Assert.Contains(models.Packages, p => p.Id == added.Id);
        models.RemoveAddedModel(added);
        Assert.DoesNotContain(models.Packages, p => p.Id == added.Id);
        Assert.False(models.IsAnyDownloading);
        Assert.Null(models.DownloadProgress(added.Id));
    }

    [Fact]
    public async Task Models_and_the_voices_used_by_an_assistant_cannot_be_removed()
    {
        var paths = new LocalAiPaths(TestPaths.New("usage"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalAi(LocalAiHost.BuildConfiguration(paths), paths);
        await using var provider = services.BuildServiceProvider();
        var models = provider.GetRequiredService<ModelService>();
        var llm = models.Packages.First(p => p.Kind == LocalAI.Models.ModelKind.Llm);
        var voice = models.Packages.First(p => p.Kind == LocalAI.Models.ModelKind.Voice);
        await provider.GetRequiredService<LocalAI.Core.Assistants.IAssistantStore>().CreateAsync(
            new LocalAI.Core.Assistants.NewAssistant("Nova", "", llm.Id, "if_sara", "it"), VoiceStyle.Default);

        var usage = await models.GetUsageAsync();
        Assert.Equal(["Nova"], usage[llm.Id]);
        Assert.Equal(["Nova"], usage[voice.Id]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => models.UninstallAsync(voice));
        Assert.Equal("Used by Nova.", ex.Message);
    }
}

public sealed class ConfigurationTests
{
    [Fact]
    public void Appsettings_binds_all_sections()
    {
        var configuration = LocalAiHost.BuildConfiguration(new LocalAiPaths(TestPaths.New("cfg")));
        var o = new LocalAiOptions();
        configuration.GetSection(LocalAiOptions.SectionName).Bind(o);

        Assert.Equal("auto", o.Llm.Model);
        Assert.Equal(-1, o.Llm.GpuLayers);
        Assert.Equal(["pt", "it", "en"], o.SpeechToText.AllowedLanguages);
        Assert.Empty(o.TextToSpeech.Voice); // set from the active assistant
        Assert.Equal(ReplyMode.Text, o.Voice.ReplyMode);
        Assert.True(o.Memory.Enabled);
        Assert.True(o.Audio.EchoCancellation);
    }

    [Fact]
    public void User_settings_override_appsettings()
    {
        var dataDir = TestPaths.New("cfg");
        try
        {
            var paths = new LocalAiPaths(dataDir);
            var store = new UserSettingsStore(paths.UserSettingsPath);
            store.Set("Voice", "ReplyMode", "Live");
            store.Set("Audio", "InputDeviceId", "{mic-id}");
            store.Set("Audio", "InputDeviceId", "{mic-id-2}"); // last write wins

            var o = new LocalAiOptions();
            LocalAiHost.BuildConfiguration(paths).GetSection(LocalAiOptions.SectionName).Bind(o);
            Assert.Equal(ReplyMode.Live, o.Voice.ReplyMode);
            Assert.Equal("{mic-id-2}", o.Audio.InputDeviceId);
            Assert.Equal(["pt", "it", "en"], o.SpeechToText.AllowedLanguages); // untouched values still from appsettings
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        }
    }

    [Fact]
    public void The_application_folder_is_the_executables_folder()
    {
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), LocalAiPaths.ForApplication().Home);
    }

    [Fact]
    public void Everything_the_app_creates_is_in_the_data_folder_next_to_the_executable()
    {
        var app = TestPaths.New("app"); // never created
        var paths = new LocalAiPaths(app);
        var data = Path.Combine(app, "data");
        Assert.Equal(data, paths.DataDirectory);
        string[] created = [paths.ModelsDirectory, paths.DownloadsDirectory, paths.LogsDirectory, paths.TempDirectory,
            paths.DatabasePath, paths.UserSettingsPath];
        Assert.All(created, p => Assert.StartsWith(data + Path.DirectorySeparatorChar, p));
        Assert.Equal(Path.Combine(app, "runtime", "llama.cpp"), paths.LlamaCppDirectory); // shipped with the app
    }

    [Fact]
    public void Catalog_parses_and_unknown_fields_are_ignored()
    {
        var catalog = ModelCatalog.Parse("""
            { "llm": [ { "id": "a", "displayName": "A", "file": "llm/a.gguf", "minVramMb": 1000, "url": "x", "extra": 1 } ],
              "tts": { "id": "k", "repo": "org/k", "files": ["model.onnx", "data/"] },
              "voices": [ { "id": "v", "speaker": 7, "language": "pt", "lang": "pt-br" } ] }
            """);
        Assert.Equal("a", Assert.Single(catalog.Llm).Id);
        Assert.Equal(["model.onnx", "data/"], catalog.Tts.Files);
        Assert.Equal((7, "pt"), (Assert.Single(catalog.Voices).Speaker, catalog.Voices[0].Language));
        Assert.Empty(catalog.Whisper);
    }

    [Fact]
    public void Repository_catalog_is_valid()
    {
        var catalog = ModelCatalog.LoadDefault();
        Assert.NotEmpty(catalog.Llm);
        Assert.NotEmpty(catalog.Whisper);
        Assert.NotEmpty(catalog.Vad);
        // Preference order: larger VRAM requirement first.
        Assert.Equal(catalog.Llm.OrderByDescending(l => l.MinVramMb).Select(l => l.Id), catalog.Llm.Select(l => l.Id));
        Assert.All(catalog.Llm, l => Assert.True(LocalAI.Models.ModelLibrary.IsHuggingFace(new Uri(l.Url)), l.Url));
        Assert.All(catalog.Llm, l => Assert.True(l.SizeBytes > 0));
        // Hugging Face files come from a pinned commit: a file its publisher uploads again keeps its size here.
        Assert.All(catalog.Llm.Select(l => l.Url).Concat(catalog.Whisper.Concat(catalog.Embedding).Select(e => e.Url)),
            url => Assert.Matches("^https://huggingface\\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}/", url));
        Assert.All(catalog.Whisper.Concat(catalog.Vad).Concat(catalog.Embedding), e => Assert.True(e.SizeBytes > 0));
        Assert.NotEmpty(catalog.Runtime.Urls);
        // Each archive of the runtime has its SHA-256, checked when it is downloaded.
        Assert.Equal(catalog.Runtime.Urls.Count, catalog.Runtime.Sha256.Count);
        Assert.All(catalog.Runtime.Sha256, hash => Assert.Matches("^[0-9a-f]{64}$", hash));

        // The speech model comes from a pinned revision, so its speaker ids never move.
        Assert.Matches("^[0-9a-f]{40}$", catalog.Tts.Revision);
        Assert.True(catalog.Tts.SizeBytes > 0);
        Assert.Contains("model.onnx", catalog.Tts.Files);
        Assert.Contains("espeak-ng-data/", catalog.Tts.Files);
    }

    [Fact]
    public void Repository_voices_are_valid()
    {
        var voices = ModelCatalog.LoadDefault().Voices;
        var languages = LanguageData.LoadDefault().Languages.Keys;
        Assert.Equal(voices.Count, voices.Select(v => v.Id).Distinct().Count());
        Assert.Equal(voices.Count, voices.Select(v => v.Speaker).Distinct().Count());
        // Only the languages the app knows, each with exactly one default voice.
        Assert.All(voices, v => Assert.Contains(v.Language, languages));
        Assert.All(languages, l => Assert.Single(voices, v => v.Language == l && v.Default));
        // espeak-ng language names sherpa-onnx accepts: British English is "en" ("en-gb" gives no audio at all).
        Assert.All(voices, v => Assert.Contains(v.Lang, new[] { "pt-br", "it", "en-us", "en" }));
        Assert.All(voices, v => Assert.StartsWith(v.Language + "-", v.Locale, StringComparison.Ordinal));
        // The gender picks the picture of an assistant created with the voice.
        Assert.All(voices, v => Assert.Contains(v.Gender, new[] { "female", "male" }));
        Assert.Equal("[pt-BR] Dora (female)", voices.Single(v => v.Id == "pf_dora").Label);
    }

    [Fact]
    public void A_voice_gets_the_default_settings_with_its_own_changes()
    {
        var catalog = ModelCatalog.Parse("""
            { "voices": [
              { "id": "plain", "language": "en" },
              { "id": "tuned", "language": "en", "settings": { "speed": { "default": 1.25, "max": 1.75 }, "pitch": { "default": 9 } } },
              { "id": "fixed", "language": "en", "settings": { "unsupported": ["Pitch"] } } ] }
            """);
        Assert.Equal(VoiceTuning.Default, catalog.Voices[0].Tuning);

        var tuned = catalog.Voices[1].Tuning;
        Assert.Equal(new VoiceSettingRange(0.75, 1.75, 1.25), tuned.Speed);
        Assert.Equal(1, tuned.Pitch!.Default); // kept within the range

        Assert.Null(catalog.Voices[2].Tuning.Pitch);
        Assert.Equal(VoiceTuning.Default.Speed, catalog.Voices[2].Tuning.Speed);
    }
}

public sealed class ModelSelectorTests : IDisposable
{
    private readonly string _dir = TestPaths.New("models");
    private readonly ModelCatalog _catalog = new()
    {
        Llm =
        [
            new LlmCatalogEntry { Id = "big", DisplayName = "Big", File = "llm/big.gguf", MinVramMb = 22000, DefaultContext = 16384, Quantization = "Q4" },
            new LlmCatalogEntry { Id = "mid", DisplayName = "Mid", File = "llm/mid.gguf", MinVramMb = 11000, DefaultContext = 16384, Quantization = "Q4" },
            new LlmCatalogEntry { Id = "small", DisplayName = "Small", File = "llm/small.gguf", MinVramMb = 0, DefaultContext = 8192 },
        ],
    };

    public ModelSelectorTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "llm"));
        foreach (var f in new[] { "big", "mid", "small" }) File.WriteAllBytes(Path.Combine(_dir, "llm", f + ".gguf"), new byte[1024 * 1024]);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(24576, "big")]
    [InlineData(16376, "mid")]
    [InlineData(8192, "small")]
    [InlineData(null, "small")]
    public void Auto_selects_best_model_that_fits_vram(int? vram, string expected) =>
        Assert.Equal(expected, ModelSelector.Select(_catalog, new LlmOptions(), _dir, vram).Entry.Id);

    [Fact]
    public void Skips_models_that_are_not_installed()
    {
        File.Delete(Path.Combine(_dir, "llm", "mid.gguf"));
        Assert.Equal("small", ModelSelector.Select(_catalog, new LlmOptions(), _dir, 16376).Entry.Id);
    }

    [Fact]
    public void Missing_models_produce_a_clear_error()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "llm"))) File.Delete(f);
        var ex = Assert.Throws<ModelNotAvailableException>(() => ModelSelector.Select(_catalog, new LlmOptions(), _dir, 16376));
        Assert.Equal(ModelSelector.NoModelMessage, ex.Message);
    }

    [Fact]
    public void An_assistants_model_is_never_silently_replaced()
    {
        File.Delete(Path.Combine(_dir, "llm", "big.gguf"));
        var ex = Assert.Throws<ModelNotAvailableException>(() => ModelSelector.Select(_catalog, new LlmOptions { Model = "big" }, _dir, 24576));
        Assert.StartsWith("Big is not installed yet", ex.Message);
    }

    [Fact]
    public void No_gpu_means_cpu_layers_and_explicit_settings_win()
    {
        var cpu = ModelSelector.Select(_catalog, new LlmOptions(), _dir, null);
        Assert.Equal(0, cpu.GpuLayers);
        Assert.Equal(0, cpu.EstimatedVramMb);

        var explicitModel = ModelSelector.Select(_catalog, new LlmOptions { Model = "big", ContextSize = 4096 }, _dir, 16376);
        Assert.Equal("big", explicitModel.Entry.Id);
        Assert.Equal(4096, explicitModel.ContextSize);
        Assert.True(explicitModel.EstimatedVramMb > 600);
    }

    [Theory]
    [InlineData("big", 16376, true)] // needs 22 GB on a 16 GB GPU: split between GPU and CPU
    [InlineData("big", 24576, false)]
    [InlineData("mid", 16376, false)]
    [InlineData("big", null, false)] // no GPU: CPU only, nothing to split
    public void A_model_larger_than_the_gpu_is_split_between_gpu_and_cpu(string model, int? vram, bool split) =>
        Assert.Equal(split, ModelSelector.Select(_catalog, new LlmOptions { Model = model }, _dir, vram).FitToMemory);

    [Fact]
    public void A_model_added_from_hugging_face_loads_from_its_folder_but_auto_prefers_the_catalog()
    {
        var added = new AddedLanguageModels(new LocalAiPaths(_dir) { ModelsDirectory = _dir });
        Directory.CreateDirectory(Path.Combine(_dir, "llm", "hf", "org--m", "q4"));
        File.WriteAllBytes(Path.Combine(_dir, "llm", "hf", "org--m", "q4", "m-00001-of-00002.gguf"), new byte[1024]);
        added.Add(new LlmCatalogEntry
        {
            Id = "hf-org--m-q4", DisplayName = "M", File = "llm/hf/org--m/q4/m-00001-of-00002.gguf", Dir = "llm/hf/org--m/q4",
            Repo = "org/m", Files = ["m-00001-of-00002.gguf", "m-00002-of-00002.gguf"], SizeBytes = 4L << 30, MinVramMb = 5000,
        });
        var catalog = added.Merge(_catalog);

        var chosen = ModelSelector.Select(catalog, new LlmOptions { Model = "hf-org--m-q4" }, _dir, 16376);
        Assert.EndsWith("m-00001-of-00002.gguf", chosen.FullPath);
        Assert.True(chosen.EstimatedVramMb > 4096); // the whole split model, not its first file
        Assert.Equal("mid", ModelSelector.Select(catalog, new LlmOptions(), _dir, 16376).Entry.Id);
    }
}

public sealed class LlamaServerTests
{
    [Fact]
    public void Server_arguments_are_loopback_offline_and_without_web_ui()
    {
        var args = LlamaServerProcess.BuildArguments(new LlamaServerStartInfo
        {
            ExecutablePath = "llama-server.exe", ModelPath = "m.gguf", ContextSize = 16384, GpuLayers = -1, ParallelSlots = 2,
        }, 12345);
        var joined = string.Join(' ', args);
        Assert.Contains("--host 127.0.0.1", joined, StringComparison.Ordinal);
        Assert.Contains("--offline", args);
        Assert.Contains("--no-webui", args);
        Assert.Contains("--n-gpu-layers 999", joined, StringComparison.Ordinal);
        Assert.Contains("--kv-unified", args);
        Assert.Contains("--reasoning auto --reasoning-format deepseek", joined, StringComparison.Ordinal); // per request
        Assert.Contains("--load-mode none", joined, StringComparison.Ordinal);
        Assert.DoesNotContain(args, a => a.Contains("api-key", StringComparison.Ordinal)); // key goes via environment
    }

    [Fact]
    public void A_model_larger_than_the_gpu_lets_llama_cpp_fit_it_with_room_for_the_other_models()
    {
        var args = LlamaServerProcess.BuildArguments(new LlamaServerStartInfo
        {
            ExecutablePath = "x", ModelPath = "big.gguf", GpuLayers = -1, FitToMemory = true,
        }, 1);
        var joined = string.Join(' ', args);
        Assert.Contains("--fit on", joined, StringComparison.Ordinal);
        Assert.Contains("--fit-target 2560", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("--n-gpu-layers", args); // llama.cpp decides how many layers fit
        Assert.DoesNotContain("--load-mode", args); // weights partly used from host memory: keep mmap
    }

    [Fact]
    public void Embedding_server_arguments()
    {
        var args = LlamaServerProcess.BuildArguments(new LlamaServerStartInfo
        {
            ExecutablePath = "x", ModelPath = "e.gguf", Embedding = true, GpuLayers = 0, ContextSize = 2048,
        }, 1);
        Assert.Contains("--embedding", args);
        Assert.Contains("--ubatch-size 2048", string.Join(' ', args), StringComparison.Ordinal); // a whole input in one batch
        Assert.DoesNotContain("--reasoning", args);
        Assert.DoesNotContain("--load-mode", args); // CPU: keep mmap
        Assert.Contains("--n-gpu-layers 0", string.Join(' ', args), StringComparison.Ordinal);
    }

    [Fact]
    public void Request_body_streams_with_prompt_cache_and_slot_pinning()
    {
        var json = LlamaCppLanguageModel.BuildRequestBody(
            [new ChatMessage(ChatRole.System, "s"), new ChatMessage(ChatRole.User, "u")],
            new GenerationOptions { Temperature = 0.2f, MaxTokens = 50, Background = true }, parallelSlots: 2);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("stream").GetBoolean());
        Assert.True(r.GetProperty("cache_prompt").GetBoolean());
        Assert.Equal(1, r.GetProperty("id_slot").GetInt32());
        Assert.Equal("system", r.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(50, r.GetProperty("max_tokens").GetInt32());
        // Thinking off: both ways of stopping it (Qwen templates and the others).
        Assert.False(r.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(0, r.GetProperty("thinking_budget_tokens").GetInt32());

        using var thinking = JsonDocument.Parse(LlamaCppLanguageModel.BuildRequestBody(
            [new ChatMessage(ChatRole.User, "u")], new GenerationOptions { Thinking = true }, parallelSlots: 2));
        Assert.False(thinking.RootElement.TryGetProperty("thinking_budget_tokens", out _));
        Assert.False(thinking.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void Reasoning_comes_apart_from_the_reply()
    {
        var evt = SseParser.Parse("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Let me count\"}}]}");
        Assert.Equal(("Let me count", null), (evt!.Reasoning, evt.Text));
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}", "Hel", false)]
    [InlineData("data: {\"choices\":[{\"delta\":{}}],\"timings\":{\"predicted_n\":42,\"predicted_per_second\":75.5,\"prompt_n\":10}}", null, true)]
    [InlineData("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}", null, false)]
    [InlineData(": keep-alive", null, false)]
    [InlineData("", null, false)]
    public void Sse_lines_are_parsed(string line, string? text, bool hasStats)
    {
        var evt = SseParser.Parse(line);
        Assert.Equal(text, evt?.Text);
        Assert.Equal(hasStats, evt?.Stats != null);
        if (hasStats)
        {
            Assert.Equal(42, evt!.Stats!.GeneratedTokens);
            Assert.Equal(75.5, evt.Stats.TokensPerSecond);
        }
    }

    [Fact]
    public void An_error_of_the_server_is_told_apart_when_the_request_did_not_fit_the_context()
    {
        const string overflow =
            "{\"error\":{\"code\":400,\"message\":\"request (9000 tokens) exceeds the available context size (8192 tokens), try increasing it\"," +
            "\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9000,\"n_ctx\":8192}}";
        Assert.True(SseParser.IsContextOverflow(overflow));
        Assert.StartsWith("request (9000 tokens) exceeds", SseParser.ErrorMessage(overflow), StringComparison.Ordinal);
        Assert.False(SseParser.IsContextOverflow("{\"error\":{\"code\":500,\"message\":\"out of memory\",\"type\":\"server_error\"}}"));
        Assert.Null(SseParser.ErrorMessage("Service Unavailable"));

        // Sent after the stream started, it ends the reply instead of being skipped.
        Assert.Equal("out of memory", SseParser.Parse("data: {\"error\":{\"code\":500,\"message\":\"out of memory\"}}")!.Error);
    }

    [Fact]
    public void The_memory_notes_ask_for_a_json_object_and_the_chat_does_not()
    {
        ChatMessage[] messages = [new(ChatRole.User, "Hi")];
        using var json = JsonDocument.Parse(LlamaCppLanguageModel.BuildRequestBody(messages, new GenerationOptions { Json = true }, 1));
        Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        using var chat = JsonDocument.Parse(LlamaCppLanguageModel.BuildRequestBody(messages, new GenerationOptions(), 1));
        Assert.False(chat.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void Sse_done_marker()
    {
        Assert.True(SseParser.Parse("data: [DONE]")!.Done);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/health", true)]
    [InlineData("http://localhost:1/x", true)]
    [InlineData("http://[::1]:1/x", true)]
    [InlineData("https://api.openai.com/v1/chat", false)]
    [InlineData("http://192.168.1.10:8080/", false)]
    public void Loopback_guard(string url, bool allowed) =>
        Assert.Equal(allowed, LoopbackOnlyHandler.IsLoopback(new Uri(url)));

    [Fact]
    public async Task Loopback_handler_blocks_remote_requests()
    {
        using var client = new HttpClient(new LoopbackOnlyHandler());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.com/"));
        Assert.Contains("non-loopback", ex.Message, StringComparison.Ordinal);
    }
}
