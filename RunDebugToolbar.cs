using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace CAAI;

public sealed class DotnetRunner : IDisposable
{
    private readonly TerminalWidget _output;
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cts;

    public event Action? Started;
    public event Action<int>? Exited;

    public bool IsRunning
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    public DotnetRunner(TerminalWidget outputPanel) => _output = outputPanel;

    public void Build(string projectPath, string configuration = "Debug")
        => StartDotnet($"build \"{projectPath}\" -c {configuration}", "build");

    public void Run(string projectPath, string configuration = "Debug")
        => StartDotnet($"run --project \"{projectPath}\" -c {configuration} --no-build", "run");

    public void Test(string projectPath, string configuration = "Debug")
        => StartDotnet($"test \"{projectPath}\" -c {configuration}", "test");

    public void Clean(string projectPath, string configuration = "Debug")
        => StartDotnet($"clean \"{projectPath}\" -c {configuration}", "clean");

    public void Restore(string projectPath)
        => StartDotnet($"restore \"{projectPath}\"", "restore");

    private async void StartDotnet(string arguments, string label)
    {
        await StopAsync();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var cts = new CancellationTokenSource();

        lock (_gate) { _process = process; _cts = cts; }

        _output.AppendOutput($"$ dotnet {arguments}{Environment.NewLine}", OutputType.Info);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) _output.AppendOutput(e.Data + Environment.NewLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _output.AppendOutput(e.Data + Environment.NewLine, OutputType.Error);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Started?.Invoke();

            await process.WaitForExitAsync(cts.Token);

            var exit = process.ExitCode;
            var type = exit == 0 ? OutputType.Success : OutputType.Error;
            _output.AppendOutput($"[{label}] exited with code {exit}.{Environment.NewLine}", type);
            Exited?.Invoke(exit);
        }
        catch (OperationCanceledException)
        {
            _output.AppendOutput($"[{label}] cancelled.{Environment.NewLine}", OutputType.Error);
            Exited?.Invoke(-1);
        }
        catch (Exception exception)
        {
            _output.AppendOutput($"[{label}] {exception.Message}{Environment.NewLine}", OutputType.Error);
            Exited?.Invoke(-1);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_process, process)) _process = null; }
            process.Dispose();
            cts.Dispose();
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        CancellationTokenSource? cts;
        lock (_gate) { process = _process; cts = _cts; }

        if (process == null) return;
        try { cts?.Cancel(); } catch { }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        catch { /* best effort */ }
    }

    public void Stop() => _ = StopAsync();

    public void Dispose() => Stop();
}

public sealed class RunDebugToolbar : UserControl
{
    public event Action? OnRunRequested;
    public event Action? OnDebugRequested;
    public event Action? OnStopRequested;
    public event Action? OnBuildRequested;
    public event Action? OnSearchRequested;
    public event Action? OnPauseRequested;
    public event Action? OnStepOverRequested;
    public event Action? OnStepIntoRequested;
    public event Action? OnStepOutRequested;
    public event Action? OnRestartRequested;
    public event Action? OnCleanRequested;
    public event Action? OnTestRequested;
    public event Action? OnConfigurationChanged;

    private readonly Button _run, _debug, _stop, _build, _pause,
                           _stepOver, _stepInto, _stepOut, _restart, _search;
    private readonly ComboBox _configuration;
    private readonly MenuFlyout _moreMenu;

    public string Configuration => _configuration.SelectedItem as string ?? "Debug";

    public RunDebugToolbar()
    {
        // ---- Primary run group ----
        _run = Tool(Symbol.Play, "Run Without Debugging (Ctrl+F5)", () => OnRunRequested?.Invoke());
        _debug = Tool(Symbol.Play, "Start Debugging (F5)", () => OnDebugRequested?.Invoke(), accent: true);
        _pause = Tool(Symbol.Pause, "Pause (F6)", () => OnPauseRequested?.Invoke());
        _stop = Tool(Symbol.Stop, "Stop (Shift+F5)", () => OnStopRequested?.Invoke(), danger: true);
        _restart = Tool(Symbol.Refresh, "Restart Debugging (Ctrl+Shift+F5)", () => OnRestartRequested?.Invoke());

        // ---- Stepping group ----
        _stepOver = Tool(Symbol.Next, "Step Over (F10)", () => OnStepOverRequested?.Invoke());
        _stepInto = Tool(Symbol.Forward, "Step Into (F11)", () => OnStepIntoRequested?.Invoke());
        _stepOut = Tool(Symbol.Back, "Step Out (Shift+F11)", () => OnStepOutRequested?.Invoke());

        // ---- Build group ----
        _build = Tool(Symbol.Repair, "Build Project (Ctrl+Shift+B)", () => OnBuildRequested?.Invoke());
        _search = Tool(Symbol.Find, "Search Symbols (Ctrl+Shift+F)", () => OnSearchRequested?.Invoke());

        // ---- Overflow ----
        _moreMenu = BuildMoreMenu();
        var more = new Button
        {
            Content = new SymbolIcon { Symbol = Symbol.More, FontSize = 16 },
            Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Flyout = _moreMenu,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(more, "More build actions");
        Avalonia.Automation.AutomationProperties.SetName(more, "More build actions");

        // ---- Configuration dropdown ----
        _configuration = new ComboBox
        {
            ItemsSource = new[] { "Debug", "Release" },
            SelectedIndex = 0,
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center
        };
        _configuration.SelectionChanged += (_, _) => OnConfigurationChanged?.Invoke();

        // ---- Compose ----
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 3),
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(_run);
        stack.Children.Add(_debug);
        stack.Children.Add(_pause);
        stack.Children.Add(_stop);
        stack.Children.Add(Sep());
        stack.Children.Add(_stepOver);
        stack.Children.Add(_stepInto);
        stack.Children.Add(_stepOut);
        stack.Children.Add(_restart);
        stack.Children.Add(Sep());
        stack.Children.Add(_build);
        stack.Children.Add(_search);
        stack.Children.Add(more);
        stack.Children.Add(Sep());
        stack.Children.Add(_configuration);

        Content = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ResourceBrush("ControlBorderBrush", Brushes.Gray),
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
            Child = stack
        };

        SetDebugState(DebugSessionState.Idle);
    }

    // Resolve a Fluent theme brush dynamically; falls back if the resource key
    // doesn't exist in the current theme.
    private IBrush ResourceBrush(string key, IBrush fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : fallback;

    private static Control Sep() => new Border
    {
        Width = 1, Height = 20,
        Margin = new Thickness(5, 0),
        Background = Brushes.Gray,
        Opacity = 0.25
    };

    private MenuFlyout BuildMoreMenu()
    {
        var flyout = new MenuFlyout();

        void Add(string header, Symbol icon, Action action)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = icon, FontSize = 14 } };
            item.Click += (_, _) => action();
            flyout.Items.Add(item);
        }
        void AddSep() => flyout.Items.Add(new Separator());

        Add("Build", Symbol.Repair, () => OnBuildRequested?.Invoke());
        Add("Clean", Symbol.Delete, () => OnCleanRequested?.Invoke());
        Add("Test", Symbol.ReportHacked, () => OnTestRequested?.Invoke());
        AddSep();
        Add("Run Without Debugging", Symbol.Play, () => OnRunRequested?.Invoke());
        Add("Start Debugging", Symbol.Play, () => OnDebugRequested?.Invoke());
        Add("Restart Debugging", Symbol.Refresh, () => OnRestartRequested?.Invoke());
        AddSep();
        Add("Stop", Symbol.Stop, () => OnStopRequested?.Invoke());

        return flyout;
    }

    public void SetDebugState(DebugSessionState state)
    {
        var idle = state is DebugSessionState.Idle or DebugSessionState.Faulted;
        var running = state == DebugSessionState.Running;
        var paused = state == DebugSessionState.Paused;

        _run.IsEnabled = idle;
        _debug.IsEnabled = idle || paused;
        _build.IsEnabled = idle;
        _configuration.IsEnabled = idle;

        _pause.IsEnabled = running;
        _stop.IsEnabled = running || paused;
        _restart.IsEnabled = running || paused;

        _stepOver.IsEnabled = _stepInto.IsEnabled = _stepOut.IsEnabled = paused;

        ToolTip.SetTip(_debug, paused ? "Continue (F5)" : "Start Debugging (F5)");
    }

    private static Button Tool(Symbol icon, string tooltip, Action onClick,
                               bool accent = false, bool danger = false)
    {
        var iconControl = new SymbolIcon { Symbol = icon, FontSize = 16 };

        // Fluent palette: the debug/run "accent" uses the theme accent for
        // the glyph only, so the button itself still obeys theme backgrounds.
        if (accent)
        {
            // The Fluent "Accent" is exposed via AccentFillColorDefaultBrush
            // in the resource dictionary; fall back to a sane color otherwise.
            iconControl.Bind(FAIconElement.ForegroundProperty,
                iconControl.GetResourceObservable("AccentFillColorDefaultBrush"));
        }
        else if (danger)
        {
            iconControl.Bind(FAIconElement.ForegroundProperty,
                iconControl.GetResourceObservable("SystemFillColorCriticalBrush"));
        }
        // default: leave Foreground null so the theme foreground applies.

        var btn = new Button
        {
            Content = iconControl,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(btn, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
