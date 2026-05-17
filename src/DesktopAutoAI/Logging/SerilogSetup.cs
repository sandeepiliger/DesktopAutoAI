using DesktopAutoAI.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DesktopAutoAI.Logging;

public static class SerilogSetup
{
    /// <summary>
    /// Configure the global Serilog logger from settings. Pass
    /// <paramref name="extraSink"/> when an additional sink should receive
    /// every event (e.g. the WPF UI's in-window log viewer). Pass
    /// <paramref name="includeConsole"/>=false when running under a UI host
    /// that has no console window.
    /// </summary>
    public static void Initialize(
        AppSettings settings,
        ILogEventSink? extraSink = null,
        bool includeConsole = true)
    {
        var dir = string.IsNullOrWhiteSpace(settings.Logging.LogDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopAutoAI", "logs")
            : settings.Logging.LogDirectory!;

        Directory.CreateDirectory(dir);

        var level = Enum.TryParse<LogEventLevel>(settings.Logging.MinimumLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(dir, "auto-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (includeConsole)
            config = config.WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (extraSink is not null)
            config = config.WriteTo.Sink(extraSink);

        Log.Logger = config.CreateLogger();

        Log.Information("Logging initialized. Directory: {Dir}, Level: {Level}", dir, level);
    }
}
