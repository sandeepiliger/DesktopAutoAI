using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace DesktopAutoAI.Safety;

/// <summary>
/// Global hotkey (default <c>Ctrl+Shift+Backspace</c>) that signals a
/// <see cref="CancellationTokenSource"/>. The loop and the executor observe
/// the token, so pressing the hotkey aborts the run regardless of which
/// window currently has focus.
///
/// Implementation: <see cref="RegisterHotKey"/> with <c>hWnd = NULL</c>
/// posts <c>WM_HOTKEY</c> to the calling thread's message queue. We pump
/// messages on a dedicated background thread; no window is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KillSwitch : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT   = 0x0012;
    private const int MOD_ALT     = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT   = 0x0004;
    private const int MOD_WIN     = 0x0008;
    private const int HOTKEY_ID   = 0xC001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _hotkeyDisplay;
    private uint _threadId;
    private bool _disposed;

    public CancellationToken Token => _cts.Token;
    public string Hotkey => _hotkeyDisplay;

    public KillSwitch(string hotkeySpec = "Ctrl+Shift+Backspace")
    {
        _hotkeyDisplay = hotkeySpec;
        var (mods, vk) = ParseHotkey(hotkeySpec);

        _thread = new Thread(() => Pump(mods, vk))
        {
            IsBackground = true,
            Name = "KillSwitch",
        };
        _thread.Start();
        _ready.Wait();
    }

    private void Pump(int mods, int vk)
    {
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, mods, vk))
        {
            Log.Warning("Failed to register kill switch hotkey '{Hotkey}' (Win32 error {Err}). " +
                        "Hotkey will not work; other safety checks still apply.",
                _hotkeyDisplay, Marshal.GetLastWin32Error());
            _ready.Set();
            return;
        }
        Log.Information("Kill switch armed: press {Hotkey} at any time to cancel the run.", _hotkeyDisplay);
        _ready.Set();

        while (true)
        {
            var ret = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (ret == 0) break;                    // WM_QUIT
            if (ret == -1)
            {
                Log.Warning("GetMessage returned -1 (error {Err}); stopping kill switch.",
                    Marshal.GetLastWin32Error());
                break;
            }
            if (msg.message == WM_HOTKEY && (int)msg.wParam == HOTKEY_ID)
            {
                Log.Warning("Kill switch pressed ({Hotkey}). Cancelling run.", _hotkeyDisplay);
                _cts.Cancel();
                break;
            }
        }
        UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(1));
        }
        _cts.Dispose();
        _ready.Dispose();
    }

    // Parses strings like "Ctrl+Shift+Backspace", "Alt+F12", "Win+Space".
    private static (int Modifiers, int VirtualKey) ParseHotkey(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("Hotkey spec is empty.", nameof(spec));

        int mods = 0;
        int? vk = null;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "alt": case "menu": mods |= MOD_ALT; break;
                case "win": case "lwin": case "rwin": mods |= MOD_WIN; break;
                default:
                    if (vk is not null)
                        throw new ArgumentException($"Hotkey '{spec}' has more than one non-modifier key.", nameof(spec));
                    vk = ParseVirtualKey(raw);
                    break;
            }
        }
        if (vk is null)
            throw new ArgumentException($"Hotkey '{spec}' has no non-modifier key.", nameof(spec));
        return (mods, vk.Value);
    }

    private static int ParseVirtualKey(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "backspace": case "back": return 0x08;
            case "tab": return 0x09;
            case "enter": case "return": return 0x0D;
            case "escape": case "esc": return 0x1B;
            case "space": return 0x20;
            case "pageup": case "pgup": return 0x21;
            case "pagedown": case "pgdn": return 0x22;
            case "end": return 0x23;
            case "home": return 0x24;
            case "left": return 0x25;
            case "up": return 0x26;
            case "right": return 0x27;
            case "down": return 0x28;
            case "delete": case "del": return 0x2E;
        }
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return 0x70 + (fn - 1);
        }
        throw new ArgumentException($"Unknown virtual key '{name}' in hotkey spec.", nameof(name));
    }
}
