using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Agent;
using DesktopAutoAI.Configuration;
using DesktopAutoAI.Logging;
using DesktopAutoAI.Providers;
using DesktopAutoAI.Safety;
using DesktopAutoAI.Skills;
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
                ["ping", .. var rest] => PingCommand(rest, settings).GetAwaiter().GetResult(),
                ["run",  .. var rest] => RunCommand(rest, settings).GetAwaiter().GetResult(),
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
        bool noScreenshot = false;

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
                case "--no-screenshot":
                    noScreenshot = true; break;
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
            planner = PlannerFactory.Create(settings.Planner, treeOnly: noScreenshot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create planner");
            return 1;
        }
        Log.Information("Planner: {Provider} / {Model} (screenshot={ScreenshotState})",
            planner.ProviderName, planner.ModelId, noScreenshot ? "off" : "on");

        Log.Information("Attaching to process {Process}", process);
        using var session = UiaSession.Attach(process);
        Log.Information("Attached. Window title: {Title}", session.TargetWindow.Title);

        var tree = UiaTreeDumper.Dump(session.TargetWindow);
        // Indented for the artifact file; compact for the LLM payload.
        File.WriteAllText(Path.Combine(outDir, "tree.json"), JsonSerializer.Serialize(tree, JsonOpts));
        var treeJson = JsonSerializer.Serialize(tree, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        byte[]? shotBytes = null;
        if (!noScreenshot)
        {
            var rect = session.TargetWindow.BoundingRectangle;
            var shotPath = Path.Combine(outDir, "shot.png");
            ScreenCapture.CapturePng(rect, shotPath);
            shotBytes = ScreenCapture.ReadAndDownscalePng(shotPath, maxEdge: 1280);
            Log.Information("Screenshot: {W}x{H} on disk, {Kb} KB to LLM (downscaled to 1280px max edge)",
                rect.Width, rect.Height, shotBytes.Length / 1024);
        }
        else
        {
            Log.Information("Screenshot disabled (--no-screenshot). Sending UIA tree only.");
        }

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

        var executor = new ActionExecutor(session.TargetWindow, treeOnly: noScreenshot);
        var result = executor.Execute(planned.Action, CancellationToken.None);
        Log.Information("Result : {Status} - {Message}", result.Status, result.Message);

        return result.Status == ExecutionStatus.Failed ? 2 : 0;
    }

    private static async Task<int> RunCommand(string[] args, AppSettings settings)
    {
        string? process = null;
        string? goal = null;
        string outDir = ".\\out";
        int? maxStepsOverride = null;
        bool noCache = false;
        bool noSafety = false;
        bool autoYes = false;
        bool noScreenshot = false;
        bool batch = string.Equals(settings.Planner.Strategy, PlannerStrategies.Batch, StringComparison.OrdinalIgnoreCase);

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
                case "--max-steps" when i + 1 < args.Length:
                    maxStepsOverride = int.Parse(args[++i]); break;
                case "--no-cache":
                    noCache = true; break;
                case "--no-safety":
                    noSafety = true; break;
                case "--yes":
                    autoYes = true; break;
                case "--no-screenshot":
                    noScreenshot = true; break;
                case "--batch":
                    batch = true; break;
                case "--loop":
                    batch = false; break;
                default:
                    Log.Error("Unknown or incomplete argument: {Arg}", args[i]);
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(process) || string.IsNullOrWhiteSpace(goal))
        {
            Log.Error(
                "--process and --goal are required. Example: " +
                "run --process Notepad --goal \"Type hello and save as test.txt\"");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        IActionPlanner planner;
        try { planner = PlannerFactory.Create(settings.Planner, treeOnly: batch || noScreenshot, batchMode: batch); }
        catch (Exception ex) { Log.Error(ex, "Failed to create planner"); return 1; }
        Log.Information("Planner: {Provider} / {Model} (strategy={Strategy}, screenshot={ScreenshotState})",
            planner.ProviderName, planner.ModelId,
            batch ? "batch" : "loop",
            batch ? "off (batch is tree-only)" : (noScreenshot ? "off" : "on"));

        Log.Information("Attaching to process {Process}", process);
        using var session = UiaSession.Attach(process);
        Log.Information("Attached. Window title: {Title}", session.TargetWindow.Title);

        var maxSteps = maxStepsOverride ?? settings.Planner.MaxSteps;
        var cache = new SkillsCache();

        // Build the safety pieces. Kill switch always runs (unless disabled
        // in config or via --no-safety); destructive detector + prompt
        // respect both the master Safety.Enabled flag and --no-safety.
        var safetyOn = settings.Safety.Enabled && !noSafety;
        KillSwitch? killSwitch = null;
        if (settings.Safety.KillSwitchEnabled && !noSafety)
        {
            try { killSwitch = new KillSwitch(settings.Safety.KillSwitchHotkey); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not start kill switch ({Hotkey}); continuing without it.",
                    settings.Safety.KillSwitchHotkey);
            }
        }

        SafetyGate? safetyGate = null;
        if (safetyOn)
        {
            var detector = new DestructiveDetector(settings.Safety.DestructivePatterns);
            var prompt = new ConfirmationPrompt(
                TimeSpan.FromSeconds(settings.Safety.ConfirmationTimeoutSeconds),
                autoConfirm: autoYes);
            safetyGate = new SafetyGate(detector, prompt, enabled: true);
        }

        var ct = killSwitch?.Token ?? CancellationToken.None;

        Log.Information(
            "Loop config: max_steps={MaxSteps}, out_dir={OutDir}, cache={CacheState}, safety={SafetyState}, kill_switch={KillSwitch}",
            maxSteps, Path.GetFullPath(outDir),
            noCache ? "disabled" : cache.Directory,
            safetyGate is null ? "disabled" : (autoYes ? "auto-confirm" : "prompt"),
            killSwitch?.Hotkey ?? "disabled");

        try
        {
            // Batch strategy: plan-once + verify-and-repair (one LLM call per task).
            if (batch)
                return await RunBatchCommand(planner, session, cache, safetyGate, ct,
                    process!, goal!, outDir, noCache, settings.Planner.MaxRepairs, JsonOpts);

            // 1. Try a cached skill first (skipped when --no-cache).
            if (!noCache)
            {
                var skill = cache.TryLoad(process!, goal!);
                if (skill is not null && skill.Actions.Count > 0)
                {
                    Log.Information("Skills cache HIT for goal '{Goal}' ({Count} action(s), {Successes} prior run(s)). Replaying.",
                        goal, skill.Actions.Count, skill.SuccessCount);

                    var replayer = new SkillReplayer(
                        new ActionExecutor(session.TargetWindow, treeOnly: noScreenshot),
                        safetyGate);
                    ReplayResult replay;
                    try
                    {
                        replay = await replayer.ReplayAsync(skill.Actions, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Warning("Replay cancelled.");
                        return 130;
                    }

                    if (replay.Succeeded)
                    {
                        cache.Save(process!, goal!, skill.Actions);
                        File.WriteAllText(
                            Path.Combine(outDir, "history.json"),
                            JsonSerializer.Serialize(new
                            {
                                outcome = "ReplayedFromCache",
                                steps = replay.StepsRun,
                                message = replay.Message,
                                cache_key = skill.CacheKey,
                            }, JsonOpts));
                        Log.Information("Replay finished cleanly in {Steps} step(s). No LLM call made.", replay.StepsRun);
                        return 0;
                    }

                    if (replay.Denied)
                    {
                        Log.Warning("Replay aborted by user denial; not falling back to live planner.");
                        return 5;
                    }

                    Log.Information("Replay failed at step {Step}: {Msg}. Falling back to live planning.",
                        replay.StepsRun + 1, replay.Message);
                }
                else
                {
                    Log.Information("Skills cache MISS for goal '{Goal}'. Running live planner.", goal);
                }
            }

            // 2. Live planning loop (M3 + M5 safety gate).
            var loop = new PlanningLoop(planner, session, maxSteps, screenshotMaxEdge: 1280, outDir, safetyGate,
                historyWindow: settings.Planner.HistoryWindow,
                includeScreenshot: !noScreenshot);
            var result = await loop.RunAsync(goal!, ct);

            File.WriteAllText(
                Path.Combine(outDir, "history.json"),
                JsonSerializer.Serialize(new
                {
                    outcome = result.Outcome.ToString(),
                    steps = result.StepsTaken,
                    message = result.Message,
                    history = result.History,
                }, JsonOpts));

            // 3. On a successful live run, record the winning path so the next
            //    invocation with the same goal can replay it without an LLM call.
            //    Skip the save if any step typed into a password field - we
            //    don't want a redacted-or-real password sitting in the cache.
            if (result.Outcome == LoopOutcome.Done && !noCache)
            {
                if (result.ContainsPasswordFields)
                {
                    Log.Warning("Run typed into a password field; skipping cache save to avoid persisting credentials.");
                }
                else
                {
                    var winning = result.History
                        .Where(h => h.Result.StartsWith("ok", StringComparison.OrdinalIgnoreCase)
                                 || h.Result.StartsWith("done", StringComparison.OrdinalIgnoreCase))
                        .Select(h => h.Action)
                        .ToList();
                    cache.Save(process!, goal!, winning);
                }
            }

            return result.Outcome switch
            {
                LoopOutcome.Done => 0,
                LoopOutcome.Failed => 2,
                LoopOutcome.MaxSteps => 3,
                LoopOutcome.PlannerError => 4,
                LoopOutcome.Denied => 5,
                LoopOutcome.Cancelled => 130,
                _ => 1,
            };
        }
        finally
        {
            killSwitch?.Dispose();
        }
    }

    private static async Task<int> RunBatchCommand(
        IActionPlanner planner, UiaSession session, SkillsCache cache, SafetyGate? safetyGate,
        CancellationToken ct, string process, string goal, string outDir, bool noCache, int maxRepairs,
        JsonSerializerOptions jsonOpts)
    {
        var loop = new BatchPlanningLoop(planner, session, outDir, safetyGate, maxRepairs: maxRepairs);

        // 1. Self-verifying cache replay (skipped with --no-cache).
        if (!noCache)
        {
            var skill = cache.TryLoad(process, goal);
            if (skill?.Steps is { Count: > 0 } steps)
            {
                Log.Information("Skills cache HIT for goal '{Goal}' ({Count} verified step(s), {N} prior run(s)). Replaying.",
                    goal, steps.Count, skill.SuccessCount);
                BatchResult replay;
                try { replay = await loop.ReplayAsync(steps, ct); }
                catch (OperationCanceledException) { Log.Warning("Replay cancelled."); return 130; }

                if (replay.Outcome == BatchOutcome.Done)
                {
                    cache.SaveSteps(process, goal, replay.ExecutedPlan);
                    File.WriteAllText(Path.Combine(outDir, "history.json"),
                        JsonSerializer.Serialize(new
                        {
                            outcome = "ReplayedFromCache",
                            steps = replay.StepsExecuted,
                            message = replay.Message,
                            cache_key = skill.CacheKey,
                        }, jsonOpts));
                    Log.Information("Replay finished cleanly in {Steps} step(s). No LLM call made.", replay.StepsExecuted);
                    return 0;
                }
                if (replay.Outcome == BatchOutcome.Denied)
                {
                    Log.Warning("Replay aborted by user denial; not falling back.");
                    return 5;
                }
                Log.Information("Replay diverged at step {Step}: {Msg}. Falling back to live batch planning.",
                    replay.StepsExecuted + 1, replay.Message);
            }
            else
            {
                Log.Information("Skills cache MISS for goal '{Goal}' (no verified plan). Running live batch planner.", goal);
            }
        }

        // 2. Live batch planning (plan-once + verify-and-repair).
        BatchResult result;
        try { result = await loop.RunAsync(goal, ct); }
        catch (OperationCanceledException) { Log.Warning("Batch run cancelled."); return 130; }

        File.WriteAllText(Path.Combine(outDir, "history.json"),
            JsonSerializer.Serialize(new
            {
                outcome = result.Outcome.ToString(),
                steps = result.StepsExecuted,
                llm_calls = result.LlmCalls,
                repairs = result.Repairs,
                message = result.Message,
                plan = result.ExecutedPlan,
            }, jsonOpts));

        if (result.Outcome == BatchOutcome.Done && !noCache && !result.ContainsPasswordFields)
            cache.SaveSteps(process, goal, result.ExecutedPlan);
        else if (result.ContainsPasswordFields)
            Log.Warning("Batch run typed into a password field; skipping cache save to avoid persisting credentials.");

        return result.Outcome switch
        {
            BatchOutcome.Done => 0,
            BatchOutcome.Failed => 2,
            BatchOutcome.MaxRepairs => 3,
            BatchOutcome.PlannerError => 4,
            BatchOutcome.Denied => 5,
            BatchOutcome.Cancelled => 130,
            _ => 1,
        };
    }

    private static async Task<int> PingCommand(string[] args, AppSettings settings)
    {
        if (args.Length != 0)
        {
            Log.Error("Unknown argument: {Arg}. Usage: ping", args[0]);
            return 1;
        }

        IActionPlanner planner;
        try { planner = PlannerFactory.Create(settings.Planner); }
        catch (Exception ex) { Log.Error(ex, "Failed to create planner"); return 1; }
        Log.Information("Planner: {Provider} / {Model}", planner.ProviderName, planner.ModelId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await planner.PingAsync(CancellationToken.None);
            sw.Stop();
            Log.Information("Ping OK in {Ms}ms. Reply: {Reply}", sw.ElapsedMilliseconds, reply);
            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "Ping failed after {Ms}ms", sw.ElapsedMilliseconds);
            return 1;
        }
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
                   [--no-screenshot]
                Run ONE planning turn: dump tree + screenshot, ask the configured
                LLM for the next action, then execute it via UIA. Requires
                ANTHROPIC_API_KEY (or the provider's key) in the environment.
                --dry-run prints the action without executing.
                --no-screenshot skips the image attachment and runs the planner
                tree-only (cheaper, requires AutomationIds in the target app).

              run --process <name> --goal "..." [--out <dir>] [--max-steps N]
                  [--no-cache] [--no-safety] [--yes] [--no-screenshot]
                  [--batch | --loop]
                Multi-step planning loop. First checks the skills cache
                (%LOCALAPPDATA%\DesktopAutoAI\skills) for a previously successful
                action sequence for this (app, goal) pair - on hit it replays
                without any LLM call. On miss or replay failure it falls back to
                live planning, and saves the winning path on success. The run
                is gated by the safety layer: a global kill-switch hotkey
                (default Ctrl+Shift+Backspace) aborts immediately, and any
                destructive action (delete / send / submit / etc.) requires
                console confirmation before it executes.
                  --no-cache       Skip cache lookup and skip saving on success.
                  --no-safety      Disable both the kill switch and the
                                   destructive-action prompt for this run.
                  --yes            Auto-confirm every destructive prompt. Use
                                   only when you trust the goal and the model.
                  --no-screenshot  Don't send the screenshot to the planner;
                                   rely on the UIA tree alone. Coordinate
                                   clicks are refused in this mode. Best
                                   used with apps that expose AutomationIds.
                  --batch          Plan the WHOLE task in one LLM call, then
                                   execute deterministically and verify each
                                   step locally, re-planning only on
                                   divergence. ~1 LLM call per task instead of
                                   one per step: far faster, cheaper, and
                                   rate-limit-safe. Tree-only. Best for apps
                                   with AutomationIds. (Default follows
                                   Planner:Strategy in appsettings.json.)
                  --loop           Force the per-step live loop even when
                                   Planner:Strategy is "batch".
                Per-step artifacts (tree-NN.json, shot-NN.png, action-NN.json)
                land in <out>, plus a final history.json.

              ping
                Send a trivial "say pong" request to the configured planner.
                No tools, no image, no UIA. Use this to isolate connectivity /
                auth / model-id problems from payload / schema problems.

            Examples:
              DesktopAutoAI.exe dump --process Notepad --out .\out
              DesktopAutoAI.exe step --process Notepad --goal "Open the File menu"
              DesktopAutoAI.exe run  --process Notepad --goal "Type hello and save as test.txt"
              DesktopAutoAI.exe ping
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
