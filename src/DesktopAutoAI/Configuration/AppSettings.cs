namespace DesktopAutoAI.Configuration;

public sealed class AppSettings
{
    public LoggingSettings Logging { get; set; } = new();
    public PlannerSettings Planner { get; set; } = new();
}

public sealed class LoggingSettings
{
    public string? LogDirectory { get; set; }
    public string MinimumLevel { get; set; } = "Information";
}

public sealed class PlannerSettings
{
    public string Provider { get; set; } = "anthropic";
    public int MaxSteps { get; set; } = 25;
    public AnthropicSettings Anthropic { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();
}

public sealed class AnthropicSettings
{
    public string Model { get; set; } = "claude-opus-4-7";
    public int MaxTokens { get; set; } = 2048;
}

public sealed class GoogleSettings
{
    public string Model { get; set; } = "gemini-2.5-pro";
    public int MaxTokens { get; set; } = 2048;
}
