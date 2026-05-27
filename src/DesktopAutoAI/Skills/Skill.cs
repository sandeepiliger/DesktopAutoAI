using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Skills;

/// <summary>
/// A cached action sequence that previously got from "open the app" to
/// "goal achieved". Replayed on the next run with the same
/// (app_executable, normalized_goal); on first replay failure the agent
/// falls back to live planning and overwrites this record on success.
/// </summary>
public sealed record Skill(
    string CacheKey,
    string AppExecutable,
    string Goal,
    string NormalizedGoal,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    int SuccessCount,
    IReadOnlyList<AgentAction> Actions,
    // Present when the skill was recorded in batch mode. Carries each action's
    // verified postcondition so replay is self-checking against app drift. The
    // flat Actions list is still populated for the per-step replayer's benefit.
    IReadOnlyList<PlanStep>? Steps = null);
