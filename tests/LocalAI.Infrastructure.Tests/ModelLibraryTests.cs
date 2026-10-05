using LocalAI.Tests;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using ICSharpCode.SharpZipLib.BZip2;
using LocalAI.Configuration;
using LocalAI.Models;

namespace LocalAI.Infrastructure.Tests;

public sealed class ModelLibraryTests : IDisposable
{
    private const string LlmUrl = "https://huggingface.co/org/repo/resolve/main/model.gguf";
    private const string VoiceUrl = "https://github.com/org/voices/releases/download/v1/voice.tar.bz2";

    private readonly LocalAiPaths _paths = new(TestPaths.New("lib"));
    private readonly FakeServer _server = new();
    private readonly byte[] _model = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
    private readonly ModelLibrary _library;

    public ModelLibraryTests()
    {
        _server.Files[LlmUrl] = _model;
        _server.Files[VoiceUrl] = CreateVoiceArchive();
        var catalog = new ModelCatalog
        {
            Llm =
            [
                new LlmCatalogEntry { Id = "hf", DisplayName = "HF", File = "llm/model.gguf", Url = LlmUrl, SizeBytes = _model.Length },
                new LlmCatalogEntry { Id = "elsewhere", DisplayName = "X", File = "llm/x.gguf", Url = "https://example.com/x.gguf" },
            ],
            Tts = [new VoiceCatalogEntry { Id = "voice", Language = "pt", Dir = "tts/voice", Url = VoiceUrl, SizeBytes = _server.Files[VoiceUrl].Length }],
        };
        _library = new ModelLibrary(catalog, _paths, new ModelDownloader(_server));
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Home)) Directory.Delete(_paths.Home, true);
    }

    private ModelPackage Llm => _library.Packages.Single(p => p.Kind == ModelKind.Llm);
    private ModelPackage Voice => _library.Packages.Single(p => p.Kind == ModelKind.Voice);

    [Fact]
    public void Only_hugging_face_language_models_are_offered()
    {
        Assert.Equal("hf", Llm.Id);
        Assert.True(ModelLibrary.IsHuggingFace(new Uri(LlmUrl)));
        Assert.False(ModelLibrary.IsHuggingFace(new Uri("http://huggingface.co/a")));
        Assert.False(ModelLibrary.IsHuggingFace(new Uri("https://huggingface.co.evil.com/a")));
    }

    [Fact]
    public async Task Installs_a_file_into_the_models_folder_and_cleans_up()
    {
        var reports = new List<double>();
        await _library.InstallAsync(Llm, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.True(_library.IsInstalled(Llm));
        Assert.Equal(_model, await File.ReadAllBytesAsync(Path.Combine(_paths.ModelsDirectory, "llm", "model.gguf")));
        Assert.Equal(1, reports[^1]);
        Assert.Empty(Directory.GetFileSystemEntries(_paths.DownloadsDirectory));
    }

    [Fact]
    public async Task Resumes_an_interrupted_download()
    {
        var staging = Path.Combine(_paths.DownloadsDirectory, Llm.Id);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "model.gguf.partial"), _model[..100_000]);

        await _library.InstallAsync(Llm, null, CancellationToken.None);

        Assert.Equal(100_000, _server.LastRangeStart);
        Assert.Equal(_model, await File.ReadAllBytesAsync(Llm.InstallPath));
    }

    [Fact]
    public async Task Incomplete_download_is_rejected()
    {
        _server.Files[LlmUrl] = _model[..1000];
        await Assert.ThrowsAsync<InvalidDataException>(() => _library.InstallAsync(Llm, null, CancellationToken.None));
        Assert.False(_library.IsInstalled(Llm));
    }

    [Fact]
    public async Task Extracts_voice_archives_and_uninstalls()
    {
        await _library.InstallAsync(Voice, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_paths.ModelsDirectory, "tts", "voice", "model.onnx")));
        Assert.True(File.Exists(Path.Combine(_paths.ModelsDirectory, "tts", "voice", "espeak-ng-data", "phontab")));

        _library.Uninstall(Voice);
        Assert.False(_library.IsInstalled(Voice));
        Assert.False(Directory.Exists(Voice.InstallPath));
    }

    private static byte[] CreateVoiceArchive()
    {
        using var output = new MemoryStream();
        using (var bzip = new BZip2OutputStream(output) { IsStreamOwner = false })
        using (var tar = new TarWriter(bzip))
        {
            foreach (var name in new[] { "vits-piper-voice/model.onnx", "vits-piper-voice/espeak-ng-data/phontab" })
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream([1, 2, 3]) });
        }
        return output.ToArray();
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = Files[request.RequestUri!.ToString()];
            LastRangeStart = request.Headers.Range?.Ranges.First().From;
            var start = (int)(LastRangeStart ?? 0);
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data[start..]),
            };
            if (start > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }
}
