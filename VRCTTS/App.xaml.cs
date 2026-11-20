using System.Windows;
using VRCTTS.Services;

namespace VRCTTS;

public partial class App : Application
{
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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
