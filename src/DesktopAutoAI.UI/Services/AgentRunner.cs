using System.IO;
using System.Runtime.Versioning;
using DesktopAutoAI.Agent;
using DesktopAutoAI.Configuration;
using DesktopAutoAI.Providers;
using DesktopAutoAI.Safety;
using DesktopAutoAI.Skills;
using DesktopAutoAI.Uia;
using Serilog;

namespace DesktopAutoAI.UI.Services;

public sealed record RunOptions(
    string ProcessName,
    string Goal,
    bool UseCache,
    bool UseSafety,
    bool AutoYes,
    bool IncludeScreenshot = true,
    int? MaxStepsOverride = null,
    string? OutDirOverride = null);

public sealed record RunSummary(LoopOutcome? Outcome, int Steps, string Message, string? OutDir);

/// <summary>
/// One-prompt-one-run wrapper. The WPF MainWindow constructs a fresh
/// AgentRunner each time the user clicks Run, and disposes it when the run
/// finishes - so a long-running app session doesn't keep a UIA handle open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentRunner
{
    private readonly AppSettings _settings;

    public AgentRunner(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<RunSummary> RunAsync(RunOptions opts, CancellationToken externalCt)
    {
        var planner = PlannerFactory.Create(_settings.Planner, treeOnly: !opts.IncludeScreenshot);
        Log.Information("Planner: {Provider} / {Model} (screenshot={ScreenshotState})",
            planner.ProviderName, planner.ModelId,
            opts.IncludeScreenshot ? "on" : "off");

        Log.Information("Attaching to process {Process}", opts.ProcessName);
        using var session = UiaSession.Attach(opts.ProcessName);
        Log.Information("Attached. Window title: {Title}", session.TargetWindow.Title);

        var outDir = opts.OutDirOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "DesktopAutoAI", "runs",
                            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);

        var maxSteps = opts.MaxStepsOverride ?? _settings.Planner.MaxSteps;
        var cache = new SkillsCache();

        // Build the safety pieces (same wiring as the CLI's RunCommand).
        var safetyOn = _settings.Safety.Enabled && opts.UseSafety;
        KillSwitch? killSwitch = null;
        if (_settings.Safety.KillSwitchEnabled && opts.UseSafety)
        {
            try { killSwitch = new KillSwitch(_settings.Safety.KillSwitchHotkey); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not start kill switch ({Hotkey}); continuing without it.",
                    _settings.Safety.KillSwitchHotkey);
            }
        }

        SafetyGate? safetyGate = null;
        if (safetyOn)
        {
            var detector = new DestructiveDetector(_settings.Safety.DestructivePatterns);
            var prompt = new ConfirmationPrompt(
                TimeSpan.FromSeconds(_settings.Safety.ConfirmationTimeoutSeconds),
                autoConfirm: opts.AutoYes);
            safetyGate = new SafetyGate(detector, prompt, enabled: true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            externalCt, killSwitch?.Token ?? CancellationToken.None);
        var ct = linked.Token;

        Log.Information("Run config: max_steps={MaxSteps}, out_dir={OutDir}, cache={CacheState}, safety={SafetyState}, kill_switch={KillSwitch}",
            maxSteps, outDir,
            opts.UseCache ? cache.Directory : "disabled",
            safetyGate is null ? "disabled" : (opts.AutoYes ? "auto-confirm" : "prompt"),
            killSwitch?.Hotkey ?? "disabled");

        try
        {
            if (opts.UseCache)
            {
                var skill = cache.TryLoad(opts.ProcessName, opts.Goal);
                if (skill is not null && skill.Actions.Count > 0)
                {
                    Log.Information("Skills cache HIT ({Count} actions, {Successes} prior runs). Replaying.",
                        skill.Actions.Count, skill.SuccessCount);
                    var replayer = new SkillReplayer(
                        new ActionExecutor(session.TargetWindow, treeOnly: !opts.IncludeScreenshot),
                        safetyGate);
                    var replay = await replayer.ReplayAsync(skill.Actions, ct);
                    if (replay.Succeeded)
                    {
                        cache.Save(opts.ProcessName, opts.Goal, skill.Actions);
                        return new RunSummary(LoopOutcome.Done, replay.StepsRun,
                            "Replayed from cache: " + replay.Message, outDir);
                    }
                    if (replay.Denied)
                        return new RunSummary(LoopOutcome.Denied, replay.StepsRun, replay.Message, outDir);
                    Log.Information("Replay failed at step {Step}: {Msg}. Falling back to live planning.",
                        replay.StepsRun + 1, replay.Message);
                }
                else
                {
                    Log.Information("Skills cache MISS. Running live planner.");
                }
            }

            var loop = new PlanningLoop(planner, session, maxSteps, screenshotMaxEdge: 1280, outDir, safetyGate,
                historyWindow: _settings.Planner.HistoryWindow,
                includeScreenshot: opts.IncludeScreenshot);
            var result = await loop.RunAsync(opts.Goal, ct);

            if (result.Outcome == LoopOutcome.Done && opts.UseCache && !result.ContainsPasswordFields)
            {
                var winning = result.History
                    .Where(h => h.Result.StartsWith("ok", StringComparison.OrdinalIgnoreCase)
                             || h.Result.StartsWith("done", StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Action)
                    .ToList();
                cache.Save(opts.ProcessName, opts.Goal, winning);
            }
            else if (result.ContainsPasswordFields)
            {
                Log.Warning("Run typed into a password field; skipping cache save.");
            }

            return new RunSummary(result.Outcome, result.StepsTaken, result.Message, outDir);
        }
        finally
        {
            killSwitch?.Dispose();
        }
    }
}
