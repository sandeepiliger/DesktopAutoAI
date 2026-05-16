namespace DesktopAutoAI.Configuration;

public sealed class AppSettings
{
    public LoggingSettings Logging { get; set; } = new();
}

public sealed class LoggingSettings
{
    public string? LogDirectory { get; set; }
    public string MinimumLevel { get; set; } = "Information";
}
