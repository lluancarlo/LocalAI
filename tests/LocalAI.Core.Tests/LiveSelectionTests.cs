using LocalAI.Configuration;
using LocalAI.Core.Desktop;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class LiveSelectionTests : IDisposable
{
    private readonly FakeSelectionMonitor _monitor = new();
    private readonly LocalAiOptions _options = new() { Voice = { ReadSelection = true } };
    private readonly LiveSelection _selection;

    public LiveSelectionTests() =>
        _selection = new LiveSelection(_monitor, new FixedMonitor(_options), NullLogger<LiveSelection>.Instance);

    public void Dispose() => _selection.Dispose();

    [Fact]
    public void Watches_only_while_live_mode_is_on_and_the_option_is_enabled()
    {
        Assert.False(_monitor.IsWatching);
        _selection.SetLive(true);
        Assert.True(_monitor.IsWatching);

        _options.Voice.ReadSelection = false;
        _selection.Refresh();
        Assert.False(_monitor.IsWatching);

        _options.Voice.ReadSelection = true;
        _selection.Refresh();
        Assert.True(_monitor.IsWatching);

        _selection.SetLive(false);
        Assert.False(_monitor.IsWatching);
    }

    [Fact]
    public void The_latest_selection_goes_with_the_next_message_only()
    {
        _selection.SetLive(true);
        _monitor.Select("first selection");
        _monitor.Select("  second selection \n");

        Assert.Equal("second selection", _selection.Take());
        Assert.Null(_selection.Take());
    }

    [Fact]
    public void Leaving_live_mode_forgets_the_pending_selection()
    {
        _selection.SetLive(true);
        _monitor.Select("something");
        var changes = 0;
        _selection.PendingChanged += (_, _) => changes++;

        _selection.SetLive(false);
        Assert.Null(_selection.Pending);
        Assert.Equal(1, changes);
        _selection.SetLive(true);
        Assert.Null(_selection.Take());
    }

    [Fact]
    public void Long_selections_are_cut()
    {
        _options.Voice.SelectionMaxChars = 10;
        _selection.SetLive(true);
        _monitor.Select("0123456789ABCDEF");
        Assert.Equal("0123456789…", _selection.Take());
    }

    [Fact]
    public void Nothing_is_watched_where_selections_cannot_be_read()
    {
        _monitor.IsSupported = false;
        _selection.SetLive(true);
        Assert.False(_monitor.IsWatching);
    }

    private sealed class FakeSelectionMonitor : ITextSelectionMonitor
    {
        private Action<string>? _selected;

        public bool IsSupported { get; set; } = true;
        public bool IsWatching => _selected != null;

        public IDisposable Start(Action<string> selected)
        {
            _selected = selected;
            return new Stop(() => _selected = null);
        }

        public void Select(string text) => _selected?.Invoke(text);

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private sealed class FixedMonitor(LocalAiOptions options) : Microsoft.Extensions.Options.IOptionsMonitor<LocalAiOptions>
    {
        public LocalAiOptions CurrentValue => options;
        public LocalAiOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<LocalAiOptions, string?> listener) => null;
    }
}
