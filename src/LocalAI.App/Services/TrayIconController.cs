using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LocalAI.Core.Voice;

namespace LocalAI.App.Services;

/// <summary>
/// The notification-area icon: always present while the app runs. Click or "Open" shows the window, "Live mode" turns
/// the live voice conversation on and off (checked while it is on), "Exit" quits.
/// While a live voice conversation is on, the icon blinks a green dot (the assistant is listening); while a shortcut
/// is starting one (switching assistant, loading its model), it shows a steady amber dot.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private const string AppName = "Local AI";
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(600);
    private static readonly Color LiveColor = Color.Parse("#9ECE6A");
    private static readonly Color StartingColor = Color.Parse("#E0AF68");

    private readonly LiveActivation _live;
    private readonly Func<(bool CanStart, bool IsLive)> _liveMode;
    private readonly NativeMenuItem _liveItem;
    private readonly TrayIcon _tray;
    private readonly WindowIcon _normalIcon;
    private readonly WindowIcon _liveIcon;
    private readonly WindowIcon _startingIcon;
    private readonly DispatcherTimer _blink;
    private bool _blinkLit;

    /// <param name="liveMode">Whether live mode can be turned on now (offered, microphone ready), and whether it is on.</param>
    /// <param name="toggleLive">Turns live mode on or off (runs on the UI thread).</param>
    public TrayIconController(Application application, LiveActivation live, Func<(bool CanStart, bool IsLive)> liveMode,
        Action toggleLive, Action showWindow, Action exit)
    {
        _live = live;
        _liveMode = liveMode;
        using (var baseImage = new Bitmap(OpenAppIcon()))
        {
            _liveIcon = WithDot(baseImage, LiveColor);
            _startingIcon = WithDot(baseImage, StartingColor);
        }
        _normalIcon = new WindowIcon(OpenAppIcon());

        var open = new NativeMenuItem("Open " + AppName);
        open.Click += (_, _) => showWindow();
        _liveItem = new NativeMenuItem("Live mode") { ToggleType = MenuItemToggleType.CheckBox };
        _liveItem.Click += (_, _) => toggleLive();
        var quit = new NativeMenuItem("Exit");
        quit.Click += (_, _) => exit();

        _tray = new TrayIcon
        {
            Icon = _normalIcon,
            ToolTipText = AppName,
            Menu = new NativeMenu { Items = { open, _liveItem, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(application, [_tray]);

        _blink = new DispatcherTimer { Interval = BlinkInterval };
        _blink.Tick += (_, _) =>
        {
            _blinkLit = !_blinkLit;
            _tray.Icon = _blinkLit ? _liveIcon : _normalIcon;
        };

        _live.StateChanged += OnLiveStateChanged;
        _live.Failed += OnLiveFailed;
        Update();
    }

    private void OnLiveStateChanged(object? sender, LiveState state) => Dispatcher.UIThread.Post(Update);

    private void OnLiveFailed(object? sender, string reason) =>
        Dispatcher.UIThread.Post(() => _tray.ToolTipText = $"{AppName}: could not start listening. {reason}");

    /// <summary>Refreshes the icon, its tooltip and the Live mode menu item. Call on the UI thread.</summary>
    public void Update()
    {
        var (canStart, isLive) = _liveMode();
        _liveItem.IsChecked = isLive;
        _liveItem.IsEnabled = isLive || canStart;

        var name = _live.Assistant?.Name ?? AppName;
        switch (_live.State)
        {
            case LiveState.Starting:
                _blink.Stop();
                _tray.Icon = _startingIcon;
                _tray.ToolTipText = $"{AppName}: {name} is getting ready…";
                break;
            case LiveState.Live:
                var stop = _live.Assistant?.Hotkey is { } hotkey ? $" (press {hotkey} to stop)" : "";
                _tray.ToolTipText = $"{AppName}: {name} is listening{stop}";
                _blinkLit = true;
                _tray.Icon = _liveIcon;
                _blink.Start();
                break;
            default:
                _blink.Stop();
                _tray.Icon = _normalIcon;
                _tray.ToolTipText = AppName;
                break;
        }
    }

    private static Stream OpenAppIcon() => AssetLoader.Open(new Uri("avares://LocalAI/Assets/app.ico"));

    /// <summary>The app icon with a status dot (dark outline, so it reads on light and dark taskbars) in the lower right corner.</summary>
    private static WindowIcon WithDot(Bitmap baseImage, Color color)
    {
        const int size = 32;
        var target = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(baseImage, new Rect(0, 0, size, size));
            context.DrawEllipse(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.Parse("#0E1015")), 2.5),
                new Point(size - 8.5, size - 8.5), 7, 7);
        }
        return new WindowIcon(target);
    }

    public void Dispose()
    {
        _live.StateChanged -= OnLiveStateChanged;
        _live.Failed -= OnLiveFailed;
        _blink.Stop();
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
