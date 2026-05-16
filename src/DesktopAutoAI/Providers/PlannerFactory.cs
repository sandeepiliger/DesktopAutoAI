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
            Google => throw new NotImplementedException(
                "Google/Gemini provider lands in M2.5."),
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
}
