using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using DesktopAutoAI.Agent;
using Serilog;

namespace DesktopAutoAI.Providers;

/// <summary>
/// Anthropic implementation of <see cref="IActionPlanner"/>. Forces the model
/// into a single <c>take_action</c> tool call so the response is strongly typed.
/// </summary>
public sealed class AnthropicPlanner : IActionPlanner
{
    private const string ToolName = "take_action";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly int _maxTokens;

    public string ProviderName => "anthropic";
    public string ModelId => _model;

    public AnthropicPlanner(string model, int maxTokens, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        _model = model;
        _maxTokens = maxTokens > 0 ? maxTokens : 2048;
        _client = string.IsNullOrEmpty(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = apiKey };
    }

    public async Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct)
    {
        var screenshotBase64 = Convert.ToBase64String(request.ScreenshotPng);
        var userText = BuildUserText(request);

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = SystemPrompt,
            Tools = [BuildTakeActionTool()],
            ToolChoice = new ToolChoice(new ToolChoiceTool { Name = ToolName }),
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = new MessageParamContent(BuildUserContent(userText, screenshotBase64)),
                },
            ],
        };

        Log.Debug("Anthropic planner: model={Model}, tree_chars={TreeLen}, history={History}",
            _model, request.FilteredTreeJson.Length, request.History.Count);

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);

        foreach (var block in response.Content)
        {
            if (block.TryPickToolUse(out var toolUse) && toolUse.Name == ToolName)
            {
                var json = JsonSerializer.Serialize(toolUse.Input);
                Log.Debug("Anthropic planner raw tool input: {Json}", json);
                var planned = JsonSerializer.Deserialize<PlannedAction>(json, JsonOpts)
                    ?? throw new InvalidOperationException(
                        "Anthropic returned an empty take_action payload.");
                return planned;
            }
        }

        throw new InvalidOperationException(
            "Anthropic did not return a take_action tool call. " +
            $"StopReason={response.StopReason}");
    }

    private const string SystemPrompt = """
        You are a Windows desktop automation agent. You operate a real Windows app
        by emitting ONE structured action at a time via the `take_action` tool.

        You are given:
          - The user's goal.
          - A filtered UI Automation tree (JSON) of the target window. Each node
            has control_type, name, automation_id, bounding_rect, supported
            patterns, and an ordinal `path` from the window root.
          - A current screenshot of the target window.
          - The recent action history (most recent last) and what each one returned.

        Rules:
          1. Prefer UIA selectors over coordinate clicks. Use `point` only as a
             last resort and explain why in `thought`.
          2. Build `selector.path` as the ordinal path of typed hops from the tree.
             If a node has an `automation_id`, set `selector.automation_id` and
             `selector.control_type` instead — the resolver tries those first.
          3. Use `type` to type text into a focused editable control. The
             control must be reached first via a prior `invoke` or `key Tab`.
          4. Use `key` for keyboard shortcuts like `Ctrl+S`, `Enter`, `Tab`, `F2`.
          5. When the goal is achieved, return action.type = "done" with a reason.
             If you cannot make progress, return action.type = "fail" with a reason.
          6. Set `is_destructive: true` when the action would delete data, send a
             message, submit a form, or otherwise be hard to undo. The agent will
             confirm with the user before executing.
          7. Output strictly via the `take_action` tool. No prose outside it.
        """;

    private static string BuildUserText(PlanRequest req)
    {
        var sb = new StringBuilder(req.FilteredTreeJson.Length + 1024);
        sb.AppendLine($"Goal: {req.Goal}");
        sb.AppendLine();
        if (req.History.Count == 0)
        {
            sb.AppendLine("Action history: (empty — this is step 1)");
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

    private static List<ContentBlockParam> BuildUserContent(string text, string screenshotBase64)
    {
        var image = new ImageBlockParam
        {
            Source = new ImageBlockParamSource(new Base64ImageSource
            {
                MediaType = MediaType.ImagePng,
                Data = screenshotBase64,
            }),
        };
        return
        [
            new ContentBlockParam(image),
            new ContentBlockParam(new TextBlockParam { Text = text }),
        ];
    }

    private static Tool BuildTakeActionTool()
    {
        var schemaJson = """
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

        using var doc = JsonDocument.Parse(schemaJson);
        var root = doc.RootElement;
        var properties = new Dictionary<string, JsonElement>();
        foreach (var prop in root.GetProperty("properties").EnumerateObject())
        {
            properties[prop.Name] = prop.Value.Clone();
        }
        var required = new List<string>();
        foreach (var r in root.GetProperty("required").EnumerateArray())
            required.Add(r.GetString()!);

        return new Tool
        {
            Name = ToolName,
            Description = "Emit exactly one next desktop-automation action.",
            InputSchema = new InputSchema
            {
                Properties = properties,
                Required = required,
            },
        };
    }
}
