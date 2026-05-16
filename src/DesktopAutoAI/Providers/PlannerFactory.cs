using DesktopAutoAI.Configuration;

namespace DesktopAutoAI.Providers;

public static class PlannerFactory
{
    public const string Anthropic = "anthropic";
    public const string Google = "google";

    public static IActionPlanner Create(PlannerSettings settings)
    {
        var provider = (settings.Provider ?? Anthropic).Trim().ToLowerInvariant();
        return provider switch
        {
            Anthropic => CreateAnthropic(settings.Anthropic),
            Google => CreateGemini(settings.Google),
            _ => throw new InvalidOperationException(
                $"Unknown planner provider '{settings.Provider}'. " +
                $"Supported: {Anthropic}, {Google}."),
        };
    }

    private static AnthropicPlanner CreateAnthropic(AnthropicSettings settings)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "ANTHROPIC_API_KEY environment variable is not set.");

        return new AnthropicPlanner(
            model: settings.Model,
            maxTokens: settings.MaxTokens,
            apiKey: apiKey);
    }

    private static GeminiPlanner CreateGemini(GoogleSettings settings)
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
            apiVersion: settings.ApiVersion);
    }
}
