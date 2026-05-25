using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Providers;

public interface IActionPlanner
{
    string ProviderName { get; }
    string ModelId { get; }
    Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct);

    /// <summary>
    /// Minimal round-trip used by the <c>ping</c> command to isolate connectivity,
    /// auth, and model availability from the full planner payload. Sends "Say
    /// 'pong'." with no tools, no image, no system prompt. Returns the model's
    /// raw text reply (trimmed).
    /// </summary>
    Task<string> PingAsync(CancellationToken ct);
}

public sealed record PlanRequest(
    string Goal,
    string FilteredTreeJson,
    byte[]? ScreenshotPng,
    IReadOnlyList<HistoryEntry> History);

public sealed record HistoryEntry(
    int Step,
    string Thought,
    AgentAction Action,
    string Result);
