using System.Runtime.Versioning;
using DesktopAutoAI.Agent;
using FlaUI.Core.Exceptions;
using Serilog;

namespace DesktopAutoAI.Skills;

public sealed record ReplayResult(int StepsRun, bool Succeeded, string Message);

/// <summary>
/// Runs a cached <see cref="Skill"/> against the current app state. On the
/// first failed step it bails out so the caller can fall back to live
/// planning starting from wherever the partial replay left the UI.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SkillReplayer
{
    private readonly ActionExecutor _executor;
    private readonly int _settleMs;

    public SkillReplayer(ActionExecutor executor, int settleMs = 150)
    {
        _executor = executor;
        _settleMs = Math.Max(0, settleMs);
    }

    public async Task<ReplayResult> ReplayAsync(
        IReadOnlyList<AgentAction> actions, CancellationToken ct)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = actions[i];

            ExecutionResult res;
            try
            {
                res = _executor.Execute(a, ct);
            }
            catch (ElementNotAvailableException ex)
            {
                var msg = $"Element became unavailable at replay step {i + 1}: {ex.Message}";
                Log.Information("Replay aborted - {Msg}", msg);
                return new ReplayResult(i, false, msg);
            }

            Log.Information("Replay step {Step}/{Total} ({Type}): {Status} - {Message}",
                i + 1, actions.Count, a.Type, res.Status, res.Message);

            switch (res.Status)
            {
                case ExecutionStatus.Failed:
                    return new ReplayResult(i, false, res.Message);

                case ExecutionStatus.Done:
                    return new ReplayResult(i + 1, true, res.Message);

                case ExecutionStatus.Ok:
                    if (_settleMs > 0) await Task.Delay(_settleMs, ct);
                    break;
            }
        }

        Log.Warning("Replay ran all {Count} actions without a 'done' - treating as success.",
            actions.Count);
        return new ReplayResult(actions.Count, true, "Replay completed all actions.");
    }
}
