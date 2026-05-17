using System.Text;
using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Safety;

/// <summary>
/// Single entry point used by both the live planning loop and the cached
/// skill replayer to gate destructive actions. Built once at startup,
/// injected as a dependency.
/// </summary>
public sealed class SafetyGate
{
    private readonly DestructiveDetector _detector;
    private readonly ConfirmationPrompt _prompt;
    private readonly bool _enabled;

    public SafetyGate(DestructiveDetector detector, ConfirmationPrompt prompt, bool enabled = true)
    {
        _detector = detector;
        _prompt = prompt;
        _enabled = enabled;
    }

    public bool Enabled => _enabled;

    public async Task<bool> AllowAsync(
        AgentAction action, bool modelFlag, string? thought, CancellationToken ct)
    {
        if (!_enabled) return true;
        if (!_detector.IsDestructive(action, modelFlag)) return true;

        var summary = BuildSummary(action, modelFlag, thought);
        return await _prompt.ConfirmAsync(summary, ct);
    }

    private static string BuildSummary(AgentAction a, bool modelFlag, string? thought)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Action      : {a.Type}");
        if (a.Selector is { } sel)
        {
            if (!string.IsNullOrEmpty(sel.Name))         sb.AppendLine($"Target name : {sel.Name}");
            if (!string.IsNullOrEmpty(sel.AutomationId)) sb.AppendLine($"Target id   : {sel.AutomationId}");
            if (!string.IsNullOrEmpty(sel.ControlType))  sb.AppendLine($"Control type: {sel.ControlType}");
            if (sel.Path is { Count: > 0 } path)
                sb.AppendLine($"Path leaf   : {path[^1].ControlType} '{path[^1].Name ?? "?"}'");
        }
        if (!string.IsNullOrEmpty(a.Text)) sb.AppendLine($"Text        : {a.Text}");
        if (!string.IsNullOrEmpty(a.Key))  sb.AppendLine($"Key         : {a.Key}");
        sb.AppendLine($"Flagged     : {(modelFlag ? "model said is_destructive=true" : "matched destructive pattern")}");
        if (!string.IsNullOrEmpty(thought)) sb.AppendLine($"Thought     : {thought}");
        return sb.ToString().TrimEnd();
    }
}
