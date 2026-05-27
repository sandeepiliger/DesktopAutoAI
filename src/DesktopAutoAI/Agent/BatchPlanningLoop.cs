using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Providers;
using DesktopAutoAI.Safety;
using DesktopAutoAI.Uia;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using Serilog;

namespace DesktopAutoAI.Agent;

public enum BatchOutcome { Done, Failed, PlannerError, Cancelled, Denied, MaxRepairs }

public sealed record BatchResult(
    BatchOutcome Outcome,
    int StepsExecuted,
    int LlmCalls,
    int Repairs,
    string Message,
    IReadOnlyList<PlanStep> ExecutedPlan,
    bool ContainsPasswordFields = false);

/// <summary>
/// Batch planning + verify-and-repair. One LLM call produces the whole plan;
/// each step is executed deterministically and its postcondition verified by
/// reading UIA state back (no LLM). On a failed step or failed verification the
/// remaining plan is regenerated from the current state, bounded by
/// <c>maxRepairs</c>. A whole task therefore costs ~1 LLM call instead of one
/// per step - which is what fixes the rate-limit / speed / accuracy problems of
/// the per-step loop for apps that expose AutomationIds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BatchPlanningLoop
{
    private static readonly JsonSerializerOptions ArtifactOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions TreeJsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IActionPlanner _planner;
    private readonly UiaSession _session;
    private readonly ActionExecutor _executor;
    private readonly Verifier _verifier;
    private readonly SafetyGate? _safety;
    private readonly int _maxRepairs;
    private readonly int _settleMs;
    private readonly string _outDir;

    public BatchPlanningLoop(
        IActionPlanner planner,
        UiaSession session,
        string outDir,
        SafetyGate? safety = null,
        int maxRepairs = 3,
        int settleMs = 150)
    {
        _planner = planner;
        _session = session;
        _executor = new ActionExecutor(session.TargetWindow, treeOnly: true);
        _verifier = new Verifier(session.TargetWindow);
        _safety = safety;
        _maxRepairs = Math.Max(0, maxRepairs);
        _settleMs = Math.Max(0, settleMs);
        _outDir = outDir;
        Directory.CreateDirectory(_outDir);
    }

    public async Task<BatchResult> RunAsync(string goal, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var executed = new List<PlanStep>();
        int llmCalls = 0, repairs = 0, stepNum = 0;
        bool containsPassword = false;

        BatchPlan plan;
        try
        {
            var (tree, nodeCount) = await CaptureTreeAsync("plan-00", ct);
            if (nodeCount <= 1)
            {
                return Finish(BatchOutcome.PlannerError, executed, 0, llmCalls, repairs,
                    $"UIA tree is empty ({nodeCount} node). The target window exposed no automation elements. " +
                    "Check that the right window is selected, restored (not minimized), and fully loaded, " +
                    "then try again. (Saved tree-plan-00.json for inspection.)",
                    containsPassword, sw);
            }
            plan = await _planner.PlanBatchAsync(new BatchPlanRequest(goal, tree), ct);
            llmCalls++;
        }
        catch (OperationCanceledException)
        {
            return Finish(BatchOutcome.Cancelled, executed, 0, llmCalls, repairs, "Cancelled during planning.", containsPassword, sw);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Batch planner failed");
            return Finish(BatchOutcome.PlannerError, executed, 0, llmCalls, repairs, ex.Message, containsPassword, sw);
        }

        WriteArtifact("plan-00.json", plan);
        Log.Information("Batch plan: {Steps} step(s). Success criteria: {Criteria}",
            plan.Steps.Count, plan.SuccessCriteria ?? "(none)");

        var pending = plan.Steps;

        while (pending.Count > 0)
        {
            string? failureContext = null;

            for (int i = 0; i < pending.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return Finish(BatchOutcome.Cancelled, executed, stepNum, llmCalls, repairs, "Cancelled by user.", containsPassword, sw);

                var step = pending[i];
                stepNum++;

                if (step.Action.Type == ActionTypes.Done)
                {
                    return Finish(BatchOutcome.Done, executed, stepNum - 1, llmCalls, repairs,
                        step.Action.Reason ?? "Plan reported done.", containsPassword, sw);
                }
                if (step.Action.Type == ActionTypes.Fail)
                {
                    return Finish(BatchOutcome.Failed, executed, stepNum - 1, llmCalls, repairs,
                        step.Action.Reason ?? "Plan reported failure.", containsPassword, sw);
                }

                Log.Information("step {Step} ({Type}){Destructive}: {Intent}",
                    stepNum, step.Action.Type, step.IsDestructive ? " [destructive!]" : "", step.Intent);

                if (_safety is not null)
                {
                    bool allowed;
                    try
                    {
                        allowed = await _safety.AllowAsync(step.Action, step.IsDestructive, step.Intent, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return Finish(BatchOutcome.Cancelled, executed, stepNum, llmCalls, repairs,
                            "Cancelled while awaiting confirmation.", containsPassword, sw);
                    }
                    if (!allowed)
                        return Finish(BatchOutcome.Denied, executed, stepNum, llmCalls, repairs,
                            "User denied a destructive action.", containsPassword, sw);
                }

                ExecutionResult result;
                try
                {
                    result = ExecuteWithRetry(step.Action, ct);
                }
                catch (OperationCanceledException)
                {
                    return Finish(BatchOutcome.Cancelled, executed, stepNum, llmCalls, repairs,
                        "Cancelled during execution.", containsPassword, sw);
                }

                var recordStep = step;
                if (result.WasPasswordField)
                {
                    containsPassword = true;
                    recordStep = step with { Action = step.Action with { Text = "***REDACTED***" } };
                }

                Log.Information("step {Step}: {Status} -- {Message}", stepNum, result.Status, result.Message);

                if (result.Status == ExecutionStatus.Failed)
                {
                    failureContext = $"Step {stepNum} '{step.Intent}' ({step.Action.Type}) failed: {result.Message}";
                    Log.Warning("Divergence: {Ctx}", failureContext);
                    break;
                }

                // Verify the postcondition.
                var verify = await VerifyStepAsync(step, ct);
                if (!verify.Ok)
                {
                    failureContext = $"Step {stepNum} '{step.Intent}' postcondition not met: {verify.Reason}";
                    Log.Warning("Divergence: {Ctx}", failureContext);
                    break;
                }

                executed.Add(recordStep);

                if (_settleMs > 0)
                {
                    try { await Task.Delay(_settleMs, ct); }
                    catch (OperationCanceledException)
                    {
                        return Finish(BatchOutcome.Cancelled, executed, stepNum, llmCalls, repairs,
                            "Cancelled between steps.", containsPassword, sw);
                    }
                }
            }

            if (failureContext is null)
            {
                // Whole pending plan executed and verified.
                return Finish(BatchOutcome.Done, executed, stepNum, llmCalls, repairs,
                    plan.SuccessCriteria ?? "Plan completed.", containsPassword, sw);
            }

            // Divergence -> repair (re-plan remaining from current state).
            if (repairs >= _maxRepairs)
            {
                return Finish(BatchOutcome.MaxRepairs, executed, stepNum, llmCalls, repairs,
                    $"Gave up after {_maxRepairs} repair(s). Last problem: {failureContext}", containsPassword, sw);
            }

            repairs++;
            try
            {
                var (tree, _) = await CaptureTreeAsync($"plan-repair-{repairs:D2}", ct);
                plan = await _planner.PlanBatchAsync(new BatchPlanRequest(goal, tree, failureContext), ct);
                llmCalls++;
            }
            catch (OperationCanceledException)
            {
                return Finish(BatchOutcome.Cancelled, executed, stepNum, llmCalls, repairs, "Cancelled during repair.", containsPassword, sw);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Repair planning failed");
                return Finish(BatchOutcome.PlannerError, executed, stepNum, llmCalls, repairs, ex.Message, containsPassword, sw);
            }

            WriteArtifact($"plan-repair-{repairs:D2}.json", plan);
            Log.Information("Repair #{N}: {Steps} new step(s).", repairs, plan.Steps.Count);
            pending = plan.Steps;
        }

        return Finish(BatchOutcome.Done, executed, stepNum, llmCalls, repairs,
            "Plan completed (no further steps).", containsPassword, sw);
    }

    /// <summary>
    /// Replay a cached plan with the SAME verification - no LLM calls. On the
    /// first failed step or failed postcondition, returns a non-Done outcome so
    /// the caller can fall back to live <see cref="RunAsync"/> from the current
    /// state. This is what makes cached replay self-checking against app drift.
    /// </summary>
    public async Task<BatchResult> ReplayAsync(IReadOnlyList<PlanStep> steps, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var executed = new List<PlanStep>();
        bool containsPassword = false;

        for (int i = 0; i < steps.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return Finish(BatchOutcome.Cancelled, executed, i, 0, 0, "Cancelled by user.", containsPassword, sw);

            var step = steps[i];

            if (step.Action.Type == ActionTypes.Done)
                return Finish(BatchOutcome.Done, executed, i, 0, 0, "Replay reached done.", containsPassword, sw);
            if (step.Action.Type == ActionTypes.Fail)
                return Finish(BatchOutcome.Failed, executed, i, 0, 0, "Replay reached a fail step.", containsPassword, sw);

            if (_safety is not null)
            {
                var allowed = await _safety.AllowAsync(step.Action, step.IsDestructive, step.Intent, ct);
                if (!allowed)
                    return Finish(BatchOutcome.Denied, executed, i + 1, 0, 0, "User denied a destructive action.", containsPassword, sw);
            }

            ExecutionResult result;
            try
            {
                result = ExecuteWithRetry(step.Action, ct);
            }
            catch (ElementNotAvailableException ex)
            {
                return Finish(BatchOutcome.Failed, executed, i, 0, 0,
                    $"Element unavailable at replay step {i + 1}: {ex.Message}", containsPassword, sw);
            }

            var recordStep = step;
            if (result.WasPasswordField)
            {
                containsPassword = true;
                recordStep = step with { Action = step.Action with { Text = "***REDACTED***" } };
            }

            Log.Information("Replay step {Step}/{Total} ({Type}): {Status} -- {Message}",
                i + 1, steps.Count, step.Action.Type, result.Status, result.Message);

            if (result.Status == ExecutionStatus.Failed)
                return Finish(BatchOutcome.Failed, executed, i, 0, 0, result.Message, containsPassword, sw);

            var verify = await VerifyStepAsync(step, ct);
            if (!verify.Ok)
                return Finish(BatchOutcome.Failed, executed, i, 0, 0,
                    $"Replay step {i + 1} postcondition not met: {verify.Reason}", containsPassword, sw);

            executed.Add(recordStep);
            if (_settleMs > 0) await Task.Delay(_settleMs, ct);
        }

        return Finish(BatchOutcome.Done, executed, steps.Count, 0, 0, "Replay completed all steps.", containsPassword, sw);
    }

    private async Task<VerifyResult> VerifyStepAsync(PlanStep step, CancellationToken ct)
    {
        if (step.Expect is { } expect && !string.Equals(expect.Check, ExpectationChecks.None, StringComparison.OrdinalIgnoreCase))
        {
            var vr = _verifier.Check(expect);
            if (vr.Ok) return vr;

            // Give the UI a moment to settle, then check once more before
            // declaring a divergence - avoids repairs on slow async updates.
            try { await Task.Delay(Math.Max(_settleMs, 150), ct); }
            catch (OperationCanceledException) { throw; }
            return _verifier.Check(expect);
        }

        // No model-supplied expectation: a default check is advisory only.
        var soft = _verifier.CheckDefault(step.Action);
        if (!soft.Ok)
            Log.Debug("Soft postcondition mismatch on '{Intent}': {Reason} (not treated as divergence).",
                step.Intent, soft.Reason);
        return new VerifyResult(true, "ok");
    }

    private ExecutionResult ExecuteWithRetry(AgentAction action, CancellationToken ct)
    {
        try
        {
            return _executor.Execute(action, ct);
        }
        catch (ElementNotAvailableException)
        {
            try { Task.Delay(250, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw; }
            return _executor.Execute(action, ct);
        }
    }

    // Dumps the UIA tree, restoring the window and retrying while the tree
    // comes back empty (the common "window minimized / not yet rendered"
    // failure that yields a root-only tree the model can't act on).
    private async Task<(string Json, int NodeCount)> CaptureTreeAsync(string artifactName, CancellationToken ct)
    {
        int[] delaysMs = { 0, 400, 800 };
        TreeNode best = new() { ControlType = "Unknown" };
        int bestCount = 0;

        foreach (var delay in delaysMs)
        {
            ct.ThrowIfCancellationRequested();
            if (delay > 0) await Task.Delay(delay, ct);

            TryRestoreWindow();
            var tree = UiaTreeDumper.Dump(_session.TargetWindow);
            var count = CountNodes(tree);
            if (count > bestCount) { best = tree; bestCount = count; }
            if (count > 1) break;

            Log.Debug("UIA tree dump returned {Count} node(s); window may not be ready, retrying.", count);
        }

        var compact = JsonSerializer.Serialize(best, TreeJsonOpts);
        File.WriteAllText(Path.Combine(_outDir, $"tree-{artifactName}.json"),
            JsonSerializer.Serialize(best, ArtifactOpts));
        Log.Information("Captured UIA tree: {Count} node(s), {Chars} chars (window '{Title}').",
            bestCount, compact.Length, SafeTitle());
        return (compact, bestCount);
    }

    private string SafeTitle()
    {
        try { return _session.TargetWindow.Title ?? "(no title)"; }
        catch { return "(unavailable)"; }
    }

    private void TryRestoreWindow()
    {
        try
        {
            var win = _session.TargetWindow;
            if (win.Patterns.Window.IsSupported)
            {
                var wp = win.Patterns.Window.Pattern;
                if (wp.WindowVisualState.ValueOrDefault == WindowVisualState.Minimized)
                {
                    Log.Information("Target window is minimized; restoring it before dumping the tree.");
                    wp.SetWindowVisualState(WindowVisualState.Normal);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not restore the target window; continuing.");
        }
    }

    private static int CountNodes(TreeNode? node)
    {
        if (node is null) return 0;
        int count = 1;
        if (node.Children is { } children)
            foreach (var child in children) count += CountNodes(child);
        return count;
    }

    private void WriteArtifact<T>(string filename, T payload)
        => File.WriteAllText(Path.Combine(_outDir, filename), JsonSerializer.Serialize(payload, ArtifactOpts));

    private static BatchResult Finish(
        BatchOutcome outcome, List<PlanStep> executed, int steps, int llmCalls, int repairs,
        string message, bool containsPassword, Stopwatch sw)
    {
        sw.Stop();
        Log.Information("Batch finished: {Outcome} | steps={Steps}, llm_calls={Llm}, repairs={Repairs}, {Ms}ms -- {Message}",
            outcome, steps, llmCalls, repairs, sw.ElapsedMilliseconds, message);
        return new BatchResult(outcome, steps, llmCalls, repairs, message, executed, containsPassword);
    }
}
