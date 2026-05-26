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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly string _systemPrompt;

    public string ProviderName => "anthropic";
    public string ModelId => _model;

    public AnthropicPlanner(string model, int maxTokens, string? apiKey, bool treeOnly = false)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        _model = model;
        _maxTokens = maxTokens > 0 ? maxTokens : 2048;
        _systemPrompt = PlannerPrompts.BuildSystemPrompt(treeOnly);
        _client = string.IsNullOrEmpty(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = apiKey };
    }

    public async Task<string> PingAsync(CancellationToken ct)
    {
        var msg = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 32,
                Messages =
                [
                    new MessageParam { Role = Role.User, Content = new MessageParamContent("Say 'pong'.") },
                ],
            },
            cancellationToken: ct);

        foreach (var block in msg.Content)
            if (block.TryPickText(out var text))
                return text.Text.Trim();
        return "(no text)";
    }

    public async Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct)
    {
        var userText = PlannerPrompts.BuildUserText(request);
        var hasImage = request.ScreenshotPng is { Length: > 0 };
        var screenshotBase64 = hasImage ? Convert.ToBase64String(request.ScreenshotPng!) : null;

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = _systemPrompt,
            Tools = [BuildTakeActionTool()],
            ToolChoice = new ToolChoice(new ToolChoiceTool { Name = PlannerPrompts.ToolName }),
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = new MessageParamContent(BuildUserContent(userText, screenshotBase64)),
                },
            ],
        };

        Log.Debug("Anthropic planner: model={Model}, tree_chars={TreeLen}, screenshot={ScreenshotState}, history={History}",
            _model, request.FilteredTreeJson.Length,
            hasImage ? "on" : "off (tree-only)", request.History.Count);

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);

        foreach (var block in response.Content)
        {
            if (block.TryPickToolUse(out var toolUse) && toolUse.Name == PlannerPrompts.ToolName)
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

    private static List<ContentBlockParam> BuildUserContent(string text, string? screenshotBase64)
    {
        var blocks = new List<ContentBlockParam>();
        if (!string.IsNullOrEmpty(screenshotBase64))
        {
            var image = new ImageBlockParam
            {
                Source = new ImageBlockParamSource(new Base64ImageSource
                {
                    MediaType = MediaType.ImagePng,
                    Data = screenshotBase64,
                }),
            };
            blocks.Add(new ContentBlockParam(image));
        }
        blocks.Add(new ContentBlockParam(new TextBlockParam { Text = text }));
        return blocks;
    }

    private static Tool BuildTakeActionTool()
    {
        using var doc = JsonDocument.Parse(PlannerPrompts.TakeActionSchemaJson);
        var root = doc.RootElement;
        var properties = new Dictionary<string, JsonElement>();
        foreach (var prop in root.GetProperty("properties").EnumerateObject())
            properties[prop.Name] = prop.Value.Clone();

        var required = new List<string>();
        foreach (var r in root.GetProperty("required").EnumerateArray())
            required.Add(r.GetString()!);

        return new Tool
        {
            Name = PlannerPrompts.ToolName,
            Description = PlannerPrompts.TakeActionDescription,
            InputSchema = new InputSchema
            {
                Properties = properties,
                Required = required,
            },
        };
    }
}
