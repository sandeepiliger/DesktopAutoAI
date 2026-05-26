namespace DesktopAutoAI.Agent;

/// <summary>
/// A locally-checkable assertion about UI state after a <see cref="PlanStep"/>
/// runs. The <see cref="Verifier"/> reads the live UIA tree back to confirm it -
/// no LLM call. This is the core accuracy mechanism of batch mode: every
/// state-changing step is proven, not assumed.
/// </summary>
public sealed record Expectation(
    string Check,                       // see ExpectationChecks
    ActionSelector? Selector = null,
    string? Value = null);

public static class ExpectationChecks
{
    public const string None = "none";
    public const string ValueEquals = "value_equals";
    public const string ToggleOn = "toggle_on";
    public const string ToggleOff = "toggle_off";
    public const string Selected = "selected";
    public const string Exists = "exists";
    public const string Gone = "gone";
    public const string RangeEquals = "range_equals";
}

/// <summary>
/// One step of a batch plan: a concrete action plus an optional postcondition
/// the executor verifies before moving on.
/// </summary>
public sealed record PlanStep(
    string Intent,
    AgentAction Action,
    Expectation? Expect = null,
    bool IsDestructive = false);

/// <summary>
/// The full ordered plan the model returns in a single call. Steps are executed
/// deterministically; the plan is regenerated (repaired) only when a step fails
/// or its expectation is not met.
/// </summary>
public sealed record BatchPlan(
    string Thought,
    IReadOnlyList<PlanStep> Steps,
    string? SuccessCriteria = null);

/// <summary>
/// Input to <c>IActionPlanner.PlanBatchAsync</c>. <see cref="FailureContext"/> is
/// null for the first plan and populated on repair calls with what went wrong.
/// </summary>
public sealed record BatchPlanRequest(
    string Goal,
    string FilteredTreeJson,
    string? FailureContext = null);
