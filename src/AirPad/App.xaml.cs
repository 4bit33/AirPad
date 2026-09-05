using System.Windows;
using AirPad.Core.Config;
using AirPad.Core.Connection;
using AirPad.Core.Logging;

namespace AirPad;

/// <summary>
/// Composition root: single Logger + SettingsStore + ConnectionManager
/// shared by all windows. No network logic lives in the UI layer.
/// </summary>
public partial class App : Application
{
    public AirPadLogger Logger { get; private set; } = null!;
    public SettingsStore SettingsStore { get; private set; } = null!;
    public ConnectionManager Manager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsStore = new SettingsStore();
        var initial = SettingsStore.Load();
        Logger = new AirPadLogger(minimumLevel: ParseLevel(initial.LogLevel));
        Manager = new ConnectionManager(Logger, SettingsStore);

        Logger.Info("AirPad starting");
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception: " + args.Exception, args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled exception", args.ExceptionObject as Exception);
        };
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Manager.IsRunning)
                await Manager.StopAsync();
        }
        catch { }
        try { Manager.Dispose(); } catch { }
        try { Logger.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static LogLevel ParseLevel(string level) => level switch
    {
        "Trace" => LogLevel.Trace,
        "Debug" => LogLevel.Debug,
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
