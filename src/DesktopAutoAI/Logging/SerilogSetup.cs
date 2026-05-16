using DesktopAutoAI.Configuration;
using Serilog;
using Serilog.Events;

namespace DesktopAutoAI.Logging;

public static class SerilogSetup
{
    public static void Initialize(AppSettings settings)
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

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(dir, "auto-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Logging initialized. Directory: {Dir}, Level: {Level}", dir, level);
    }
}
