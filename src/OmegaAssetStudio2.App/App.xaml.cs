using Microsoft.UI.Xaml;
using OmegaAssetStudio2.App.Services;

namespace OmegaAssetStudio2.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // Crash paths only. v1 also subscribed FirstChanceException and wrote a
        // log line for every exception thrown anywhere in the process, which
        // turned into continuous disk I/O because the codebase throws-and-swallows
        // heavily in parse loops. That hook now lives behind a setting.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        => CrashLog.Write("UI.UnhandledException", e.Exception);

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        => CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
