using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Providers;
using DesktopAutoAI.Safety;
using DesktopAutoAI.Uia;
using FlaUI.Core.Exceptions;
using Serilog;

namespace DesktopAutoAI.Agent;

public enum LoopOutcome { Done, Failed, MaxSteps, PlannerError, Cancelled, Denied }

public sealed record LoopResult(
    LoopOutcome Outcome,
    int StepsTaken,
    string Message,
    IReadOnlyList<HistoryEntry> History,
    bool ContainsPasswordFields = false);

/// <summary>
/// Multi-step planning loop. Each step: re-capture tree + screenshot, ask the
/// planner for the next action, execute it, append a text summary to history,
/// repeat until the planner says <c>done</c>/<c>fail</c> or we hit
/// <c>maxSteps</c>. Screenshots are only sent for the current step - the rest
/// of the context is text - which keeps token usage bounded as the run grows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PlanningLoop
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IActionPlanner _planner;
    private readonly UiaSession _session;
    private readonly ActionExecutor _executor;
    private readonly SafetyGate? _safety;
    private readonly int _maxSteps;
    private readonly int _screenshotMaxEdge;
    private readonly string _outDir;
    private readonly int _postActionSettleMs;

    public PlanningLoop(
        IActionPlanner planner,
        UiaSession session,
        int maxSteps,
        int screenshotMaxEdge,
        string outDir,
        SafetyGate? safety = null,
        int postActionSettleMs = 150)
    {
        _planner = planner;
        _session = session;
        _executor = new ActionExecutor(session.TargetWindow);
        _safety = safety;
        _maxSteps = maxSteps > 0 ? maxSteps : 25;
        _screenshotMaxEdge = screenshotMaxEdge > 0 ? screenshotMaxEdge : 1280;
        _outDir = outDir;
        _postActionSettleMs = Math.Max(0, postActionSettleMs);
        Directory.CreateDirectory(_outDir);
    }

    public async Task<LoopResult> RunAsync(string goal, CancellationToken ct)
    {
        var history = new List<HistoryEntry>();
        var totalSw = Stopwatch.StartNew();
        var containsPassword = false;

        for (int step = 1; step <= _maxSteps; step++)
        {
            if (ct.IsCancellationRequested)
                return Finish(LoopOutcome.Cancelled, step - 1, "Cancelled by user.", history, totalSw);

            var (treeJson, shotBytes) = await CaptureWorldAsync(step, ct);

            Log.Information("--- step {Step}/{Max} ({Provider}/{Model}) ---",
                step, _maxSteps, _planner.ProviderName, _planner.ModelId);

            PlannedAction planned;
            try
            {
                planned = await _planner.PlanNextAsync(
                    new PlanRequest(goal, treeJson, shotBytes, history), ct);
            }
            catch (OperationCanceledException)
            {
                return Finish(LoopOutcome.Cancelled, step - 1, "Cancelled while waiting on planner.", history, totalSw);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Planner failed at step {Step}", step);
                return Finish(LoopOutcome.PlannerError, step - 1, ex.Message, history, totalSw, containsPassword);
            }

            var marker = planned.IsDestructive ? " [destructive!]" : "";
            Log.Information("step {Step}: {Type}{Marker} -- {Thought}",
                step, planned.Action.Type, marker, planned.Thought);

            if (_safety is not null)
            {
                bool allowed;
                try
                {
                    allowed = await _safety.AllowAsync(
                        planned.Action, planned.IsDestructive, planned.Thought, ct);
                }
                catch (OperationCanceledException)
                {
                    return Finish(LoopOutcome.Cancelled, step - 1,
                        "Cancelled while awaiting confirmation.", history, totalSw, containsPassword);
                }
                if (!allowed)
                {
                    history.Add(new HistoryEntry(step, planned.Thought, planned.Action,
                        "denied: user declined destructive action"));
                    return Finish(LoopOutcome.Denied, step,
                        "User denied a destructive action.", history, totalSw, containsPassword);
                }
            }

            var result = ExecuteWithRetry(planned.Action, ct);
            Log.Information("step {Step}: {Status} -- {Message}", step, result.Status, result.Message);

            // Redact typed text when the target was an IsPassword=true Edit
            // control. Applies to the artifact, the in-memory history, and
            // any downstream serialisation (history.json).
            var actionForRecord = planned.Action;
            var plannedForArtifact = planned;
            if (result.WasPasswordField)
            {
                containsPassword = true;
                actionForRecord = planned.Action with { Text = "***REDACTED***" };
                plannedForArtifact = planned with { Action = actionForRecord };
            }
            WriteArtifact($"action-{step:D2}.json", plannedForArtifact);

            history.Add(new HistoryEntry(step, planned.Thought, actionForRecord,
                $"{result.Status.ToString().ToLowerInvariant()}: {result.Message}"));

            switch (result.Status)
            {
                case ExecutionStatus.Done:
                    return Finish(LoopOutcome.Done, step, result.Message, history, totalSw, containsPassword);

                case ExecutionStatus.Failed when planned.Action.Type == ActionTypes.Fail:
                    return Finish(LoopOutcome.Failed, step, result.Message, history, totalSw, containsPassword);

                case ExecutionStatus.Failed:
                    Log.Debug("Step {Step} failed at execution but model didn't give up; continuing.", step);
                    break;

                case ExecutionStatus.Ok:
                    if (_postActionSettleMs > 0)
                    {
                        try { await Task.Delay(_postActionSettleMs, ct); }
                        catch (OperationCanceledException)
                        {
                            return Finish(LoopOutcome.Cancelled, step,
                                "Cancelled between steps.", history, totalSw, containsPassword);
                        }
                    }
                    break;
            }
        }

        return Finish(LoopOutcome.MaxSteps, _maxSteps,
            $"Hit step limit ({_maxSteps}) without finishing.", history, totalSw, containsPassword);
    }

    private async Task<(string TreeJson, byte[] ShotBytes)> CaptureWorldAsync(int step, CancellationToken ct)
    {
        var tree = UiaTreeDumper.Dump(_session.TargetWindow);
        var treeJson = JsonSerializer.Serialize(tree, JsonOpts);
        File.WriteAllText(Path.Combine(_outDir, $"tree-{step:D2}.json"), treeJson);

        var rect = await WaitForValidBoundsAsync(ct);
        var shotPath = Path.Combine(_outDir, $"shot-{step:D2}.png");
        ScreenCapture.CapturePng(rect, shotPath);
        var bytes = ScreenCapture.ReadAndDownscalePng(shotPath, _screenshotMaxEdge);

        Log.Debug("step {Step} capture: tree={TreeChars} chars, shot={Kb} KB (window {W}x{H})",
            step, treeJson.Length, bytes.Length / 1024, rect.Width, rect.Height);
        return (treeJson, bytes);
    }

    // After a dialog closes the target window can briefly report 0x0 bounds while
    // it repaints/activates. Retry with short back-off before falling back to the
    // primary screen dimensions so the loop can continue rather than crashing.
    private async Task<Rectangle> WaitForValidBoundsAsync(CancellationToken ct)
    {
        int[] delays = { 150, 300, 600, 1200 };
        foreach (var ms in delays)
        {
            var r = _session.TargetWindow.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0) return r;
            Log.Debug("Window bounds are 0x0; waiting {Ms}ms to settle", ms);
            await Task.Delay(ms, ct);
        }
        var final = _session.TargetWindow.BoundingRectangle;
        if (final.Width > 0 && final.Height > 0) return final;

        var sw = GetSystemMetrics(0);  // SM_CXSCREEN
        var sh = GetSystemMetrics(1);  // SM_CYSCREEN
        Log.Warning("Window bounds still 0x0 after retries; capturing primary screen ({W}x{H})", sw, sh);
        return new Rectangle(0, 0, sw, sh);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private ExecutionResult ExecuteWithRetry(AgentAction action, CancellationToken ct)
    {
        try
        {
            return _executor.Execute(action, ct);
        }
        catch (ElementNotAvailableException ex)
        {
            Log.Debug(ex, "Stale element; retrying once after 250ms");
            try { Task.Delay(250, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw; }

            try
            {
                return _executor.Execute(action, ct);
            }
            catch (ElementNotAvailableException retryEx)
            {
                return new ExecutionResult(ExecutionStatus.Failed,
                    $"Element became unavailable twice in a row: {retryEx.Message}");
            }
        }
    }

    private void WriteArtifact<T>(string filename, T payload)
        => File.WriteAllText(Path.Combine(_outDir, filename), JsonSerializer.Serialize(payload, JsonOpts));

    private static LoopResult Finish(
        LoopOutcome outcome, int steps, string message,
        IReadOnlyList<HistoryEntry> history, Stopwatch sw,
        bool containsPassword = false)
    {
        sw.Stop();
        Log.Information("Loop finished: {Outcome} after {Steps} steps in {Ms}ms -- {Message}",
            outcome, steps, sw.ElapsedMilliseconds, message);
        return new LoopResult(outcome, steps, message, history, containsPassword);
    }
}
