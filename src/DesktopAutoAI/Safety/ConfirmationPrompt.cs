using System.Text;
using Serilog;

namespace DesktopAutoAI.Safety;

/// <summary>
/// Console y/N prompt with an auto-deny timeout. The default is 15 seconds
/// so a hung run cannot sit forever waiting for input. Use <c>AutoConfirm</c>
/// in non-interactive scenarios (CI, scripts) where a human cannot answer.
/// </summary>
public sealed class ConfirmationPrompt
{
    private readonly TimeSpan _timeout;
    private readonly bool _autoConfirm;

    public ConfirmationPrompt(TimeSpan timeout, bool autoConfirm = false)
    {
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(15);
        _autoConfirm = autoConfirm;
    }

    public async Task<bool> ConfirmAsync(string summary, CancellationToken ct)
    {
        if (_autoConfirm)
        {
            Log.Warning("Auto-confirming destructive action (--yes was set).");
            Console.WriteLine();
            Console.WriteLine("==========  DESTRUCTIVE ACTION (auto-confirmed)  ==========");
            Console.WriteLine(summary);
            Console.WriteLine("===========================================================");
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Log.Warning("Destructive action requires confirmation but stdin is redirected. Denying.");
            Console.WriteLine();
            Console.WriteLine("==========  DESTRUCTIVE ACTION (auto-denied: no TTY)  ==========");
            Console.WriteLine(summary);
            Console.WriteLine("=================================================================");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("==========  DESTRUCTIVE ACTION CONFIRMATION  ==========");
        Console.WriteLine(summary);
        Console.Write($"Proceed? [y/N] (auto-deny in {_timeout.TotalSeconds:0}s): ");

        var input = await ReadLineWithTimeoutAsync(_timeout, ct);
        var ok = input is not null
              && (input.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
               || input.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine(ok ? "Confirmed. Proceeding." : "Denied. Aborting action.");
        Console.WriteLine("========================================================");
        return ok;
    }

    // Poll Console.KeyAvailable so we can respect both the timeout and the
    // external cancellation token (kill switch). Console.In.ReadLine() would
    // block uninterruptibly.
    private static async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var sb = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return null;
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: false);
                if (key.Key == ConsoleKey.Enter) return sb.ToString();
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
                continue;
            }
            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { return null; }
        }
        Console.WriteLine();
        Console.WriteLine("(timeout)");
        return null;
    }
}
