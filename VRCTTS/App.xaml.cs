using System.Windows;
using VRCTTS.Services;

namespace VRCTTS;

public partial class App : Application
{
    public const string InstallerMutexName = @"Local\VRCTTS.Running";
    private Mutex? runningMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--setup-microphone", var nonce])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await DriverSetup.RunElevatedAsync(nonce); }
            catch { code = 1; }
            Shutdown(code);
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "VRCTTS", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        // Let setup/uninstall ask the user to close running instances instead of
        // replacing an executable or discarding an unsent draft automatically.
        runningMutex = new Mutex(initiallyOwned: false, InstallerMutexName);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runningMutex?.Dispose();
        base.OnExit(e);
    }
}
