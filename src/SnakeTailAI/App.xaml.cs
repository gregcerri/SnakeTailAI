using System.IO;
using System.Windows;
using System.Windows.Threading;
using SnakeTailAI.Core;
using SnakeTailAI.Services;
using SnakeTailAI.ViewModels;

namespace SnakeTailAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Started by ssh to supply the saved password: answer and leave, without any window.
        if (SshLauncher.TryHandleAskPass(e.Args))
        {
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;

        var settings = SettingsStore.Load();
        ThemeService.Initialize();
        var vm = new MainViewModel(settings);
        var window = new MainWindow(vm);
        MainWindow = window;
        // Runs after the window's own Closing handler, which saves the session.
        window.Closing += (_, _) => _closing = true;
        ThemeService.Apply(settings.Theme, window);
        window.Show();

        vm.RestoreSession();
        foreach (string arg in e.Args)
        {
            if (File.Exists(arg))
                vm.Open(arg);
        }
    }

    /// <summary>The main window is closed: session and settings are already saved.</summary>
    private bool _closing;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        if (_closing || Dispatcher.HasShutdownStarted)
        {
            // An error while shutting down (e.g. the .NET runtime was updated while the app ran) must not leave
            // a process without a window behind: everything is saved, so just end it.
            Environment.Exit(0);
        }

        // Otherwise keep the viewer running: a log tool must not die because of one bad line or a busy file.
        e.Handled = true;
        (MainWindow as MainWindow)?.ShowToast("Unexpected error: " + e.Exception.Message, isError: true);
    }

    private static void LogError(Exception exception)
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsStore.DefaultPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
