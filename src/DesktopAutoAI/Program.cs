using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Configuration;
using DesktopAutoAI.Logging;
using DesktopAutoAI.Uia;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace DesktopAutoAI;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        EnableDpiAwareness();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        var settings = config.Get<AppSettings>() ?? new AppSettings();
        SerilogSetup.Initialize(settings);

        try
        {
            return args switch
            {
                [] => PrintHelp(0),
                ["help"] or ["-h"] or ["--help"] => PrintHelp(0),
                ["dump", .. var rest] => DumpCommand(rest),
                _ => PrintHelp(1),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled error");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int DumpCommand(string[] args)
    {
        string? process = null;
        string outDir = ".\\out";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--process" when i + 1 < args.Length:
                    process = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                default:
                    Log.Error("Unknown or incomplete argument: {Arg}", args[i]);
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(process))
        {
            Log.Error("--process is required. Example: dump --process Notepad --out .\\out");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        Log.Information("Attaching to process {Process}", process);
        using var session = UiaSession.Attach(process);
        Log.Information("Attached. Window title: {Title}", session.TargetWindow.Title);

        var tree = UiaTreeDumper.Dump(session.TargetWindow);
        var treePath = Path.Combine(outDir, "tree.json");
        File.WriteAllText(treePath, JsonSerializer.Serialize(tree, JsonOpts));
        Log.Information("Wrote filtered UIA tree to {Path}", Path.GetFullPath(treePath));

        var rect = session.TargetWindow.BoundingRectangle;
        var shotPath = Path.Combine(outDir, "shot.png");
        ScreenCapture.CapturePng(rect, shotPath);
        Log.Information("Wrote screenshot to {Path} ({W}x{H})",
            Path.GetFullPath(shotPath), rect.Width, rect.Height);

        return 0;
    }

    private static int PrintHelp(int exitCode)
    {
        Console.WriteLine("""
            DesktopAutoAI - Windows UIA automation agent

            Commands:
              dump --process <name> [--out <dir>]
                Dump the filtered UIA tree + a screenshot of the target app's
                foreground window. <name> is the process name without .exe.

            Examples:
              DesktopAutoAI.exe dump --process Notepad --out .\out
            """);
        return exitCode;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    private static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older Windows - best effort */ }
    }
}
