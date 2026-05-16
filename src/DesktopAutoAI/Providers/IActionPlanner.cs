using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Providers;

public interface IActionPlanner
{
    string ProviderName { get; }
    string ModelId { get; }
    Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct);
}

public sealed record PlanRequest(
    string Goal,
    string FilteredTreeJson,
    byte[] ScreenshotPng,
    IReadOnlyList<HistoryEntry> History);

public sealed record HistoryEntry(
    int Step,
    string Thought,
    AgentAction Action,
    string Result);
