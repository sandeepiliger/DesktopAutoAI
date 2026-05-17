using System.Text.RegularExpressions;
using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Safety;

/// <summary>
/// Regex-based check for destructive UI actions. Independent of the model -
/// fires on the action's own metadata so cached replays are still guarded
/// even though the saved skill has no <c>is_destructive</c> flag.
/// </summary>
public sealed class DestructiveDetector
{
    public static readonly IReadOnlyList<string> DefaultPatterns = new[]
    {
        @"\bdelete\b",
        @"\bremove\b",
        @"\bsend\b",
        @"\bsubmit\b",
        @"\bdrop\b",
        @"\bformat\b",
        @"\bdiscard\b",
        @"\boverwrite\b",
        @"\buninstall\b",
        @"\berase\b",
    };

    private readonly Regex _combined;

    public DestructiveDetector(IEnumerable<string>? patterns = null)
    {
        var src = patterns?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (src is null || src.Length == 0) src = DefaultPatterns.ToArray();
        var combined = string.Join("|", src.Select(p => $"(?:{p})"));
        _combined = new Regex(combined, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// True if any of: (a) the model flagged the action as destructive,
    /// (b) the selector targets an element whose Name / AutomationId
    /// matches a destructive pattern, (c) the action is a Type with
    /// destructive text, or (d) the action is a Key like Delete.
    /// </summary>
    public bool IsDestructive(AgentAction action, bool modelFlag = false)
    {
        if (modelFlag) return true;

        if (action.Selector is { } sel)
        {
            if (Matches(sel.Name)) return true;
            if (Matches(sel.AutomationId)) return true;
            if (sel.Path is not null)
            {
                foreach (var hop in sel.Path)
                {
                    if (Matches(hop.Name)) return true;
                    if (Matches(hop.AutomationId)) return true;
                }
            }
        }

        if (action.Type == ActionTypes.Type && Matches(action.Text)) return true;
        if (action.Type == ActionTypes.Key  && Matches(action.Key))  return true;

        return false;
    }

    private bool Matches(string? s) => !string.IsNullOrEmpty(s) && _combined.IsMatch(s);
}
