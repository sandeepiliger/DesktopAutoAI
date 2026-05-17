namespace DesktopAutoAI.UI.Services;

public sealed record ProcessListItem(int Pid, string ProcessName, string WindowTitle)
{
    /// <summary>What the user sees in the ComboBox.</summary>
    public string Display => $"{ProcessName}  -  {WindowTitle}  (pid {Pid})";
}
