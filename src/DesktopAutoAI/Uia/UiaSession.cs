using System.Diagnostics;
using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace DesktopAutoAI.Uia;

[SupportedOSPlatform("windows")]
public sealed class UiaSession : IDisposable
{
    public UIA3Automation Automation { get; }
    public Application Application { get; }
    public Window TargetWindow { get; }

    private UiaSession(UIA3Automation automation, Application app, Window window)
    {
        Automation = automation;
        Application = app;
        TargetWindow = window;
    }

    public static UiaSession Attach(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("Process name is required.", nameof(processName));

        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0)
            throw new InvalidOperationException(
                $"No running process named '{processName}'. Start the target app first.");

        var process = procs[0];
        var automation = new UIA3Automation();
        var app = Application.Attach(process.Id);

        var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(5))
            ?? throw new InvalidOperationException(
                $"Could not find a main window for process '{processName}' (PID {process.Id}).");

        return new UiaSession(automation, app, main);
    }

    public void Dispose()
    {
        Automation.Dispose();
    }
}
