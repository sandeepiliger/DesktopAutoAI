using System.Windows;
using DesktopAutoAI.Configuration;
using DesktopAutoAI.Logging;
using DesktopAutoAI.UI.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace DesktopAutoAI.UI;

public partial class App : Application
{
    /// <summary>Process-wide settings, loaded once at startup.</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Sink shared with MainWindow so log lines stream into the UI.</summary>
    public static UiLogSink LogSink { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        Settings = config.Get<AppSettings>() ?? new AppSettings();

        // WPF host has no console window - skip the console sink, keep file + UI.
        SerilogSetup.Initialize(Settings, extraSink: LogSink, includeConsole: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
