using DesktopAutoAI.Configuration;

namespace DesktopAutoAI.Providers;

public static class PlannerFactory
{
    public const string Anthropic = "anthropic";
    public const string Google = "google";

    public static IActionPlanner Create(PlannerSettings settings, bool treeOnly = false)
    {
        var provider = (settings.Provider ?? Anthropic).Trim().ToLowerInvariant();
        return provider switch
        {
            Anthropic => CreateAnthropic(settings.Anthropic, treeOnly),
            Google => CreateGemini(settings.Google, treeOnly),
            _ => throw new InvalidOperationException(
                $"Unknown planner provider '{settings.Provider}'. " +
                $"Supported: {Anthropic}, {Google}."),
        };
    }

    private static AnthropicPlanner CreateAnthropic(AnthropicSettings settings, bool treeOnly)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "ANTHROPIC_API_KEY environment variable is not set.");

        return new AnthropicPlanner(
            model: settings.Model,
            maxTokens: settings.MaxTokens,
            apiKey: apiKey,
            treeOnly: treeOnly);
    }

    private static GeminiPlanner CreateGemini(GoogleSettings settings, bool treeOnly)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "GEMINI_API_KEY (or GOOGLE_API_KEY) environment variable is not set.");

        return new GeminiPlanner(
            model: settings.Model,
            maxTokens: settings.MaxTokens,
            apiKey: apiKey,
            timeoutSeconds: settings.TimeoutSeconds,
            apiVersion: settings.ApiVersion,
            treeOnly: treeOnly);
    }
}
