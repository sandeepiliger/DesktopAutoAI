namespace DesktopAutoAI.Configuration;

public sealed class AppSettings
{
    public LoggingSettings Logging { get; set; } = new();
    public PlannerSettings Planner { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
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
    /// <summary>How many recent history entries to send to the LLM per step.
    /// Caps token growth on long runs.</summary>
    public int HistoryWindow { get; set; } = 10;

    /// <summary>Planning strategy: "loop" (one LLM call per step, can use a
    /// screenshot) or "batch" (one call for the whole task, verify each step
    /// locally, repair on divergence). Default "loop" for backwards
    /// compatibility; the UI toggle / --batch flag select per run.</summary>
    public string Strategy { get; set; } = "loop";

    /// <summary>Batch mode only: how many times the loop may re-plan from the
    /// current state when a step fails verification before giving up.</summary>
    public int MaxRepairs { get; set; } = 3;

    /// <summary>Batch mode only: optional model id used for the (single, more
    /// expensive) planning call. Must be valid for the configured provider.
    /// When null, the provider's normal Model is used. Set this to a stronger
    /// model (e.g. gemini-2.5-pro) for higher accuracy at negligible cost,
    /// since batch makes just one call.</summary>
    public string? PlanModel { get; set; }

    public AnthropicSettings Anthropic { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();
}

public static class PlannerStrategies
{
    public const string Loop = "loop";
    public const string Batch = "batch";
}

public sealed class AnthropicSettings
{
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class GoogleSettings
{
    public string Model { get; set; } = "gemini-2.5-flash";
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Optional override for the Gemini REST API version (e.g. "v1beta", "v1").
    /// Leave null to use the SDK's default.
    /// </summary>
    public string? ApiVersion { get; set; }
}

public sealed class SafetySettings
{
    /// <summary>Master switch for destructive-action detection + prompt.
    /// Kill switch is governed independently.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Register the global abort hotkey at startup.</summary>
    public bool KillSwitchEnabled { get; set; } = true;

    /// <summary>Global hotkey that signals cancellation. Examples:
    /// "Ctrl+Shift+Backspace", "Alt+F12", "Win+Pause".</summary>
    public string KillSwitchHotkey { get; set; } = "Ctrl+Shift+Backspace";

    /// <summary>Override the regex list used by DestructiveDetector. Empty
    /// list = use built-in defaults (delete/remove/send/submit/drop/...).</summary>
    public List<string> DestructivePatterns { get; set; } = new();

    /// <summary>How long the confirmation prompt waits before auto-denying.</summary>
    public int ConfirmationTimeoutSeconds { get; set; } = 15;
}
