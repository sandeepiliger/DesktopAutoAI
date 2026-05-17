using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopAutoAI.Providers;
using DesktopAutoAI.UI.Logging;
using DesktopAutoAI.UI.Services;
using Serilog;
using Serilog.Events;

namespace DesktopAutoAI.UI;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public MainWindow()
    {
        InitializeComponent();

        App.LogSink.Emitted += OnLogEmitted;
        Closed += (_, _) => App.LogSink.Emitted -= OnLogEmitted;

        PlannerItem.Content = DescribePlanner();
        RefreshTargets();
    }

    // ---- log streaming ------------------------------------------------------

    private void OnLogEmitted(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var item = new ListBoxItem
            {
                Content = entry.Text,
                Foreground = ColorForLevel(entry.Level),
            };
            LogList.Items.Add(item);
            LogList.ScrollIntoView(item);
        });
    }

    private static Brush ColorForLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Error or LogEventLevel.Fatal => Brushes.Tomato,
        LogEventLevel.Warning => Brushes.Goldenrod,
        LogEventLevel.Debug or LogEventLevel.Verbose => Brushes.Gray,
        _ => Brushes.LightGray,
    };

    private void OnClearLog(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    // ---- target picker ------------------------------------------------------

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshTargets();

    private void RefreshTargets()
    {
        var previous = (TargetCombo.SelectedItem as ProcessListItem)?.Pid;
        var items = ProcessPicker.EnumerateWindowedProcesses();
        TargetCombo.ItemsSource = items;
        if (items.Count == 0)
        {
            SetStatus("No windowed processes found - start the target app first.", warning: true);
            return;
        }
        var restored = previous is { } p ? items.FirstOrDefault(i => i.Pid == p) : null;
        TargetCombo.SelectedItem = restored ?? items[0];
        SetStatus($"Found {items.Count} target(s).");
    }

    private async void OnPickForeground(object sender, RoutedEventArgs e)
    {
        SetStatus("Minimising in 1s - tab to the target window... (3s window)");
        var prev = WindowState;
        await Task.Delay(TimeSpan.FromSeconds(1));
        WindowState = WindowState.Minimized;

        var pick = await Task.Run(() => ProcessPicker.WaitForExternalForeground(TimeSpan.FromSeconds(3)));

        WindowState = prev;
        Activate();

        if (pick is null)
        {
            SetStatus("No external window came to the foreground.", warning: true);
            return;
        }

        var items = ProcessPicker.EnumerateWindowedProcesses().ToList();
        var match = items.FirstOrDefault(i => i.Pid == pick.Pid);
        if (match is null)
        {
            items.Insert(0, pick);
            match = pick;
        }
        TargetCombo.ItemsSource = items;
        TargetCombo.SelectedItem = match;
        SetStatus($"Picked {pick.Display}");
    }

    // ---- run / stop ---------------------------------------------------------

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            OnRun(sender, new RoutedEventArgs());
        }
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_runTask is { IsCompleted: false })
        {
            SetStatus("A run is already in progress.", warning: true);
            return;
        }

        if (TargetCombo.SelectedItem is not ProcessListItem target)
        {
            SetStatus("Select a target first.", warning: true);
            return;
        }
        var goal = PromptBox.Text.Trim();
        if (string.IsNullOrEmpty(goal))
        {
            SetStatus("Type a prompt first.", warning: true);
            return;
        }

        var opts = new RunOptions(
            ProcessName: target.ProcessName,
            Goal: goal,
            UseCache: UseCacheCb.IsChecked == true,
            UseSafety: UseSafetyCb.IsChecked == true,
            AutoYes: AutoYesCb.IsChecked == true);

        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        SetStatus($"Running against {target.ProcessName} (pid {target.Pid})...");

        _runCts = new CancellationTokenSource();
        var runner = new AgentRunner(App.Settings);
        _runTask = Task.Run(() => runner.RunAsync(opts, _runCts.Token))
            .ContinueWith(t => Dispatcher.BeginInvoke(() => FinishRun(t)),
                          TaskScheduler.Default);
    }

    private void FinishRun(Task<RunSummary> t)
    {
        RunBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        _runCts?.Dispose();
        _runCts = null;

        if (t.IsFaulted)
        {
            var ex = t.Exception?.GetBaseException();
            Log.Error(ex, "Run failed with an exception.");
            SetStatus($"Run errored: {ex?.Message}", warning: true);
            return;
        }
        if (t.IsCanceled)
        {
            SetStatus("Run cancelled.", warning: true);
            return;
        }
        var s = t.Result;
        SetStatus($"Done: outcome={s.Outcome} steps={s.Steps} - {s.Message}");
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_runCts is { IsCancellationRequested: false })
        {
            Log.Warning("Stop button pressed.");
            _runCts.Cancel();
            SetStatus("Cancelling...");
        }
    }

    // ---- helpers ------------------------------------------------------------

    private void SetStatus(string text, bool warning = false)
    {
        StatusItem.Content = text;
        StatusItem.Foreground = warning ? Brushes.OrangeRed : SystemColors.WindowTextBrush;
    }

    private static string DescribePlanner()
    {
        try
        {
            var planner = PlannerFactory.Create(App.Settings.Planner);
            return $"Planner: {planner.ProviderName} / {planner.ModelId}";
        }
        catch (Exception ex)
        {
            return $"Planner config error: {ex.Message}";
        }
    }
}
