using System.Globalization;
using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace DesktopAutoAI.UI.Logging;

/// <summary>
/// Serilog sink that fans every event out to a single C# event. The WPF
/// MainWindow subscribes and dispatches to the UI thread.
/// </summary>
public sealed class UiLogSink : ILogEventSink
{
    private readonly MessageTemplateTextFormatter _formatter =
        new("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture);

    public event Action<LogEntry>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, sw);
        var text = sw.ToString().TrimEnd('\r', '\n');
        Emitted?.Invoke(new LogEntry(logEvent.Timestamp.DateTime, logEvent.Level, text));
    }
}

public sealed record LogEntry(DateTime Timestamp, LogEventLevel Level, string Text);
