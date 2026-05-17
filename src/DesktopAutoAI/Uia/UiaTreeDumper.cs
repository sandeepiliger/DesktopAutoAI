using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace DesktopAutoAI.Uia;

public sealed class TreeNode
{
    public string ControlType { get; init; } = "";
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsKeyboardFocusable { get; init; }
    public List<string>? SupportedPatterns { get; init; }
    public List<int> Path { get; init; } = new();
    public List<TreeNode>? Children { get; init; }
}

[SupportedOSPlatform("windows")]
public static class UiaTreeDumper
{
    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button,
        ControlType.MenuItem,
        ControlType.Edit,
        ControlType.ComboBox,
        ControlType.ListItem,
        ControlType.TabItem,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.Hyperlink,
        ControlType.TreeItem,
        ControlType.DataItem,
        ControlType.SplitButton,
        ControlType.Slider,
        ControlType.Spinner,
        ControlType.Document,
    };

    public static TreeNode Dump(AutomationElement root, int maxDepth = 20)
        => BuildFiltered(root, new List<int>(), depth: 0, maxDepth)
           ?? new TreeNode { ControlType = "Unknown" };

    private static TreeNode? BuildFiltered(AutomationElement el, List<int> path, int depth, int maxDepth)
    {
        if (depth > maxDepth) return null;
        if (el.Properties.IsOffscreen.ValueOrDefault) return null;

        var children = el.FindAllChildren();
        var childNodes = new List<TreeNode>(children.Length);
        for (int i = 0; i < children.Length; i++)
        {
            var nextPath = new List<int>(path) { i };
            var childNode = BuildFiltered(children[i], nextPath, depth + 1, maxDepth);
            if (childNode is not null) childNodes.Add(childNode);
        }

        var name = el.Properties.Name.ValueOrDefault;
        var autoId = el.Properties.AutomationId.ValueOrDefault;
        var ctlType = el.Properties.ControlType.ValueOrDefault;
        bool isInteractive = InteractiveTypes.Contains(ctlType);
        bool isNamed = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(autoId);

        bool keep = depth == 0 || isInteractive || isNamed || childNodes.Count > 0;
        if (!keep) return null;

        var patterns = ListSupportedPatterns(el);
        return new TreeNode
        {
            ControlType = ctlType.ToString(),
            Name = string.IsNullOrEmpty(name) ? null : name,
            AutomationId = string.IsNullOrEmpty(autoId) ? null : autoId,
            IsEnabled = el.Properties.IsEnabled.ValueOrDefault,
            IsKeyboardFocusable = el.Properties.IsKeyboardFocusable.ValueOrDefault,
            SupportedPatterns = patterns.Count > 0 ? patterns : null,
            Path = path,
            Children = childNodes.Count > 0 ? childNodes : null,
        };
    }

    private static List<string> ListSupportedPatterns(AutomationElement el)
    {
        var p = el.Patterns;
        var list = new List<string>(8);
        if (p.Invoke.IsSupported) list.Add("Invoke");
        if (p.Toggle.IsSupported) list.Add("Toggle");
        if (p.Value.IsSupported) list.Add("Value");
        if (p.Text.IsSupported) list.Add("Text");
        if (p.SelectionItem.IsSupported) list.Add("SelectionItem");
        if (p.ExpandCollapse.IsSupported) list.Add("ExpandCollapse");
        if (p.Scroll.IsSupported) list.Add("Scroll");
        if (p.ScrollItem.IsSupported) list.Add("ScrollItem");
        if (p.RangeValue.IsSupported) list.Add("RangeValue");
        return list;
    }
}
