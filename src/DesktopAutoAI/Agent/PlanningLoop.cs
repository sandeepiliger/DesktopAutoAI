using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Providers;
using DesktopAutoAI.Uia;
using FlaUI.Core.Exceptions;
using Serilog;

namespace DesktopAutoAI.Agent;

public enum LoopOutcome { Done, Failed, MaxSteps, PlannerError, Cancelled }

public sealed record LoopResult(
    LoopOutcome Outcome,
    int StepsTaken,
    string Message,
    IReadOnlyList<HistoryEntry> History);

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
        int postActionSettleMs = 150)
    {
        _planner = planner;
        _session = session;
        _executor = new ActionExecutor(session.TargetWindow);
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

        for (int step = 1; step <= _maxSteps; step++)
        {
            if (ct.IsCancellationRequested)
                return Finish(LoopOutcome.Cancelled, step - 1, "Cancelled by user.", history, totalSw);

            var (treeJson, shotBytes) = CaptureWorld(step);

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
                return Finish(LoopOutcome.PlannerError, step - 1, ex.Message, history, totalSw);
            }

            WriteArtifact($"action-{step:D2}.json", planned);

            var marker = planned.IsDestructive ? " [destructive!]" : "";
            Log.Information("step {Step}: {Type}{Marker} -- {Thought}",
                step, planned.Action.Type, marker, planned.Thought);

            var result = ExecuteWithRetry(planned.Action, ct);
            Log.Information("step {Step}: {Status} -- {Message}", step, result.Status, result.Message);

            history.Add(new HistoryEntry(step, planned.Thought, planned.Action,
                $"{result.Status.ToString().ToLowerInvariant()}: {result.Message}"));

            switch (result.Status)
            {
                case ExecutionStatus.Done:
                    return Finish(LoopOutcome.Done, step, result.Message, history, totalSw);

                case ExecutionStatus.Failed when planned.Action.Type == ActionTypes.Fail:
                    return Finish(LoopOutcome.Failed, step, result.Message, history, totalSw);

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
                                "Cancelled between steps.", history, totalSw);
                        }
                    }
                    break;
            }
        }

        return Finish(LoopOutcome.MaxSteps, _maxSteps,
            $"Hit step limit ({_maxSteps}) without finishing.", history, totalSw);
    }

    private (string TreeJson, byte[] ShotBytes) CaptureWorld(int step)
    {
        var tree = UiaTreeDumper.Dump(_session.TargetWindow);
        var treeJson = JsonSerializer.Serialize(tree, JsonOpts);
        File.WriteAllText(Path.Combine(_outDir, $"tree-{step:D2}.json"), treeJson);

        var rect = _session.TargetWindow.BoundingRectangle;
        var shotPath = Path.Combine(_outDir, $"shot-{step:D2}.png");
        ScreenCapture.CapturePng(rect, shotPath);
        var bytes = ScreenCapture.ReadAndDownscalePng(shotPath, _screenshotMaxEdge);

        Log.Debug("step {Step} capture: tree={TreeChars} chars, shot={Kb} KB (window {W}x{H})",
            step, treeJson.Length, bytes.Length / 1024, rect.Width, rect.Height);
        return (treeJson, bytes);
    }

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
        IReadOnlyList<HistoryEntry> history, Stopwatch sw)
    {
        sw.Stop();
        Log.Information("Loop finished: {Outcome} after {Steps} steps in {Ms}ms -- {Message}",
            outcome, steps, sw.ElapsedMilliseconds, message);
        return new LoopResult(outcome, steps, message, history);
    }
}
