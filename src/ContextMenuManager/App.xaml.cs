using Microsoft.UI.Xaml;
using ContextMenuManager.Services;

namespace ContextMenuManager;

public partial class App : Application
{
    private Window? _window;

    // Lets the installer and uninstaller (Inno Setup AppMutex) see that the app is running.
    private readonly Mutex _runningMutex = new(false, UpdateProtocol.RunningMutexName);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write("Unhandled: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write($"start {BuildInfo.Version} {(UpdateChecker.IsInstalledCopy() ? "installed" : "portable")} from {AppContext.BaseDirectory}");
        _window = new MainWindow();
        _window.Activate();
    }
}
