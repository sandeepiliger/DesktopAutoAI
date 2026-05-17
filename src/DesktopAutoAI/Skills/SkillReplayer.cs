using System.Runtime.Versioning;
using DesktopAutoAI.Agent;
using DesktopAutoAI.Safety;
using FlaUI.Core.Exceptions;
using Serilog;

namespace DesktopAutoAI.Skills;

public sealed record ReplayResult(int StepsRun, bool Succeeded, bool Denied, string Message);

/// <summary>
/// Runs a cached <see cref="Skill"/> against the current app state. On the
/// first failed step it bails out so the caller can fall back to live
/// planning starting from wherever the partial replay left the UI. If a
/// <see cref="SafetyGate"/> is configured it gates each destructive action
/// just like the live planning loop does.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SkillReplayer
{
    private readonly ActionExecutor _executor;
    private readonly SafetyGate? _safety;
    private readonly int _settleMs;

    public SkillReplayer(ActionExecutor executor, SafetyGate? safety = null, int settleMs = 150)
    {
        _executor = executor;
        _safety = safety;
        _settleMs = Math.Max(0, settleMs);
    }

    public async Task<ReplayResult> ReplayAsync(
        IReadOnlyList<AgentAction> actions, CancellationToken ct)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = actions[i];

            if (_safety is not null)
            {
                // Cached actions don't carry the model's IsDestructive flag,
                // so the detector has to decide based on the action itself.
                var allowed = await _safety.AllowAsync(a, modelFlag: false, thought: null, ct);
                if (!allowed)
                {
                    var msg = $"User denied destructive action at replay step {i + 1}.";
                    Log.Warning("Replay aborted - {Msg}", msg);
                    return new ReplayResult(i, false, true, msg);
                }
            }

            ExecutionResult res;
            try
            {
                res = _executor.Execute(a, ct);
            }
            catch (ElementNotAvailableException ex)
            {
                var msg = $"Element became unavailable at replay step {i + 1}: {ex.Message}";
                Log.Information("Replay aborted - {Msg}", msg);
                return new ReplayResult(i, false, false, msg);
            }

            Log.Information("Replay step {Step}/{Total} ({Type}): {Status} - {Message}",
                i + 1, actions.Count, a.Type, res.Status, res.Message);

            switch (res.Status)
            {
                case ExecutionStatus.Failed:
                    return new ReplayResult(i, false, false, res.Message);

                case ExecutionStatus.Done:
                    return new ReplayResult(i + 1, true, false, res.Message);

                case ExecutionStatus.Ok:
                    if (_settleMs > 0) await Task.Delay(_settleMs, ct);
                    break;
            }
        }

        Log.Warning("Replay ran all {Count} actions without a 'done' - treating as success.",
            actions.Count);
        return new ReplayResult(actions.Count, true, false, "Replay completed all actions.");
    }
}
