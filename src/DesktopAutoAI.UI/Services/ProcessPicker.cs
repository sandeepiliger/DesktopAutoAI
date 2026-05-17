using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopAutoAI.UI.Services;

/// <summary>
/// Enumerates running processes that have a visible top-level window so the
/// user can pick a target from a dropdown. Also exposes a "wait for next
/// foreground window" helper so the user can minimise our UI and tab into
/// the app they want to drive.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessPicker
{
    public static IReadOnlyList<ProcessListItem> EnumerateWindowedProcesses()
    {
        int self = Environment.ProcessId;
        var items = new List<ProcessListItem>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                items.Add(new ProcessListItem(p.Id, p.ProcessName, p.MainWindowTitle));
            }
            catch { /* process may have exited mid-enumeration */ }
            finally { p.Dispose(); }
        }
        return items
            .OrderBy(i => i.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Poll the foreground window for up to <paramref name="timeout"/>; return
    /// the first one whose owning process is NOT us. Used by the "Use
    /// foreground window" button after our window is minimised.
    /// </summary>
    public static ProcessListItem? WaitForExternalForeground(TimeSpan timeout)
    {
        int self = Environment.ProcessId;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0 && (int)pid != self)
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                            return new ProcessListItem(p.Id, p.ProcessName, p.MainWindowTitle);
                    }
                    catch { /* ignore */ }
                }
            }
            Thread.Sleep(150);
        }
        return null;
    }
}
