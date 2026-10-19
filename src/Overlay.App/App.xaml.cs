using System.IO;
using System.Windows;
using System.Windows.Threading;
using Overlay.Core.Settings;

namespace Overlay.App;

public partial class App : Application
{
    private AppController? controller;
    private Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnCrash;

        // One overlay at a time: a second copy would draw a second strip and fight over the hotkeys.
        singleInstance = new Mutex(true, @"Local\OsuTimelineOverlay", out bool first);
        if (!first)
        {
            MessageBox.Show("The osu! timeline overlay is already running. Look for its icon in the notification area.",
                "osu! timeline overlay", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        NativeMethods.timeBeginPeriod(1);
        controller = new AppController();
        MainWindow = controller.Control;
        if (!controller.Settings.StartInTray) controller.Control.Show();

        // Optional arguments: a .osu path to show, "--demo" for the simulated editor, and "--log <file>" to append frame stats every second.
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--log" && i + 1 < e.Args.Length) controller.StartStatsLog(e.Args[++i]);
            else if (e.Args[i] == "--trace" && i + 1 < e.Args.Length) controller.StartTrace(e.Args[++i]);
            else if (e.Args[i] == "--demo") controller.SetSource(TimeSourceKind.Demo);
            else if (File.Exists(e.Args[i])) controller.OpenFile(e.Args[i]);
        }
    }

    /// <summary>Writes the error next to the settings and tells the user, instead of vanishing silently.</summary>
    private void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string log = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "crash.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:u}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        MessageBox.Show($"The overlay hit an unexpected error and will close.\n\n{e.Exception.Message}\n\nDetails were saved to:\n{log}",
            "osu! timeline overlay", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        if (controller != null) NativeMethods.timeEndPeriod(1);
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
