using Avalonia;
using Avalonia.Controls;
using LocalAI.App.Services;
using LocalAI.Configuration;

namespace LocalAI.App;

internal static class Program
{
    /// <summary>This process's claim to be the only running copy of the app in its folder.</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire(LocalAiPaths.ForApplication().Home);
        if (instance == null) return 0; // already running: it shows its window
        Instance = instance;
        // The app lives in the tray: closing the window hides it, only Exit (tray menu) quits.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
