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
        Assert.IsType<LocalAI.Memory.MemoryRetriever>(provider.GetRequiredService<IMemoryRetriever>());
        Assert.IsType<LlamaCppEmbeddingService>(provider.GetRequiredService<IEmbeddingService>());
        Assert.NotNull(provider.GetRequiredService<IAudioCapture>());
        Assert.NotNull(provider.GetRequiredService<AssistantSession>());
        Assert.NotNull(provider.GetRequiredService<VoiceConversationController>());
        Assert.Same(provider.GetRequiredService<ILanguageModel>(), provider.GetRequiredService<LlamaCppLanguageModel>());
        Assert.NotEmpty(provider.GetRequiredService<ModelService>().Packages);
    }

    [Fact]
    public async Task Models_and_voices_used_by_an_assistant_cannot_be_removed()
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
            new LocalAI.Core.Assistants.NewAssistant("Diana", "", llm.Id, voice.Id, voice.Language!), VoiceStyle.Default);

        var usage = await models.GetUsageAsync();
        Assert.Equal(["Diana"], usage[llm.Id]);
        Assert.Equal(["Diana"], usage[voice.Id]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => models.UninstallAsync(voice));
        Assert.Equal("Used by Diana.", ex.Message);
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
              "tts": [ { "id": "v", "language": "pt", "dir": "tts/v" } ] }
            """);
        Assert.Equal("a", Assert.Single(catalog.Llm).Id);
        Assert.Equal("pt", Assert.Single(catalog.Tts).Language);
        Assert.Empty(catalog.Whisper);
    }

    [Fact]
    public void Repository_catalog_is_valid()
    {
        var catalog = ModelCatalog.LoadDefault();
        Assert.NotEmpty(catalog.Llm);
        Assert.NotEmpty(catalog.Whisper);
        Assert.NotEmpty(catalog.Vad);
        Assert.Contains(catalog.Tts, v => v.Language == "pt");
        Assert.Contains(catalog.Tts, v => v.Language == "it");
        Assert.Contains(catalog.Tts, v => v.Language == "en");
        // Preference order: larger VRAM requirement first.
        Assert.Equal(catalog.Llm.OrderByDescending(l => l.MinVramMb).Select(l => l.Id), catalog.Llm.Select(l => l.Id));
        Assert.All(catalog.Llm, l => Assert.True(LocalAI.Models.ModelLibrary.IsHuggingFace(new Uri(l.Url)), l.Url));
        Assert.All(catalog.Llm, l => Assert.True(l.SizeBytes > 0));
        Assert.All(catalog.Tts, v => Assert.True(v.SizeBytes > 0));
        Assert.All(catalog.Whisper.Concat(catalog.Vad).Concat(catalog.Embedding), e => Assert.True(e.SizeBytes > 0));
        Assert.NotEmpty(catalog.Runtime.Urls);
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
        Assert.Contains("--reasoning off", joined, StringComparison.Ordinal);
        Assert.Contains("--load-mode none", joined, StringComparison.Ordinal);
        Assert.DoesNotContain(args, a => a.Contains("api-key", StringComparison.Ordinal)); // key goes via environment
    }

    [Fact]
    public void Embedding_server_arguments()
    {
        var args = LlamaServerProcess.BuildArguments(new LlamaServerStartInfo
        {
            ExecutablePath = "x", ModelPath = "e.gguf", Embedding = true, GpuLayers = 0,
        }, 1);
        Assert.Contains("--embedding", args);
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
