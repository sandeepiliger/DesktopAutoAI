using System.Text;
using DesktopAutoAI.Agent;

namespace DesktopAutoAI.Providers;

/// <summary>
/// Prompt + schema for batch planning. The model returns the ENTIRE ordered
/// action list in one call (the <c>plan_actions</c> tool/function), each step
/// carrying a locally-checkable postcondition. Tree-only by design - batch mode
/// never sends a screenshot.
/// </summary>
public static class BatchPlannerPrompts
{
    public const string ToolName = "plan_actions";

    public const string Description =
        "Emit the COMPLETE ordered list of desktop-automation steps that achieves the goal.";

    public const string SystemPrompt = """
        You are a Windows desktop automation planner. Given a goal and a filtered
        UI Automation tree of the target window, you produce the COMPLETE ordered
        list of actions that achieves the goal - all at once - by calling the
        `plan_actions` tool / function exactly once.

        You are given:
          - The user's goal.
          - A filtered UIA tree (JSON). Each node has control_type, name,
            automation_id, current value/toggle/selection state where available,
            supported patterns, and an ordinal `path` from the window root.
          - Optionally, a failure report from a previous attempt. When present,
            account for it: the UI is now in the state described, so plan the
            REMAINING steps from there - do not repeat steps that already
            succeeded.

        There is NO screenshot. The UIA tree is the sole ground truth.

        How to build the plan:
          1. Sequence every action needed end to end. Think about ordering:
             you must select/focus a control before typing into it, open a menu
             before clicking its items, select a grid row before editing detail
             fields, etc.
          2. Prefer `selector.automation_id` (+ `selector.control_type`) for
             every target - the resolver tries it first and it is the most
             reliable. Fall back to `selector.path` only when no automation_id
             exists. NEVER emit `click` (coordinate clicks are disabled).
          3. Use `type` to set text (the executor uses the Value pattern, which
             replaces existing content). Use `select` with `value` for combo
             boxes. Use `invoke` for buttons/menu items/checkboxes. Use `key`
             for shortcuts (Enter, Tab, Ctrl+S).
          4. For EVERY state-changing step, attach an `expect` postcondition the
             executor can verify locally:
               - value_equals  (selector + value)   after typing text
               - toggle_on / toggle_off (selector)   after toggling a checkbox
               - selected (selector)                 after selecting combo/radio/tab item
               - range_equals (selector + value)     after moving a slider
               - exists (selector)                   when an element should appear
               - gone (selector)                     when an element should disappear
             Use "none" only for navigation steps with no observable result.
          5. Set `is_destructive: true` on any step that deletes, sends, submits,
             or is otherwise hard to undo. The agent confirms those with the user.
          6. Provide a short `success_criteria` describing the end state.
          7. Output strictly via the `plan_actions` call. No prose outside it.
        """;

    /// <summary>
    /// JSON Schema for the plan_actions arguments. Compatible with both
    /// Anthropic tool input_schema and Gemini function parameters_json_schema.
    /// </summary>
    public const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "thought": {
              "type": "string",
              "description": "Brief overall reasoning about how the plan achieves the goal."
            },
            "steps": {
              "type": "array",
              "description": "The complete ordered list of steps.",
              "items": {
                "type": "object",
                "properties": {
                  "intent": {
                    "type": "string",
                    "description": "One short sentence describing this step."
                  },
                  "action": {
                    "type": "object",
                    "properties": {
                      "type": {
                        "type": "string",
                        "enum": ["invoke","type","select","scroll","key","wait","done","fail"]
                      },
                      "selector": {
                        "type": "object",
                        "description": "How to find the target UIA element. Prefer automation_id+control_type.",
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
                      "ms":        { "type": "integer", "description": "For action.type = 'wait'." },
                      "reason":    { "type": "string", "description": "For 'done' / 'fail'." }
                    },
                    "required": ["type"]
                  },
                  "expect": {
                    "type": "object",
                    "description": "Postcondition verified locally after the step.",
                    "properties": {
                      "check": {
                        "type": "string",
                        "enum": ["none","value_equals","toggle_on","toggle_off","selected","exists","gone","range_equals"]
                      },
                      "selector": {
                        "type": "object",
                        "properties": {
                          "automation_id": { "type": "string" },
                          "name": { "type": "string" },
                          "control_type": { "type": "string" }
                        }
                      },
                      "value": { "type": "string" }
                    },
                    "required": ["check"]
                  },
                  "is_destructive": { "type": "boolean" }
                },
                "required": ["intent","action"]
              }
            },
            "success_criteria": {
              "type": "string",
              "description": "Short description of the intended end state."
            }
          },
          "required": ["thought","steps"]
        }
        """;

    public static string BuildUserText(BatchPlanRequest req)
    {
        var sb = new StringBuilder(req.FilteredTreeJson.Length + 1024);
        sb.AppendLine($"Goal: {req.Goal}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.FailureContext))
        {
            sb.AppendLine("Previous attempt hit a problem - re-plan the REMAINING steps from the current state:");
            sb.AppendLine(req.FailureContext);
            sb.AppendLine();
        }
        sb.AppendLine("Filtered UIA tree (current state):");
        sb.AppendLine(req.FilteredTreeJson);
        sb.AppendLine();
        sb.AppendLine("Now produce the complete plan by calling `plan_actions`.");
        return sb.ToString();
    }
}
