using LocalAI.Tests;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalAI.Configuration;
using LocalAI.Models;

namespace LocalAI.Infrastructure.Tests;

public sealed class ModelLibraryTests : IDisposable
{
    private const string LlmUrl = "https://huggingface.co/org/repo/resolve/main/model.gguf";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "https://huggingface.co/api/models/org/voices/tree/" + Revision + "?recursive=true";
    private const string TreePage2 = Tree + "&cursor=2";
    private const string Resolve = "https://huggingface.co/org/voices/resolve/" + Revision + "/";

    private readonly LocalAiPaths _paths = new(TestPaths.New("lib"));
    private readonly FakeServer _server = new();
    private readonly byte[] _model = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
    private readonly ModelLibrary _library;

    public ModelLibraryTests()
    {
        _server.Files[LlmUrl] = _model;
        // The repository listing comes in two pages; lexicon.txt is not wanted.
        _server.Pages[Tree] = ("""
            [ { "type": "file", "path": "model.onnx", "size": 3 }, { "type": "directory", "path": "espeak-ng-data", "size": 0 },
              { "type": "file", "path": "lexicon.txt", "size": 2 } ]
            """, TreePage2);
        _server.Pages[TreePage2] = ("""[ { "type": "file", "path": "espeak-ng-data/voices/phontab", "size": 4 } ]""", null);
        _server.Files[Resolve + "model.onnx"] = [1, 2, 3];
        _server.Files[Resolve + "espeak-ng-data/voices/phontab"] = [4, 5, 6, 7];
        _library = new ModelLibrary(Catalog(), _paths, new ModelDownloader(_server));
    }

    private ModelCatalog Catalog() => new()
    {
        Llm =
        [
            new LlmCatalogEntry { Id = "hf", DisplayName = "HF", File = "llm/model.gguf", Url = LlmUrl, SizeBytes = _model.Length },
            new LlmCatalogEntry { Id = "elsewhere", DisplayName = "X", File = "llm/x.gguf", Url = "https://example.com/x.gguf" },
        ],
        Tts = new TtsCatalogEntry
        {
            Id = "voices", Dir = "tts/voices", Repo = "org/voices", Revision = Revision,
            Files = ["model.onnx", "espeak-ng-data/"], SizeBytes = 7,
        },
    };

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
    public async Task Downloads_the_wanted_files_of_a_hugging_face_repository_and_uninstalls()
    {
        var reports = new List<double>();
        await _library.InstallAsync(Voice, new SyncProgress(r => { lock (reports) reports.Add(r); }), CancellationToken.None);

        Assert.True(_library.IsInstalled(Voice));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_paths.ModelsDirectory, "tts", "voices", "model.onnx")));
        Assert.Equal([4, 5, 6, 7], await File.ReadAllBytesAsync(Path.Combine(_paths.ModelsDirectory, "tts", "voices", "espeak-ng-data", "voices", "phontab")));
        Assert.False(File.Exists(Path.Combine(Voice.InstallPath, "lexicon.txt")));
        Assert.Equal(1, reports.Max());
        Assert.Empty(Directory.GetFileSystemEntries(_paths.DownloadsDirectory));

        _library.Uninstall(Voice);
        Assert.False(_library.IsInstalled(Voice));
        Assert.False(Directory.Exists(Voice.InstallPath));
    }

    [Fact]
    public async Task A_failed_repository_download_leaves_nothing_installed()
    {
        _server.Files.Remove(Resolve + "espeak-ng-data/voices/phontab");
        await Assert.ThrowsAnyAsync<Exception>(() => _library.InstallAsync(Voice, null, CancellationToken.None));
        Assert.False(_library.IsInstalled(Voice));

        // The next try downloads only what is missing.
        _server.Files[Resolve + "espeak-ng-data/voices/phontab"] = [4, 5, 6, 7];
        _server.Requests.Clear();
        await _library.InstallAsync(Voice, null, CancellationToken.None);
        Assert.True(_library.IsInstalled(Voice));
        Assert.DoesNotContain(Resolve + "model.onnx", _server.Requests);
    }

    [Fact]
    public async Task A_file_the_catalog_names_must_be_in_the_repository()
    {
        _server.Pages[TreePage2] = ("[]", null);
        var library = new ModelLibrary(new ModelCatalog
        {
            Tts = new TtsCatalogEntry { Id = "voices", Dir = "tts/voices", Repo = "org/voices", Revision = Revision, Files = ["model.onnx", "voices.bin"] },
        }, _paths, new ModelDownloader(_server));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.InstallAsync(library.Packages.Single(), null, CancellationToken.None));
    }

    [Fact]
    public void Speech_models_no_longer_used_are_removed()
    {
        var tts = Path.Combine(_paths.ModelsDirectory, "tts");
        foreach (var dir in new[] { "voices", "vits-piper-pt_BR-faber-medium", "kokoro-multi-lang-v1_0" })
            Directory.CreateDirectory(Path.Combine(tts, dir, "espeak-ng-data"));

        Assert.Equal(["kokoro-multi-lang-v1_0", "vits-piper-pt_BR-faber-medium"], _library.RemoveObsoleteVoices().Order());
        Assert.Equal([Path.Combine(tts, "voices")], Directory.GetDirectories(tts));
    }

    [Fact]
    public async Task Downloads_asked_for_stay_pending_until_installed_or_cleared()
    {
        Assert.Empty(_library.PendingDownloads());
        _library.AddPending([Llm, Voice]);
        _library.AddPending([Voice]);
        Assert.Equal([Llm, Voice], _library.PendingDownloads());

        // Survives a restart of the app.
        var reopened = new ModelLibrary(Catalog(), _paths, new ModelDownloader(_server));
        Assert.Equal(["hf", "voices"], reopened.PendingDownloads().Select(p => p.Id));

        await _library.InstallAsync(Llm, null, CancellationToken.None);
        Assert.Equal([Voice], _library.PendingDownloads()); // installed ones are no longer pending

        _library.ClearPending();
        Assert.Empty(_library.PendingDownloads());
    }

    [Fact]
    public void An_interrupted_language_model_download_brings_the_rest_of_its_set()
    {
        // Left by a version without the pending list: only the staging folder tells the download was interrupted.
        Directory.CreateDirectory(Path.Combine(_paths.DownloadsDirectory, Llm.Id));
        Assert.Equal([Llm, Voice], _library.PendingDownloads());
    }

    [Fact]
    public async Task A_model_added_from_hugging_face_downloads_into_its_folder_and_leaves_the_list_when_removed()
    {
        const string rev = "1111111111111111111111111111111111111111";
        _server.Pages[$"https://huggingface.co/api/models/org/m-GGUF/tree/{rev}?recursive=true"] = ("""
            [ { "type": "file", "path": "Q4/m-Q4-00001-of-00002.gguf", "size": 2 }, { "type": "file", "path": "Q4/m-Q4-00002-of-00002.gguf", "size": 1 },
              { "type": "file", "path": "m-Q8.gguf", "size": 5 } ]
            """, null);
        _server.Files[$"https://huggingface.co/org/m-GGUF/resolve/{rev}/Q4/m-Q4-00001-of-00002.gguf"] = [1, 2];
        _server.Files[$"https://huggingface.co/org/m-GGUF/resolve/{rev}/Q4/m-Q4-00002-of-00002.gguf"] = [3];
        var entry = new LlmCatalogEntry
        {
            Id = "hf-org--m-gguf-q4", DisplayName = "m-GGUF · Q4", Dir = "llm/hf/org--m-gguf/q4", File = "llm/hf/org--m-gguf/q4/Q4/m-Q4-00001-of-00002.gguf",
            Repo = "org/m-GGUF", Revision = rev, Files = ["Q4/m-Q4-00001-of-00002.gguf", "Q4/m-Q4-00002-of-00002.gguf"], SizeBytes = 3,
        };

        var package = _library.AddLanguageModel(entry);
        Assert.Contains(_library.Packages, p => p.Id == entry.Id && p.IsAdded);
        await _library.InstallAsync(package, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_paths.ModelsDirectory, entry.File)));
        Assert.False(File.Exists(Path.Combine(package.InstallPath, "m-Q8.gguf")));
        Assert.Contains(new ModelLibrary(Catalog(), _paths, new ModelDownloader(_server)).Packages, p => p.Id == entry.Id); // kept

        _library.Uninstall(package);
        Assert.DoesNotContain(_library.Packages, p => p.Id == entry.Id);
        Assert.False(Directory.Exists(Path.Combine(_paths.ModelsDirectory, "llm", "hf")));
    }

    [Fact]
    public void Only_hugging_face_repositories_can_be_added()
    {
        var entry = new LlmCatalogEntry { Id = "x", DisplayName = "X", File = "llm/x.gguf", Repo = "../evil", Files = ["x.gguf"], Dir = "llm/x" };
        Assert.Throws<ArgumentException>(() => _library.AddLanguageModel(entry));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        /// <summary>API pages: the JSON body and the next page's address (sent in a Link header).</summary>
        public Dictionary<string, (string Body, string? Next)> Pages { get; } = [];
        public List<string> Requests { get; } = [];
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            if (Pages.TryGetValue(url, out var page))
            {
                var listing = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Body, Encoding.UTF8, "application/json") };
                if (page.Next != null) listing.Headers.TryAddWithoutValidation("Link", $"<{page.Next}>; rel=\"next\"");
                return Task.FromResult(listing);
            }
            if (!Files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
