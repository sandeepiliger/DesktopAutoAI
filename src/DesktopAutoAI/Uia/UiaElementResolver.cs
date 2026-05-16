using System.Runtime.Versioning;
using DesktopAutoAI.Agent;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using Serilog;

namespace DesktopAutoAI.Uia;

/// <summary>
/// Resolves an <see cref="ActionSelector"/> against a live UIA tree rooted at
/// a target window. Tries shortcuts (automation_id / name + control_type) first,
/// then walks the typed ordinal path.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UiaElementResolver
{
    public static AutomationElement? Resolve(AutomationElement root, ActionSelector selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.AutomationId))
        {
            var byId = FindByAutomationId(root, selector.AutomationId!, selector.ControlType);
            if (byId is not null) return byId;
            Log.Debug("Selector automation_id '{Id}' not found; trying path/name fallback.",
                selector.AutomationId);
        }

        if (selector.Path is { Count: > 0 })
        {
            var byPath = ResolvePath(root, selector.Path);
            if (byPath is not null) return byPath;
            Log.Debug("Selector path resolution failed; trying name fallback.");
        }

        if (!string.IsNullOrWhiteSpace(selector.Name) && !string.IsNullOrWhiteSpace(selector.ControlType))
        {
            return FindByNameAndType(root, selector.Name!, selector.ControlType!);
        }

        return null;
    }

    private static AutomationElement? FindByAutomationId(
        AutomationElement root, string automationId, string? controlType)
    {
        var cf = root.ConditionFactory;
        ConditionBase cond = cf.ByAutomationId(automationId);
        if (!string.IsNullOrWhiteSpace(controlType) && TryParseControlType(controlType, out var ct))
            cond = cond.And(cf.ByControlType(ct));
        return root.FindFirstDescendant(cond);
    }

    private static AutomationElement? FindByNameAndType(
        AutomationElement root, string name, string controlType)
    {
        if (!TryParseControlType(controlType, out var ct)) return null;
        var cf = root.ConditionFactory;
        return root.FindFirstDescendant(cf.ByName(name).And(cf.ByControlType(ct)));
    }

    private static AutomationElement? ResolvePath(AutomationElement root, List<PathHop> hops)
    {
        var current = root;
        for (int i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            var next = MatchHop(current, hop);
            if (next is null)
            {
                Log.Debug("Path hop {Index} ({Hop}) failed under {Current}",
                    i, FormatHop(hop), current.Properties.Name.ValueOrDefault);
                return null;
            }
            current = next;
        }
        return current;
    }

    private static AutomationElement? MatchHop(AutomationElement parent, PathHop hop)
    {
        var children = parent.FindAllChildren();
        var candidates = new List<AutomationElement>(children.Length);
        foreach (var c in children)
        {
            if (!TryParseControlType(hop.ControlType, out var ct)) continue;
            if (c.Properties.ControlType.ValueOrDefault != ct) continue;
            if (!string.IsNullOrEmpty(hop.AutomationId)
                && c.Properties.AutomationId.ValueOrDefault != hop.AutomationId) continue;
            if (!string.IsNullOrEmpty(hop.Name)
                && c.Properties.Name.ValueOrDefault != hop.Name) continue;
            candidates.Add(c);
        }

        if (candidates.Count == 0) return null;
        if (hop.Index is int idx && idx >= 0 && idx < candidates.Count) return candidates[idx];
        return candidates[0];
    }

    private static bool TryParseControlType(string name, out ControlType value)
    {
        if (Enum.TryParse(name, ignoreCase: true, out ControlType parsed))
        {
            value = parsed;
            return true;
        }
        value = ControlType.Custom;
        return false;
    }

    private static string FormatHop(PathHop h)
        => $"{h.ControlType}[name={h.Name ?? "*"}, id={h.AutomationId ?? "*"}, idx={h.Index?.ToString() ?? "*"}]";
}
