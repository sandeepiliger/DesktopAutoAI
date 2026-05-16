namespace DesktopAutoAI.Agent;

public sealed record PathHop(
    string ControlType,
    string? Name = null,
    string? AutomationId = null,
    int? Index = null);

public sealed record ActionSelector(
    List<PathHop>? Path = null,
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null);

public sealed record ActionPoint(int X, int Y);

public sealed record AgentAction(
    string Type,
    ActionSelector? Selector = null,
    string? Text = null,
    string? Value = null,
    string? Direction = null,
    int? Amount = null,
    string? Key = null,
    ActionPoint? Point = null,
    int? Ms = null,
    string? Reason = null);

public sealed record PlannedAction(
    string Thought,
    AgentAction Action,
    bool IsDestructive = false);

public static class ActionTypes
{
    public const string Invoke = "invoke";
    public const string Type = "type";
    public const string Select = "select";
    public const string Scroll = "scroll";
    public const string Key = "key";
    public const string Click = "click";
    public const string Wait = "wait";
    public const string Done = "done";
    public const string Fail = "fail";
}
