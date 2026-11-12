using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;
using FluentAvalonia.UI.Controls;

namespace CAAI;

public class DotnetRunner(TerminalWidget outputPanel)
{
    private Process? _process;
	
    public void Build(string projectPath, string configuration = "Debug")
    {
        RunProcess($"build \"{projectPath}\" -c {configuration}");
    }

    public void Run(string projectPath, string configuration = "Debug")
    {
        RunProcess($"run --project \"{projectPath}\" -c {configuration}");
    }

    private async void RunProcess(string args)
    {
        Stop();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _process = process;
        try
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputPanel.AppendOutput(e.Data + Environment.NewLine); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) outputPanel.AppendOutput(e.Data + Environment.NewLine, OutputType.Error); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            outputPanel.AppendOutput($"Process exited with code {process.ExitCode}.{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            outputPanel.AppendOutput(exception.Message + Environment.NewLine, OutputType.Error);
        }
        finally { Interlocked.CompareExchange(ref _process, null, process); }
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}

public class RunDebugToolbar : UserControl
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
    private readonly Button _run;
    private readonly Button _debug;
    private readonly Button _build;
    private readonly Button _pause;
    private readonly Button _stepOver;
    private readonly Button _stepInto;
    private readonly Button _stepOut;
    private readonly Button _restart;
    private readonly ComboBox _configuration;
    public string Configuration => _configuration.SelectedItem as string ?? "Debug";
    
    public RunDebugToolbar()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 3),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Run Button
        _run = CreateToolbarButton(Symbol.Play, "Run Without Debugging (Ctrl+F5)", Brushes.Green, () => OnRunRequested?.Invoke());

        // Debug Button
        _debug = CreateToolbarButton(Symbol.Play, "Start Debugging / Continue (F5)", Brushes.Teal, () => OnDebugRequested?.Invoke());

        // Stop Button
        var stopBtn = CreateToolbarButton(Symbol.Stop, "Stop (Shift+F5)", Brushes.IndianRed, () => OnStopRequested?.Invoke());
        
        // Build Button
        _build = CreateToolbarButton(Symbol.Repair, "Build Project (Ctrl+Shift+B)", Brushes.SteelBlue, () => OnBuildRequested?.Invoke());
        _pause = CreateToolbarButton(Symbol.Pause, "Pause (F6)", Brushes.Goldenrod, () => OnPauseRequested?.Invoke());
        _stepOver = CreateToolbarButton(Symbol.Next, "Step Over (F10)", Brushes.LightGray, () => OnStepOverRequested?.Invoke());
        _stepInto = CreateToolbarButton(Symbol.Forward, "Step Into (F11)", Brushes.LightGray, () => OnStepIntoRequested?.Invoke());
        _stepOut = CreateToolbarButton(Symbol.Back, "Step Out (Shift+F11)", Brushes.LightGray, () => OnStepOutRequested?.Invoke());
        _restart = CreateToolbarButton(Symbol.Refresh, "Restart Debugging (Ctrl+Shift+F5)", Brushes.MediumSeaGreen, () => OnRestartRequested?.Invoke());
        
        var searchBtn = new Button
        {
            Content = new SymbolIcon { Symbol = Symbol.Find, FontSize = 16 },
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Margin = new Thickness(5, 0),
            Padding = new Thickness(6, 3)
        };
        ToolTip.SetTip(searchBtn, "Search Symbols (Ctrl+Shift+F)");
        searchBtn.Click += (_, _) => OnSearchRequested?.Invoke();
        stack.Children.Add(searchBtn);

        // Configuration Dropdown (e.g., Debug/Release)
        _configuration = new ComboBox
        {
            ItemsSource = new List<string> { "Debug", "Release" },
            SelectedIndex = 0,
            //Height = 30,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(_run);
        stack.Children.Add(_debug);
        stack.Children.Add(_pause);
        stack.Children.Add(stopBtn);
        stack.Children.Add(_stepOver);
        stack.Children.Add(_stepInto);
        stack.Children.Add(_stepOut);
        stack.Children.Add(_restart);
        stack.Children.Add(_build);
        stack.Children.Add(new Separator { Width = 1, Height = 20, Margin = new Thickness(5, 0) });
        stack.Children.Add(_configuration);

        Content = new Border
        {
            //Background = new SolidColorBrush(Color.Parse("#f3f3f3")),
            //BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack
        };
        SetDebugState(DebugSessionState.Idle);
    }

    public void SetDebugState(DebugSessionState state)
    {
        var idle = state is DebugSessionState.Idle or DebugSessionState.Faulted;
        _run.IsEnabled = _build.IsEnabled = _configuration.IsEnabled = idle;
        _debug.IsEnabled = idle || state == DebugSessionState.Paused;
        _pause.IsEnabled = state == DebugSessionState.Running;
        _restart.IsEnabled = state is DebugSessionState.Running or DebugSessionState.Paused;
        _stepOver.IsEnabled = _stepInto.IsEnabled = _stepOut.IsEnabled = state == DebugSessionState.Paused;
        ToolTip.SetTip(_debug, state == DebugSessionState.Paused ? "Continue (F5)" : "Start Debugging (F5)");
    }

    private Button CreateToolbarButton(Symbol icon, string tooltip, IBrush color, Action onClick)
    {
        var btn = new Button
        {
            Content = new SymbolIcon { Symbol = icon, FontSize = 16, Foreground = color },
            Width = 30,
            Height = 30,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            //Padding = new Thickness(6, 3),
            //CornerRadius = new CornerRadius(3)
        };

        ToolTip.SetTip(btn, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
