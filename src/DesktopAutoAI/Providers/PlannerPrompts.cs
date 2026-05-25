using System.Text;

namespace DesktopAutoAI.Providers;

/// <summary>
/// Provider-agnostic prompt + schema strings used by every concrete
/// <see cref="IActionPlanner"/>. Keeping these in one place is what makes
/// the Anthropic and Gemini planners interchangeable from the agent's
/// point of view.
/// </summary>
public static class PlannerPrompts
{
    public const string ToolName = "take_action";

    /// <summary>
    /// Returns the system prompt to use for this run. When <paramref name="treeOnly"/>
    /// is true the agent operates on the UIA tree alone — no screenshot is sent and
    /// coordinate clicks are rejected by the executor.
    /// </summary>
    public static string BuildSystemPrompt(bool treeOnly)
        => treeOnly ? SystemPromptTreeOnly : SystemPrompt;

    public const string SystemPrompt = """
        You are a Windows desktop automation agent. You operate a real Windows
        app by emitting ONE structured action at a time via the `take_action`
        tool / function.

        You are given:
          - The user's goal.
          - A filtered UI Automation tree (JSON) of the target window. Each
            node has control_type, name, automation_id, bounding_rect,
            supported patterns, and an ordinal `path` from the window root.
          - A current screenshot of the target window.
          - The recent action history (most recent last) and what each one
            returned.

        Rules:
          1. Prefer UIA selectors over coordinate clicks. Use `point` only as
             a last resort and explain why in `thought`.
          2. Build `selector.path` as the ordinal path of typed hops from the
             tree. If a node has an `automation_id`, set
             `selector.automation_id` and `selector.control_type` instead -
             the resolver tries those first.
          3. Use `type` to type text into a focused editable control. The
             control must be reached first via a prior `invoke` or `key Tab`.
          4. Use `key` for keyboard shortcuts like `Ctrl+S`, `Enter`, `Tab`,
             `F2`.
          5. When the goal is achieved, return action.type = "done" with a
             reason. If you cannot make progress, return action.type =
             "fail" with a reason.
          6. Set `is_destructive: true` when the action would delete data,
             send a message, submit a form, or otherwise be hard to undo.
             The agent will confirm with the user before executing.
          7. Output strictly via the `take_action` tool / function call.
             No prose outside it.
          8. Windows file dialogs (Save As / Open): the MOST RELIABLE way
             to save to a specific folder is to type the FULL PATH directly
             into the "File name" Edit box and press Save / Enter -
             Windows accepts e.g. `C:\Users\<user>\Desktop\test.txt` and
             navigates + names the file in one shot. Do NOT rely on
             Desktop / Documents / Downloads being pinned in the left
             navigation pane; they often are not. If you do not know the
             username, `%USERPROFILE%\Desktop\test.txt` also works.
          9. Do not repeat an action that just failed. If the previous
             step's result was "failed" or the same target is still
             unreachable, switch strategy: try a different selector, a
             keyboard shortcut (Alt+D to focus the address bar, Ctrl+L,
             Enter to confirm), or type a path directly into the relevant
             Edit box. Re-issuing the same failing action wastes a step.
        """;

    public const string SystemPromptTreeOnly = """
        You are a Windows desktop automation agent. You operate a real Windows
        app by emitting ONE structured action at a time via the `take_action`
        tool / function.

        You are given:
          - The user's goal.
          - A filtered UI Automation tree (JSON) of the target window. Each
            node has control_type, name, automation_id, supported patterns,
            and an ordinal `path` from the window root.
          - The recent action history (most recent last) and what each one
            returned.

        NO screenshot is provided in this mode. The UIA tree is the sole
        ground truth. After each action the tree is re-dumped so you can
        observe state changes on the next step.

        Rules:
          1. NEVER emit `click`. Coordinate clicks are disabled in this mode
             and the executor will refuse them. Resolve targets via
             `selector.automation_id` (preferred) or `selector.path`.
          2. Build `selector.path` as the ordinal path of typed hops from the
             tree. If a node has an `automation_id`, set
             `selector.automation_id` and `selector.control_type` instead -
             the resolver tries those first.
          3. Use `type` to type text into a focused editable control. The
             control must be reached first via a prior `invoke` or `key Tab`.
          4. Use `key` for keyboard shortcuts like `Ctrl+S`, `Enter`, `Tab`,
             `F2`.
          5. When the goal is achieved, return action.type = "done" with a
             reason. If you cannot make progress, return action.type =
             "fail" with a reason.
          6. Set `is_destructive: true` when the action would delete data,
             send a message, submit a form, or otherwise be hard to undo.
             The agent will confirm with the user before executing.
          7. Output strictly via the `take_action` tool / function call.
             No prose outside it.
          8. Windows file dialogs (Save As / Open): the MOST RELIABLE way
             to save to a specific folder is to type the FULL PATH directly
             into the "File name" Edit box and press Save / Enter -
             Windows accepts e.g. `C:\Users\<user>\Desktop\test.txt` and
             navigates + names the file in one shot. Do NOT rely on
             Desktop / Documents / Downloads being pinned in the left
             navigation pane; they often are not. If you do not know the
             username, `%USERPROFILE%\Desktop\test.txt` also works.
          9. Do not repeat an action that just failed. If the previous
             step's result was "failed" or the same target is still
             unreachable, switch strategy: try a different selector, a
             keyboard shortcut (Alt+D to focus the address bar, Ctrl+L,
             Enter to confirm), or type a path directly into the relevant
             Edit box. Re-issuing the same failing action wastes a step.
        """;

    public const string TakeActionDescription =
        "Emit exactly one next desktop-automation action.";

    /// <summary>
    /// JSON Schema (subset compatible with both Anthropic tool input_schema
    /// and Gemini function_declaration.parameters_json_schema).
    /// </summary>
    public const string TakeActionSchemaJson = """
        {
          "type": "object",
          "properties": {
            "thought": {
              "type": "string",
              "description": "One or two sentences explaining the next action."
            },
            "action": {
              "type": "object",
              "properties": {
                "type": {
                  "type": "string",
                  "enum": ["invoke","type","select","scroll","key","click","wait","done","fail"]
                },
                "selector": {
                  "type": "object",
                  "description": "How to find the target UIA element. Provide `path` OR `automation_id`+`control_type` OR `name`+`control_type`.",
                  "properties": {
                    "path": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "control_type": { "type": "string" },
                          "name": { "type": "string" },
                          "automation_id": { "type": "string" },
                          "index": { "type": "integer" }
                        },
                        "required": ["control_type"]
                      }
                    },
                    "automation_id": { "type": "string" },
                    "name": { "type": "string" },
                    "control_type": { "type": "string" }
                  }
                },
                "text":      { "type": "string", "description": "For action.type = 'type'." },
                "value":     { "type": "string", "description": "For action.type = 'select'." },
                "direction": { "type": "string", "enum": ["up","down","left","right"] },
                "amount":    { "type": "integer", "description": "Scroll wheel notches." },
                "key":       { "type": "string", "description": "Keys like 'Enter', 'Tab', 'Ctrl+S'." },
                "point": {
                  "type": "object",
                  "properties": {
                    "x": { "type": "integer" },
                    "y": { "type": "integer" }
                  },
                  "required": ["x","y"],
                  "description": "Last-resort coordinate click."
                },
                "ms":     { "type": "integer", "description": "For action.type = 'wait'." },
                "reason": { "type": "string", "description": "Required for 'done' / 'fail'." }
              },
              "required": ["type"]
            },
            "is_destructive": {
              "type": "boolean",
              "description": "Set true for delete/send/submit/etc."
            }
          },
          "required": ["thought","action"]
        }
        """;

    public static string BuildUserText(PlanRequest req)
    {
        var sb = new StringBuilder(req.FilteredTreeJson.Length + 1024);
        sb.AppendLine($"Goal: {req.Goal}");
        sb.AppendLine();
        if (req.History.Count == 0)
        {
            sb.AppendLine("Action history: (empty - this is step 1)");
        }
        else
        {
            sb.AppendLine("Action history (most recent last):");
            foreach (var h in req.History)
            {
                sb.AppendLine($"  step {h.Step}: {h.Action.Type} -> {h.Result}");
                if (!string.IsNullOrWhiteSpace(h.Thought))
                    sb.AppendLine($"    thought: {h.Thought}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Filtered UIA tree:");
        sb.AppendLine(req.FilteredTreeJson);
        sb.AppendLine();
        sb.AppendLine("Now choose exactly ONE next action by calling `take_action`.");
        return sb.ToString();
    }
}
