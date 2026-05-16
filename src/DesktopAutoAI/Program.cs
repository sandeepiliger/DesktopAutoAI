using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Agent;
using DesktopAutoAI.Configuration;
using DesktopAutoAI.Logging;
using DesktopAutoAI.Providers;
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
                ["step", .. var rest] => StepCommand(rest, settings).GetAwaiter().GetResult(),
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

    private static async Task<int> StepCommand(string[] args, AppSettings settings)
    {
        string? process = null;
        string? goal = null;
        string outDir = ".\\out";
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--process" when i + 1 < args.Length:
                    process = args[++i]; break;
                case "--goal" when i + 1 < args.Length:
                    goal = args[++i]; break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i]; break;
                case "--dry-run":
                    dryRun = true; break;
                default:
                    Log.Error("Unknown or incomplete argument: {Arg}", args[i]);
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(process) || string.IsNullOrWhiteSpace(goal))
        {
            Log.Error(
                "--process and --goal are required. Example: " +
                "step --process Notepad --goal \"Open the File menu\"");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        IActionPlanner planner;
        try
        {
            planner = PlannerFactory.Create(settings.Planner);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create planner");
            return 1;
        }
        Log.Information("Planner: {Provider} / {Model}", planner.ProviderName, planner.ModelId);

        Log.Information("Attaching to process {Process}", process);
        using var session = UiaSession.Attach(process);
        Log.Information("Attached. Window title: {Title}", session.TargetWindow.Title);

        var tree = UiaTreeDumper.Dump(session.TargetWindow);
        var treeJson = JsonSerializer.Serialize(tree, JsonOpts);
        File.WriteAllText(Path.Combine(outDir, "tree.json"), treeJson);

        var rect = session.TargetWindow.BoundingRectangle;
        var shotPath = Path.Combine(outDir, "shot.png");
        ScreenCapture.CapturePng(rect, shotPath);
        var shotBytes = File.ReadAllBytes(shotPath);

        var request = new PlanRequest(
            Goal: goal!,
            FilteredTreeJson: treeJson,
            ScreenshotPng: shotBytes,
            History: Array.Empty<HistoryEntry>());

        Log.Information("Asking planner for next action...");
        PlannedAction planned;
        try
        {
            planned = await planner.PlanNextAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Planner call failed");
            return 1;
        }

        File.WriteAllText(
            Path.Combine(outDir, "action.json"),
            JsonSerializer.Serialize(planned, JsonOpts));

        Log.Information("Thought: {Thought}", planned.Thought);
        Log.Information("Action : {Type}{Destructive}",
            planned.Action.Type,
            planned.IsDestructive ? " [destructive!]" : "");

        if (dryRun)
        {
            Log.Information("--dry-run: not executing.");
            return 0;
        }

        var executor = new ActionExecutor(session.TargetWindow);
        var result = executor.Execute(planned.Action, CancellationToken.None);
        Log.Information("Result : {Status} - {Message}", result.Status, result.Message);

        return result.Status == ExecutionStatus.Failed ? 2 : 0;
    }

    private static int PrintHelp(int exitCode)
    {
        Console.WriteLine("""
            DesktopAutoAI - Windows UIA automation agent

            Commands:
              dump --process <name> [--out <dir>]
                Dump the filtered UIA tree + a screenshot of the target app's
                foreground window. <name> is the process name without .exe.

              step --process <name> --goal "..." [--out <dir>] [--dry-run]
                Run ONE planning turn: dump tree + screenshot, ask the configured
                LLM for the next action, then execute it via UIA. Requires
                ANTHROPIC_API_KEY (or the provider's key) in the environment.
                --dry-run prints the action without executing.

            Examples:
              DesktopAutoAI.exe dump --process Notepad --out .\out
              DesktopAutoAI.exe step --process Notepad --goal "Open the File menu"
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
