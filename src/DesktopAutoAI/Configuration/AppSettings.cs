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
    public AnthropicSettings Anthropic { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();
}

public sealed class AnthropicSettings
{
    public string Model { get; set; } = "claude-opus-4-7";
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
