using System.Diagnostics;
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

    private readonly GoogleAI _googleAI;
    private readonly GenerativeModel _genModel;
    private GenerativeModel? _batchModel;
    private readonly string _modelId;
    private readonly int _maxTokens;
    private readonly TimeSpan _timeout;
    private readonly string? _apiVersion;

    public string ProviderName => "google";
    public string ModelId => _modelId;

    public GeminiPlanner(string model, int maxTokens, string apiKey, int timeoutSeconds, string? apiVersion = null, bool treeOnly = false)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));

        _modelId = model;
        _maxTokens = maxTokens > 0 ? maxTokens : 2048;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 120);
        _apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? null : apiVersion;

        _googleAI = new GoogleAI(apiKey: apiKey, apiVersion: _apiVersion);
        _genModel = _googleAI.GenerativeModel(
            model: model,
            systemInstruction: new Content(PlannerPrompts.BuildSystemPrompt(treeOnly), role: "system"));
        _genModel.Timeout = _timeout;
    }

    // Built lazily because most runs use only one mode (loop or batch).
    private GenerativeModel BatchModel()
    {
        if (_batchModel is not null) return _batchModel;
        _batchModel = _googleAI.GenerativeModel(
            model: _modelId,
            systemInstruction: new Content(BatchPlannerPrompts.SystemPrompt, role: "system"));
        _batchModel.Timeout = _timeout;
        return _batchModel;
    }

    public async Task<string> PingAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        try
        {
            var resp = await _genModel.GenerateContent(
                new GenerateContentRequest
                {
                    Contents = [new Content { Role = "user", Parts = [new Part { Text = "Say 'pong'." }] }],
                    GenerationConfig = new GenerationConfig { MaxOutputTokens = 32 },
                },
                cancellationToken: cts.Token);
            return (resp.Text ?? "(no text)").Trim();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Gemini ping did not respond within {(int)_timeout.TotalSeconds}s. " +
                $"Endpoint={Endpoint()}.");
        }
    }

    public async Task<PlannedAction> PlanNextAsync(PlanRequest request, CancellationToken ct)
    {
        var userText = PlannerPrompts.BuildUserText(request);
        var hasImage = request.ScreenshotPng is { Length: > 0 };

        var parts = new List<Part>();
        if (hasImage)
        {
            var screenshotBase64 = Convert.ToBase64String(request.ScreenshotPng!);
            parts.Add(new Part { InlineData = new InlineData { MimeType = "image/png", Data = screenshotBase64 } });
        }
        parts.Add(new Part { Text = userText });

        var userContent = new Content
        {
            Role = "user",
            Parts = parts,
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

        Log.Information(
            "Gemini call: model={Model}, apiVersion={ApiVersion}, timeout={Timeout}s, tree_chars={TreeLen}, screenshot={ScreenshotState}, history={History}",
            _modelId,
            _apiVersion ?? "(sdk default)",
            (int)_timeout.TotalSeconds,
            request.FilteredTreeJson.Length,
            hasImage ? $"on ({request.ScreenshotPng!.Length} bytes)" : "off (tree-only)",
            request.History.Count);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var sw = Stopwatch.StartNew();
        GenerateContentResponse response;
        try
        {
            response = await _genModel.GenerateContent(req, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Gemini did not respond within {(int)_timeout.TotalSeconds}s. " +
                $"Model={_modelId}. Check the model id, API version (try 'v1beta'), " +
                $"and that {Endpoint()} is reachable from this machine.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gemini SDK threw after {Ms}ms (model={Model})", sw.ElapsedMilliseconds, _modelId);
            throw;
        }
        sw.Stop();

        var calls = response.FunctionCalls?.Count ?? 0;
        var finish = response.Candidates?.FirstOrDefault()?.FinishReason;
        Log.Information(
            "Gemini responded in {Ms}ms (function_calls={Calls}, finish={Finish}, response_id={Id})",
            sw.ElapsedMilliseconds, calls, finish, response.ResponseId);

        var call = response.FunctionCalls?.FirstOrDefault(c => c.Name == PlannerPrompts.ToolName);
        if (call is null)
        {
            var text = response.Text;
            throw new InvalidOperationException(
                $"Gemini did not return a take_action function call. " +
                $"FinishReason={finish}. Text={text?.Substring(0, Math.Min(text?.Length ?? 0, 400))}");
        }

        var json = JsonSerializer.Serialize(call.Args, JsonOpts);
        Log.Debug("Gemini planner raw function args: {Json}", json);

        var planned = JsonSerializer.Deserialize<PlannedAction>(json, JsonOpts)
            ?? throw new InvalidOperationException("Gemini returned empty take_action args.");
        return planned;
    }

    public async Task<BatchPlan> PlanBatchAsync(BatchPlanRequest request, CancellationToken ct)
    {
        var userText = BatchPlannerPrompts.BuildUserText(request);

        var req = new GenerateContentRequest
        {
            Contents = [new Content { Role = "user", Parts = [new Part { Text = userText }] }],
            Tools = BuildBatchTools(),
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingConfigMode.Any,
                    AllowedFunctionNames = [BatchPlannerPrompts.ToolName],
                },
            },
            GenerationConfig = new GenerationConfig { MaxOutputTokens = _maxTokens },
        };

        Log.Information("Gemini batch plan: model={Model}, tree_chars={TreeLen}, repair={Repair}",
            _modelId, request.FilteredTreeJson.Length, request.FailureContext is not null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var sw = Stopwatch.StartNew();
        GenerateContentResponse response;
        try
        {
            response = await BatchModel().GenerateContent(req, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Gemini batch plan did not respond within {(int)_timeout.TotalSeconds}s. Model={_modelId}.");
        }
        sw.Stop();

        var finish = response.Candidates?.FirstOrDefault()?.FinishReason;
        Log.Information("Gemini batch responded in {Ms}ms (finish={Finish})", sw.ElapsedMilliseconds, finish);

        var call = response.FunctionCalls?.FirstOrDefault(c => c.Name == BatchPlannerPrompts.ToolName);
        if (call is null)
        {
            var text = response.Text;
            throw new InvalidOperationException(
                $"Gemini did not return a plan_actions function call. FinishReason={finish}. " +
                $"Text={text?.Substring(0, Math.Min(text?.Length ?? 0, 400))}");
        }

        var json = JsonSerializer.Serialize(call.Args, JsonOpts);
        Log.Debug("Gemini batch raw function args: {Json}", json);
        var plan = JsonSerializer.Deserialize<BatchPlan>(json, JsonOpts)
            ?? throw new InvalidOperationException("Gemini returned empty plan_actions args.");
        return plan;
    }

    private static Tools BuildBatchTools()
    {
        var schemaNode = JsonNode.Parse(BatchPlannerPrompts.SchemaJson)
            ?? throw new InvalidOperationException("plan_actions schema is invalid JSON.");

        return new Tools
        {
            new Tool
            {
                FunctionDeclarations =
                [
                    new FunctionDeclaration
                    {
                        Name = BatchPlannerPrompts.ToolName,
                        Description = BatchPlannerPrompts.Description,
                        ParametersJsonSchema = schemaNode,
                    },
                ],
            },
        };
    }

    private string Endpoint()
        => $"https://generativelanguage.googleapis.com/{_apiVersion ?? "v1beta"}/models/{_modelId}:generateContent";

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
