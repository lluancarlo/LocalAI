using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LocalAI.App.ViewModels;
using LocalAI.App.Views;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Llm;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAI.App;

public sealed class App : Application
{
    private ServiceProvider? _services;
    private CancellationTokenSource? _startupCts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();
            var logger = _services.GetRequiredService<ILogger<App>>();
            InstallGlobalExceptionHandlers(logger);

            var vm = ActivatorUtilities.CreateInstance<MainWindowViewModel>(_services);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => Shutdown();

            _startupCts = new CancellationTokenSource();
            var startup = _services.GetRequiredService<StartupService>();
            _ = Task.Run(async () =>
            {
                await startup.StartAsync(_startupCts.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(vm.OnStartupCompleted);
            });
            _ = vm.LoadConversationsAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices()
    {
        var configuration = LocalAiHost.BuildConfiguration();
        var options = new LocalAiOptions();
        configuration.GetSection(LocalAiOptions.SectionName).Bind(options);
        var paths = new LocalAiPaths(options.Paths);
        LocalAiHost.ConfigureNativeSearchPath(paths);

        var services = new ServiceCollection();
        services.AddLogging(b => LocalAiHost.ConfigureLogging(b, configuration, paths.LogsDirectory));
        services.AddLocalAi(configuration, paths);
        services.AddSingleton<SettingsViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static void InstallGlobalExceptionHandlers(ILogger logger)
    {
        // A failure in one subsystem must not take down the app: log and keep running.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception");
    }

    private void Shutdown()
    {
        if (_services == null) return;
        _startupCts?.Cancel();
        _services.GetRequiredService<AssistantSession>().CancelCurrentTurn();
        // Stop voice and the engine child processes before the process exits (they are also in a kill-on-close job).
        Task.Run(async () =>
        {
            await _services.GetRequiredService<VoiceConversationController>().DisposeAsync();
            await _services.GetRequiredService<ILanguageModel>().UnloadAsync();
            await _services.DisposeAsync();
        }).Wait(TimeSpan.FromSeconds(10));
        _services = null;
    }
}
