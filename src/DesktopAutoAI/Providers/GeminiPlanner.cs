using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAutoAI.Agent;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using Serilog;

namespace DesktopAutoAI.Providers;

/// <summary>
/// Google Gemini implementation of <see cref="IActionPlanner"/>. Forces the model
/// into a single <c>take_action</c> function call so the response is strongly typed.
/// Behaviorally identical to <see cref="AnthropicPlanner"/>; the only differences
/// are SDK-shaped request/response plumbing.
/// </summary>
public sealed class GeminiPlanner : IActionPlanner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly GenerativeModel _genModel;
    private readonly string _modelId;
    private readonly int _maxTokens;

    public string ProviderName => "google";
    public string ModelId => _modelId;

    public GeminiPlanner(string model, int maxTokens, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));

        _modelId = model;
        _maxTokens = maxTokens > 0 ? maxTokens : 2048;

        var googleAI = new GoogleAI(apiKey: apiKey);
        _genModel = googleAI.GenerativeModel(
            model: model,
            systemInstruction: new Content(PlannerPrompts.SystemPrompt, role: "system"));
    }

    public async Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct)
    {
        var screenshotBase64 = Convert.ToBase64String(request.ScreenshotPng);
        var userText = PlannerPrompts.BuildUserText(request);

        var userContent = new Content
        {
            Role = "user",
            Parts =
            [
                new Part { InlineData = new InlineData { MimeType = "image/png", Data = screenshotBase64 } },
                new Part { Text = userText },
            ],
        };

        var req = new GenerateContentRequest
        {
            Contents = [userContent],
            Tools = BuildTools(),
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingConfigMode.Any,
                    AllowedFunctionNames = [PlannerPrompts.ToolName],
                },
            },
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = _maxTokens,
            },
        };

        Log.Debug("Gemini planner: model={Model}, tree_chars={TreeLen}, history={History}",
            _modelId, request.FilteredTreeJson.Length, request.History.Count);

        var response = await _genModel.GenerateContent(req, cancellationToken: ct);

        var call = response.FunctionCalls?.FirstOrDefault(c => c.Name == PlannerPrompts.ToolName);
        if (call is null)
        {
            var finish = response.Candidates?.FirstOrDefault()?.FinishReason;
            throw new InvalidOperationException(
                $"Gemini did not return a take_action function call. FinishReason={finish}");
        }

        var json = JsonSerializer.Serialize(call.Args, JsonOpts);
        Log.Debug("Gemini planner raw function args: {Json}", json);

        var planned = JsonSerializer.Deserialize<PlannedAction>(json, JsonOpts)
            ?? throw new InvalidOperationException("Gemini returned empty take_action args.");
        return planned;
    }

    private static Tools BuildTools()
    {
        var schemaNode = JsonNode.Parse(PlannerPrompts.TakeActionSchemaJson)
            ?? throw new InvalidOperationException("take_action schema is invalid JSON.");

        return new Tools
        {
            new Tool
            {
                FunctionDeclarations =
                [
                    new FunctionDeclaration
                    {
                        Name = PlannerPrompts.ToolName,
                        Description = PlannerPrompts.TakeActionDescription,
                        ParametersJsonSchema = schemaNode,
                    },
                ],
            },
        };
    }
}
