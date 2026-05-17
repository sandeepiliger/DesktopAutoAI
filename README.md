# DesktopAutoAI

A Windows desktop automation agent in C# / .NET 8. Give it a natural-language
goal — "open the File menu, type 'hello world', save as `test.txt` on
Desktop" — and it drives any UI Automation-compliant Windows app to make that
happen. UIA is the primary effector (FlaUI walks the tree and invokes
elements); a vision-capable LLM (Claude or Gemini) plans the next action from
a screenshot plus a serialised tree.

The agent runs in two shells:

- **`DesktopAutoAI.exe`** — a CLI with `dump` / `step` / `run` / `ping`
  subcommands. Good for scripting, CI, and one-off tasks.
- **`DesktopAutoAI.UI.exe`** — a WPF window with a target picker, prompt
  textbox, run/stop buttons, and a live log viewer. Good for interactive use.

Both surfaces drive the same engine — same planner, same UIA executor, same
skills cache, same safety gate.

## What it does

- **Plans with vision + UIA.** Each step captures a screenshot and a
  filtered UIA tree of the target window, sends both to the LLM, and asks
  for exactly one next action (`invoke`, `type`, `select`, `scroll`, `key`,
  `click`, `wait`, `done`, `fail`) via a JSON-schema-typed tool call.
- **Executes via FlaUI.** Each action is dispatched against the live UIA
  tree — `InvokePattern`, `ValuePattern`, `SelectionItemPattern`,
  `ScrollItemPattern`, etc. — with a coordinate-click escape hatch when no
  selector works. Stale-element retries kick in once before giving up.
- **Multi-step loop.** Re-captures world state every step, accumulates a
  compact text-only history (no screenshot bloat), stops on `done` / `fail`
  / step-limit / planner-error / user-denied.
- **Skills cache.** A successful run is hashed by `(app, normalised goal)`
  and saved to `%LOCALAPPDATA%\DesktopAutoAI\skills\<hash>.json`. The next
  invocation of the same goal replays the saved sequence with zero LLM
  calls. On any replay failure the agent silently falls back to live
  planning and re-records on success.
- **Safety layer.**
  - Global kill-switch hotkey (default `Ctrl+Shift+Backspace`) cancels the
    run from anywhere on the desktop.
  - Destructive-action detector flags actions matching configurable regex
    patterns (`delete`, `remove`, `send`, `submit`, `drop`, `format`,
    `discard`, `overwrite`, `uninstall`, `erase`) plus anything the model
    itself flags as `is_destructive: true`. Each flagged action gets a
    console / log prompt with an auto-deny timeout.
  - Password redaction: when a `type` action targets an `IsPassword=true`
    Edit control, the text is redacted in logs, artefacts, and history —
    and the cache save is skipped entirely.
- **Model-agnostic.** A small `IActionPlanner` interface abstracts the LLM
  call. Switching between **Anthropic Claude** and **Google Gemini** is a
  one-line change in `appsettings.json`.

## Requirements

- Windows 10 / 11
- .NET 8 SDK
- An API key for at least one planner:
  - `ANTHROPIC_API_KEY` for Claude (default)
  - `GEMINI_API_KEY` for Gemini

## Build

```powershell
git clone https://github.com/sandeepiliger/DesktopAutoAI.git
cd DesktopAutoAI
dotnet build DesktopAutoAI.sln -c Release
```

Output:

- `src\DesktopAutoAI\bin\Release\net8.0-windows\DesktopAutoAI.exe` — CLI
- `src\DesktopAutoAI.UI\bin\Release\net8.0-windows\DesktopAutoAI.UI.exe` — WPF UI

## Quick start (WPF UI)

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
.\src\DesktopAutoAI.UI\bin\Release\net8.0-windows\DesktopAutoAI.UI.exe
```

1. Open the target app yourself (e.g. Notepad).
2. In the UI, click **Refresh**, pick the target from the dropdown (or click
   **Pick foreground...** to grab whatever window is currently focused).
3. Type a prompt in the textbox, press **Ctrl+Enter** (or click **Run**).
4. Watch the log stream in real time. Click **Stop** to cancel.
5. Type the next prompt and run again — each click is one planning loop
   against the selected target.

Checkboxes:

- **Use cache** — look up and save successful action sequences.
- **Safety on** — arm kill-switch + prompt before destructive actions.
- **Auto-confirm** — answer "yes" to every safety prompt for this run.

## Quick start (CLI)

```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."

# One planning turn (no loop). Good for testing the planner.
.\DesktopAutoAI.exe step --process Notepad --goal "Open the File menu"

# Full multi-step run.
.\DesktopAutoAI.exe run  --process Notepad --goal "Type 'hello world' and save as test.txt on Desktop"

# Trivial round-trip to check connectivity / auth / model id.
.\DesktopAutoAI.exe ping

# Dump the filtered UIA tree + screenshot to disk (no LLM call).
.\DesktopAutoAI.exe dump --process Notepad --out .\out
```

### `run` flags

| Flag | Effect |
|---|---|
| `--process <name>` | Target process name (without `.exe`). Required. |
| `--goal "..."` | Natural-language goal. Required. |
| `--out <dir>` | Where to write per-step artefacts (default `.\out`). |
| `--max-steps N` | Override `Planner.MaxSteps` from config. |
| `--no-cache` | Skip cache lookup AND skip saving on success. |
| `--no-safety` | Disable kill switch and destructive prompts. |
| `--yes` | Auto-confirm every destructive prompt. |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | `Done` |
| 2 | `Failed` (model gave up) |
| 3 | `MaxSteps` reached |
| 4 | `PlannerError` (LLM call threw) |
| 5 | `Denied` (user said no to a destructive prompt) |
| 130 | `Cancelled` (kill-switch / Stop button / Ctrl+C) |

## Configuration

Edit `appsettings.json` in either app's output folder:

```json
{
  "Logging":   { "MinimumLevel": "Information" },
  "Planner": {
    "Provider": "anthropic",
    "MaxSteps": 25,
    "Anthropic": { "Model": "claude-opus-4-7",  "MaxTokens": 2048, "TimeoutSeconds": 120 },
    "Google":    { "Model": "gemini-2.5-flash", "MaxTokens": 2048, "TimeoutSeconds": 120, "ApiVersion": null }
  },
  "Safety": {
    "Enabled": true,
    "KillSwitchEnabled": true,
    "KillSwitchHotkey": "Ctrl+Shift+Backspace",
    "DestructivePatterns": [],
    "ConfirmationTimeoutSeconds": 15
  }
}
```

Switching providers:

```jsonc
// Use Gemini instead of Claude
"Provider": "google"
```

Custom kill-switch hotkey:

```jsonc
"KillSwitchHotkey": "Ctrl+Alt+K"
```

Custom destructive patterns (overrides the defaults):

```jsonc
"DestructivePatterns": ["\\bpurge\\b", "\\bwipe\\b"]
```

## Output / state locations

| Path | What |
|---|---|
| `%LOCALAPPDATA%\DesktopAutoAI\logs\auto-YYYYMMDD.log` | Daily rolling log file |
| `%LOCALAPPDATA%\DesktopAutoAI\skills\<sha256>.json` | Cached action sequences |
| `%LOCALAPPDATA%\DesktopAutoAI\runs\<timestamp>\` | Per-run artefacts when launched from the UI |
| `.\out\` (or `--out <dir>`) | Per-run artefacts when launched from the CLI |

Per-step artefacts include `tree-NN.json`, `shot-NN.png`, `action-NN.json`,
plus a final `history.json`.

## Architecture

```
        +-----------------+         +-------------------+
        | DesktopAutoAI   |         | DesktopAutoAI.UI  |
        | (CLI)           |         | (WPF window)      |
        +-------+---------+         +---------+---------+
                |                             |
                +---------------+-------------+
                                |
                                v
                +-------------------------------+
                | PlanningLoop                  |
                |  - capture tree + screenshot  |
                |  - ask IActionPlanner         |
                |  - SafetyGate.AllowAsync      |
                |  - ActionExecutor.Execute     |
                +---------------+---------------+
                                |
       +-----------+------------+-----------+-----------+
       v           v            v           v           v
  IActionPlanner  ActionExecutor  SkillsCache  SafetyGate  KillSwitch
  (Anthropic |   (FlaUI:         (file:        (regex +    (RegisterHotKey
   Google)    Invoke/Type/...    %LOCALAPPDATA% console     + msg pump)
                                 \skills\)     prompt)
```

- **`IActionPlanner`** — `AnthropicPlanner` and `GeminiPlanner` both
  implement the same interface. Each provider coerces the model's reply
  into the shared `AgentAction` schema.
- **`PlanningLoop`** — orchestrates one full run. Stops on `Done` / `Failed`
  / `MaxSteps` / `PlannerError` / `Denied` / `Cancelled`.
- **`ActionExecutor`** — dispatches actions through FlaUI. Calls
  `ScrollItemPattern.ScrollIntoView()` before invoking virtualised items.
  Detects `IsPassword=true` and surfaces it via
  `ExecutionResult.WasPasswordField` for downstream redaction.
- **`SkillsCache`** — `sha256(app : normalised_goal)` → action list.
  Replays on hit; live-plans + saves on miss.
- **`SafetyGate`** — combines `DestructiveDetector` (regex over selector
  metadata + action text/key) and `ConfirmationPrompt` (console y/N with
  timeout). Gates both the live loop and cache replay.
- **`KillSwitch`** — `RegisterHotKey(hWnd=NULL)` plus a dedicated
  message-pump thread; signals a `CancellationTokenSource` observed by the
  loop and executor.

## Adding a third provider

Implement `IActionPlanner` (`PlanNextAsync` + `PingAsync`), register the new
class in `PlannerFactory`, add a settings POCO and an `appsettings.json`
section. Nothing else needs to change — the rest of the agent talks only to
the interface.

## Troubleshooting

- **"No running process named 'X'"** — start the target app first; the CLI
  attaches by process name, not by launching.
- **"Could not find a main window"** — wait until the target's main window
  is visible before invoking `run` / `step`.
- **Ping works, run hangs on the planner** — usually a vision-model timeout
  on a 4K window. The built-in 1280-px screenshot downscale is already on;
  if it still hangs, lower `MaxTokens` or switch to a faster model
  (e.g. `gemini-2.5-flash`).
- **Cache replay always falls back to live** — the saved selectors are
  stale (app version changed). Delete the matching `<hash>.json` file or
  run with `--no-cache`; the next successful live run overwrites it.
- **Kill-switch hotkey doesn't fire** — something else owns
  `Ctrl+Shift+Backspace`; change `Safety:KillSwitchHotkey` to a free combo.

## Project layout

```
DesktopAutoAI/
├── DesktopAutoAI.sln
├── README.md
└── src/
    ├── DesktopAutoAI/                  # engine + CLI
    │   ├── Agent/                      # ActionSchema, ActionExecutor, PlanningLoop
    │   ├── Configuration/              # AppSettings POCO
    │   ├── Logging/                    # SerilogSetup
    │   ├── Providers/                  # IActionPlanner, factory, prompts, Anthropic + Gemini
    │   ├── Safety/                     # KillSwitch, DestructiveDetector, ConfirmationPrompt, SafetyGate
    │   ├── Skills/                     # Skill record, SkillsCache, SkillReplayer
    │   ├── Uia/                        # UiaSession, UiaTreeDumper, UiaElementResolver, ScreenCapture
    │   ├── appsettings.json
    │   └── Program.cs                  # CLI entry point
    └── DesktopAutoAI.UI/               # WPF window
        ├── App.xaml(.cs)
        ├── MainWindow.xaml(.cs)
        ├── Logging/UiLogSink.cs        # Serilog sink → UI event
        ├── Services/AgentRunner.cs     # per-prompt orchestration
        ├── Services/ProcessPicker.cs   # target enumeration
        └── appsettings.json
```

## Status

| Milestone | Description | Status |
|---|---|---|
| M1 | Scaffolding, UIA dump, screenshot, Serilog | ✅ |
| M2 | One-shot LLM action (Anthropic) | ✅ |
| M2.5 | Gemini provider | ✅ |
| M3 | Multi-step planning loop with history + retry | ✅ |
| M4 | Skills cache (replay-on-hit, save-on-done) | ✅ |
| M5 | Safety layer (kill switch + destructive prompt) | ✅ |
| M6 | Polish (`ScrollIntoView`, password redaction) | ✅ |
| WPF UI | Windowed prompt host | ✅ |
