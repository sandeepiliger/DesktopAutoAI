using System.Runtime.Versioning;
using DesktopAutoAI.Uia;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Serilog;

namespace DesktopAutoAI.Agent;

public enum ExecutionStatus { Ok, Done, Failed }

public sealed record ExecutionResult(ExecutionStatus Status, string Message);

[SupportedOSPlatform("windows")]
public sealed class ActionExecutor
{
    private readonly AutomationElement _root;

    public ActionExecutor(AutomationElement targetWindowRoot)
    {
        _root = targetWindowRoot;
    }

    public ExecutionResult Execute(AgentAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return action.Type switch
        {
            ActionTypes.Invoke => DoInvoke(action),
            ActionTypes.Type => DoType(action),
            ActionTypes.Select => DoSelect(action),
            ActionTypes.Scroll => DoScroll(action),
            ActionTypes.Key => DoKey(action),
            ActionTypes.Click => DoClick(action),
            ActionTypes.Wait => DoWait(action, ct),
            ActionTypes.Done => new ExecutionResult(
                ExecutionStatus.Done,
                action.Reason ?? "Goal complete."),
            ActionTypes.Fail => new ExecutionResult(
                ExecutionStatus.Failed,
                action.Reason ?? "Model reported failure."),
            _ => new ExecutionResult(
                ExecutionStatus.Failed,
                $"Unknown action type '{action.Type}'."),
        };
    }

    private ExecutionResult DoInvoke(AgentAction a)
    {
        if (a.Selector is null) return Fail("invoke requires a selector.");
        var el = UiaElementResolver.Resolve(_root, a.Selector);
        if (el is null) return Fail("Element not found for invoke.");

        if (el.Patterns.Invoke.IsSupported)
        {
            el.Patterns.Invoke.Pattern.Invoke();
            return Ok("Invoked.");
        }
        if (el.Patterns.Toggle.IsSupported)
        {
            el.Patterns.Toggle.Pattern.Toggle();
            return Ok("Toggled.");
        }
        if (el.Patterns.ExpandCollapse.IsSupported)
        {
            var ec = el.Patterns.ExpandCollapse.Pattern;
            ec.Expand();
            return Ok("Expanded.");
        }
        if (el.Patterns.SelectionItem.IsSupported)
        {
            el.Patterns.SelectionItem.Pattern.Select();
            return Ok("Selected.");
        }

        el.Click();
        return Ok("Clicked (no invoke pattern available).");
    }

    private ExecutionResult DoType(AgentAction a)
    {
        if (string.IsNullOrEmpty(a.Text)) return Fail("type requires non-empty text.");
        if (a.Selector is not null)
        {
            var el = UiaElementResolver.Resolve(_root, a.Selector);
            if (el is null) return Fail("Element not found for type.");
            if (el.Patterns.Value.IsSupported)
            {
                try
                {
                    el.Patterns.Value.Pattern.SetValue(a.Text);
                    return Ok("Set value via Value pattern.");
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Value.SetValue failed; falling back to focus+type.");
                }
            }
            el.Focus();
        }
        Keyboard.Type(a.Text);
        return Ok($"Typed {a.Text!.Length} chars.");
    }

    private ExecutionResult DoSelect(AgentAction a)
    {
        if (a.Selector is null) return Fail("select requires a selector.");
        if (string.IsNullOrEmpty(a.Value)) return Fail("select requires a value.");
        var el = UiaElementResolver.Resolve(_root, a.Selector);
        if (el is null) return Fail("Element not found for select.");

        if (el is ComboBox combo)
        {
            var item = combo.Select(a.Value);
            return item is null
                ? Fail($"Combo item '{a.Value}' not found.")
                : Ok($"Selected '{a.Value}'.");
        }
        if (el.Patterns.SelectionItem.IsSupported)
        {
            el.Patterns.SelectionItem.Pattern.Select();
            return Ok("Selected via SelectionItem.");
        }
        return Fail("select target is not a ComboBox / SelectionItem.");
    }

    private ExecutionResult DoScroll(AgentAction a)
    {
        var notches = a.Amount ?? 1;
        var sign = (a.Direction ?? "down") switch
        {
            "up" => 1,
            "down" => -1,
            _ => -1,
        };

        if (a.Selector is not null)
        {
            var el = UiaElementResolver.Resolve(_root, a.Selector);
            if (el is not null && el.Patterns.Scroll.IsSupported)
            {
                var scroll = el.Patterns.Scroll.Pattern;
                var dir = sign > 0 ? FlaUI.Core.Definitions.ScrollAmount.SmallDecrement
                                   : FlaUI.Core.Definitions.ScrollAmount.SmallIncrement;
                for (int i = 0; i < notches; i++) scroll.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, dir);
                return Ok("Scrolled via Scroll pattern.");
            }
        }

        Mouse.Scroll(sign * notches);
        return Ok($"Scrolled {notches} notches {(sign > 0 ? "up" : "down")} via mouse wheel.");
    }

    private ExecutionResult DoKey(AgentAction a)
    {
        if (string.IsNullOrEmpty(a.Key)) return Fail("key requires a key string.");
        var keys = ParseKeyCombo(a.Key);
        if (keys.Count == 0) return Fail($"Could not parse key '{a.Key}'.");

        var modifiers = new List<VirtualKeyShort>();
        VirtualKeyShort? main = null;
        foreach (var k in keys)
        {
            if (IsModifier(k)) modifiers.Add(k);
            else main = k;
        }
        if (main is null) return Fail($"No non-modifier key in '{a.Key}'.");

        foreach (var m in modifiers) Keyboard.Press(m);
        Keyboard.Type(main.Value);
        foreach (var m in modifiers) Keyboard.Release(m);
        return Ok($"Sent key '{a.Key}'.");
    }

    private ExecutionResult DoClick(AgentAction a)
    {
        if (a.Point is null) return Fail("click requires a point.");
        Mouse.MoveTo(a.Point.X, a.Point.Y);
        Mouse.LeftClick();
        Log.Warning("Coordinate click at ({X},{Y}) - selector resolution failed or model bypassed UIA.",
            a.Point.X, a.Point.Y);
        return Ok($"Coordinate click at ({a.Point.X},{a.Point.Y}).");
    }

    private static ExecutionResult DoWait(AgentAction a, CancellationToken ct)
    {
        var ms = a.Ms is > 0 ? a.Ms.Value : 250;
        ms = Math.Min(ms, 10_000);
        Task.Delay(ms, ct).GetAwaiter().GetResult();
        return Ok($"Waited {ms}ms.");
    }

    private static List<VirtualKeyShort> ParseKeyCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<VirtualKeyShort>(parts.Length);
        foreach (var p in parts)
        {
            if (TryParseKey(p, out var vk)) result.Add(vk);
            else return new List<VirtualKeyShort>();
        }
        return result;
    }

    private static bool TryParseKey(string name, out VirtualKeyShort vk)
    {
        switch (name.ToLowerInvariant())
        {
            case "ctrl": case "control": vk = VirtualKeyShort.CONTROL; return true;
            case "shift": vk = VirtualKeyShort.SHIFT; return true;
            case "alt": case "menu": vk = VirtualKeyShort.ALT; return true;
            case "win": case "lwin": vk = VirtualKeyShort.LWIN; return true;
            case "enter": case "return": vk = VirtualKeyShort.RETURN; return true;
            case "tab": vk = VirtualKeyShort.TAB; return true;
            case "esc": case "escape": vk = VirtualKeyShort.ESCAPE; return true;
            case "backspace": vk = VirtualKeyShort.BACK; return true;
            case "delete": case "del": vk = VirtualKeyShort.DELETE; return true;
            case "space": vk = VirtualKeyShort.SPACE; return true;
            case "home": vk = VirtualKeyShort.HOME; return true;
            case "end": vk = VirtualKeyShort.END; return true;
            case "pageup": case "pgup": vk = VirtualKeyShort.PRIOR; return true;
            case "pagedown": case "pgdn": vk = VirtualKeyShort.NEXT; return true;
            case "up": vk = VirtualKeyShort.UP; return true;
            case "down": vk = VirtualKeyShort.DOWN; return true;
            case "left": vk = VirtualKeyShort.LEFT; return true;
            case "right": vk = VirtualKeyShort.RIGHT; return true;
        }

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z')
            {
                vk = (VirtualKeyShort)c;
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                vk = (VirtualKeyShort)c;
                return true;
            }
        }

        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (VirtualKeyShort)(0x70 + (fn - 1));
            return true;
        }

        vk = default;
        return false;
    }

    private static bool IsModifier(VirtualKeyShort k)
        => k == VirtualKeyShort.CONTROL
        || k == VirtualKeyShort.SHIFT
        || k == VirtualKeyShort.ALT
        || k == VirtualKeyShort.LWIN
        || k == VirtualKeyShort.RWIN;

    private static ExecutionResult Ok(string msg) => new(ExecutionStatus.Ok, msg);
    private static ExecutionResult Fail(string msg) => new(ExecutionStatus.Failed, msg);
}
