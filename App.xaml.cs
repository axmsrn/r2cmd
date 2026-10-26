// File: App.xaml.cs | Path: App.xaml.cs | Version: 2026-10-19.2
using System.Windows;

namespace R2Cmd;

public partial class App : Application
{
    // Keep a reference to the Mutex so it doesn't get garbage collected
    private static Mutex? _instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // The worker performs file I/O only: no main window and no instance mutex.
        if (e.Args.Length > 0 && e.Args[0] == ElevatedFileOperations.WorkerArgument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try
            {
                Shutdown(await ElevatedFileOperations.RunWorkerAsync(e.Args));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot start the administrator file worker:\n{ex.Message}",
                    "Administrator operation", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(ex.HResult);
            }
            return;
        }

        const string mutexName = "R2Cmd_SingleInstance_Mutex_v1";

        // Try to create a new Mutex. If it already exists, createdNew will be false.
        _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running. Shut down this new one immediately.
            Current.Shutdown();
            return;
        }

        // Leave StartupUri unset in worker mode; WPF rejects an explicit null value.
        StartupUri = new Uri("Windows/MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Unexpected Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
