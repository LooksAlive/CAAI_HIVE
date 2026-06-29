using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;

namespace CAAI;

public class DotnetRunner(OutputPanel outputPanel)
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

    private void RunProcess(string args)
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

        _process = new Process { StartInfo = psi };
        
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputPanel.AppendOutput(e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputPanel.AppendOutput(e.Data, OutputType.Error);
        };
        
        _process.Exited += (_, _) =>
        {
            var proc = _process; // capture reference safely

            if (proc == null)
                return;

            Console.WriteLine(proc.ExitCode == 0 ? "BUILD SUCCESS ✅" : "BUILD FAILED ❌");

            proc.Dispose();

            _process = null; // ✅ NOW it's safe
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process = null; // ✅ NOW it's safe
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited) return;
        _process.Kill(true);
        _process.Dispose();
        _process = null; // ✅ NOW it's safe
    }
}

public class RunDebugToolbar : UserControl
{
    public event Action? OnRunRequested;
    public event Action? OnDebugRequested;
    public event Action? OnStopRequested;
    public event Action? OnBuildRequested;
    public event Action? OnSearchRequested;
    
    public RunDebugToolbar()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Run Button
        var runBtn = CreateToolbarButton("▶", "Run Project (F5)", Brushes.Green, () => OnRunRequested?.Invoke());

        // Debug Button
        var debugBtn = CreateToolbarButton("🪲", "Start Debugging", Brushes.DarkOrange, () => OnDebugRequested?.Invoke());

        // Stop Button
        var stopBtn = CreateToolbarButton("■", "Stop Execution", Brushes.Red, () => OnStopRequested?.Invoke());
        
        // Build Button
        var buildBtn = CreateToolbarButton("🔨", "Build Project (Ctrl+Shift+B)", Brushes.SteelBlue, () => OnBuildRequested?.Invoke());
            
        var searchBtn = new Button
        {
            Content = "🔍 Search (Ctrl+Shift+F)",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Margin = new Thickness(5, 0),
            Padding = new Thickness(10, 5)
        };
        searchBtn.Click += (_, _) => OnSearchRequested?.Invoke();
        stack.Children.Add(searchBtn);

        // Configuration Dropdown (e.g., Debug/Release)
        var configPicker = new ComboBox
        {
            ItemsSource = new List<string> { "Debug", "Release" },
            SelectedIndex = 0,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(runBtn);
        stack.Children.Add(debugBtn);
        stack.Children.Add(stopBtn);
        stack.Children.Add(buildBtn);
        stack.Children.Add(new Separator { Width = 1, Height = 20, Margin = new Thickness(5, 0) });
        stack.Children.Add(configPicker);

        Content = new Border
        {
            //Background = new SolidColorBrush(Color.Parse("#f3f3f3")),
            //BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack
        };
    }

    private Button CreateToolbarButton(string icon, string tooltip, IBrush color, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = icon,
                FontSize = 16,
                Foreground = color,
                FontWeight = FontWeight.Bold
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4)
        };

        ToolTip.SetTip(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
