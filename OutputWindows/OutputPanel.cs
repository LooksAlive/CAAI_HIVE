using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CAAI;

public enum OutputType
{
    Normal,
    Warning,
    Error,
    Success,
    Info,
    Debug
}

public class OutputLine : INotifyPropertyChanged
{
    private string _text = "";
    private OutputType _type = OutputType.Normal;
    private DateTime _timestamp = DateTime.Now;

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public OutputType Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    public DateTime Timestamp
    {
        get => _timestamp;
        set { _timestamp = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class OutputPanel : UserControl
{
    private readonly ListBox _outputList;
    private readonly ObservableCollection<OutputLine> _outputLines;
    private readonly ScrollViewer _scrollViewer;
    private TextBox _inputBox = null!;
    private StackPanel _toolbarPanel = null!;
    private Process? _currentProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;

    public event EventHandler<string>? CommandEntered;
    public event EventHandler<Process>? ProcessStarted;
    public event EventHandler<int>? ProcessExited;

    public OutputPanel()
    {
        _outputLines = new ObservableCollection<OutputLine>();
        
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, *, Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35))
        };

        // Toolbar
        var toolbar = CreateToolbar();
        mainGrid.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);

        // Output area with list box
        _outputList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
            Foreground = Brushes.White,
            ItemsSource = _outputLines,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(5)
        };

        _outputList.ItemTemplate = new FuncDataTemplate<OutputLine>((line, _) =>
        {
            // Add null check
            if (line == null)
                return new TextBlock { Text = "null", Foreground = Brushes.Red };

            var textBlock = new TextBlock
            {
                Text = $"[{line.Timestamp:HH:mm:ss}] {line.Text}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = GetColor(line.Type),
                TextWrapping = TextWrapping.Wrap
            };
            
            return textBlock;
        });

        _scrollViewer = new ScrollViewer
        {
            Content = _outputList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        mainGrid.Children.Add(_scrollViewer);
        Grid.SetRow(_scrollViewer, 1);

        // Input area (for when running interactive apps)
        var inputPanel = CreateInputPanel();
        mainGrid.Children.Add(inputPanel);
        Grid.SetRow(inputPanel, 2);

        Content = mainGrid;

        // Add initial welcome message
        WriteWelcome();
    }

    private Border CreateToolbar()
    {
        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Padding = new Thickness(10, 5)
        };

        _toolbarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        // Run button
        var runBtn = new Button
        {
            Content = "▶ Run",
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        runBtn.Click += RunButton_Click;

        // Stop button
        var stopBtn = new Button
        {
            Content = "⏹ Stop",
            Background = new SolidColorBrush(Color.FromRgb(120, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = false
        };
        stopBtn.Click += StopButton_Click;

        // Clear button
        var clearBtn = new Button
        {
            Content = "🧹 Clear",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clearBtn.Click += (_, _) => ClearOutput();

        // Auto-scroll toggle
        var autoScrollToggle = new ToggleSwitch
        {
            OffContent = "Auto Scroll: Off",
            OnContent = "Auto Scroll: On",
            IsChecked = true,
            Margin = new Thickness(20, 0, 0, 0)
        };
        autoScrollToggle.IsCheckedChanged += (_, _) => { /* Handle auto-scroll */ };

        // Filter combo
        var filterCombo = new ComboBox
        {
            Width = 120,
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White,
            ItemsSource = new[] { "All", "Info", "Success", "Warning", "Error", "Debug" }
        };
        filterCombo.SelectedIndex = 0;
        filterCombo.SelectionChanged += FilterCombo_SelectionChanged;

        _toolbarPanel.Children.Add(runBtn);
        _toolbarPanel.Children.Add(stopBtn);
        _toolbarPanel.Children.Add(clearBtn);
        _toolbarPanel.Children.Add(autoScrollToggle);
        _toolbarPanel.Children.Add(new TextBlock 
        { 
            Text = "Filter:", 
            Foreground = Brushes.Gray, 
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        _toolbarPanel.Children.Add(filterCombo);

        // Store references for later
        _toolbarPanel.Tag = new { RunBtn = runBtn, StopBtn = stopBtn, AutoScrollToggle = autoScrollToggle };

        toolbar.Child = _toolbarPanel;
        return toolbar;
    }

    private Border CreateInputPanel()
    {
        var inputBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            Padding = new Thickness(10, 5),
            Margin = new Thickness(5, 0, 5, 5),
            CornerRadius = new CornerRadius(4),
            IsVisible = false
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto, *")
        };

        var prompt = new TextBlock
        {
            Text = "Input:",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        _inputBox = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            Watermark = "Type input and press Enter..."
        };
        _inputBox.KeyDown += InputBox_KeyDown;

        grid.Children.Add(prompt);
        Grid.SetColumn(prompt, 0);
        grid.Children.Add(_inputBox);
        Grid.SetColumn(_inputBox, 1);

        inputBorder.Child = grid;
        return inputBorder;
    }

    private IBrush GetColor(OutputType type)
    {
        return type switch
        {
            OutputType.Error => new SolidColorBrush(Color.FromRgb(255, 100, 100)),
            OutputType.Warning => new SolidColorBrush(Color.FromRgb(255, 200, 100)),
            OutputType.Success => new SolidColorBrush(Color.FromRgb(100, 255, 100)),
            OutputType.Info => new SolidColorBrush(Color.FromRgb(100, 200, 255)),
            OutputType.Debug => new SolidColorBrush(Color.FromRgb(200, 150, 255)),
            _ => new SolidColorBrush(Color.FromRgb(200, 200, 200))
        };
    }

    private void WriteWelcome()
    {
        AppendOutput("═══════════════════════════════════════════════════════════════", OutputType.Info);
        AppendOutput("  CAAI Application Output Panel", OutputType.Info);
        AppendOutput("═══════════════════════════════════════════════════════════════", OutputType.Info);
        AppendOutput("Ready to run applications.", OutputType.Success);
        AppendOutput("");
    }

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        // Show a dialog to select the application to run
        var dialog = new Window
        {
            Title = "Run Application",
            Width = 500,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        
        panel.Children.Add(new TextBlock 
        { 
            Text = "Select executable or enter command:", 
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        });

        var filePicker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        
        var filePathBox = new TextBox
        {
            Width = 300,
            Watermark = "Path to executable or command...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };
        
        var browseBtn = new Button
        {
            Content = "Browse...",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5)
        };
        browseBtn.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Executable",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Executable Files") { Patterns = new[] { "*.exe" } },
                    new("All Files") { Patterns = new[] { "*" } }
                }
            });
            if (files.Count > 0)
                filePathBox.Text = files[0].Path.LocalPath;
        };

        filePicker.Children.Add(filePathBox);
        filePicker.Children.Add(browseBtn);
        panel.Children.Add(filePicker);

        // Arguments
        var argsPanel = new StackPanel { Spacing = 5 };
        argsPanel.Children.Add(new TextBlock { Text = "Arguments:", Foreground = Brushes.Gray, FontSize = 11 });
        var argsBox = new TextBox
        {
            Watermark = "Command line arguments...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };
        argsPanel.Children.Add(argsBox);
        panel.Children.Add(argsPanel);

        // Working directory
        var workingDirPanel = new StackPanel { Spacing = 5 };
        workingDirPanel.Children.Add(new TextBlock { Text = "Working Directory:", Foreground = Brushes.Gray, FontSize = 11 });
        var workingDirBox = new TextBox
        {
            Watermark = "Working directory...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            Text = Environment.CurrentDirectory
        };
        workingDirPanel.Children.Add(workingDirBox);
        panel.Children.Add(workingDirPanel);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var runBtn = new Button
        {
            Content = "Run",
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 8)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 8)
        };

        var result = false;
        string? filePath = null;
        string? args = null;
        string? workingDir = null;

        runBtn.Click += (_, _) =>
        {
            filePath = filePathBox.Text;
            args = argsBox.Text;
            workingDir = workingDirBox.Text;
            result = true;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        buttonPanel.Children.Add(runBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        await dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? new Window());

        if (result && !string.IsNullOrEmpty(filePath))
        {
            await RunApplication(filePath, args ?? "", workingDir ?? Environment.CurrentDirectory);
        }
    }

    private async Task RunApplication(string filePath, string arguments, string workingDirectory)
    {
        if (_isRunning) return;

        ClearOutput();
        AppendOutput($"═══════════════════════════════════════════════════════════════", OutputType.Info);
        AppendOutput($"  Running: {filePath}", OutputType.Info);
        AppendOutput($"  Arguments: {arguments}", OutputType.Info);
        AppendOutput($"  Working Directory: {workingDirectory}", OutputType.Info);
        AppendOutput($"═══════════════════════════════════════════════════════════════", OutputType.Info);
        AppendOutput("");

        _isRunning = true;
        UpdateToolbarState();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _currentProcess = new Process { StartInfo = processInfo };

            _currentProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendOutput(e.Data, OutputType.Normal);
                }
            };

            _currentProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AppendOutput(e.Data, OutputType.Error);
                }
            };

            _currentProcess.Exited += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isRunning = false;
                    UpdateToolbarState();
                    AppendOutput("");
                    AppendOutput($"═══════════════════════════════════════════════════════════════", OutputType.Info);
                    AppendOutput($"  Process exited with code: {_currentProcess?.ExitCode}", 
                        _currentProcess?.ExitCode == 0 ? OutputType.Success : OutputType.Error);
                    ProcessExited?.Invoke(this, _currentProcess?.ExitCode ?? -1);
                    _currentProcess?.Dispose();
                    _currentProcess = null;
                });
            };

            // Enable input
            ShowInputPanel(true);

            _currentProcess.Start();
            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();

            ProcessStarted?.Invoke(this, _currentProcess);

            // Wait for process to exit (non-blocking)
            await Task.Run(() => _currentProcess.WaitForExit());
        }
        catch (Exception ex)
        {
            AppendOutput($"Error starting process: {ex.Message}", OutputType.Error);
            _isRunning = false;
            UpdateToolbarState();
            ShowInputPanel(false);
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try
            {
                _currentProcess.Kill();
                AppendOutput("Process stopped by user.", OutputType.Warning);
            }
            catch (Exception ex)
            {
                AppendOutput($"Error stopping process: {ex.Message}", OutputType.Error);
            }
        }
        else
        {
            _cancellationTokenSource?.Cancel();
        }

        _isRunning = false;
        UpdateToolbarState();
        ShowInputPanel(false);
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _currentProcess != null && !_currentProcess.HasExited)
        {
            var input = _inputBox.Text;
            if (!string.IsNullOrEmpty(input))
            {
                CommandEntered?.Invoke(this, input);
                try
                {
                    _currentProcess.StandardInput.WriteLine(input);
                    _currentProcess.StandardInput.Flush();
                    AppendOutput($"> {input}", OutputType.Info);
                    _inputBox.Text = "";
                }
                catch (Exception ex)
                {
                    AppendOutput($"Error sending input: {ex.Message}", OutputType.Error);
                }
            }
            e.Handled = true;
        }
    }

    private void ShowInputPanel(bool show)
    {
        var inputPanel = this.FindControl<Border>("InputPanel");
        if (inputPanel != null)
        {
            inputPanel.IsVisible = show;
            if (show)
                _inputBox?.Focus();
        }
    }

    private void UpdateToolbarState()
    {
        /*if (_toolbarPanel.Tag is dynamic tag)
        {
            tag.RunBtn.IsEnabled = !_isRunning;
            tag.StopBtn.IsEnabled = _isRunning;
        }*/
    }

    private void FilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var filter = ((ComboBox)sender!).SelectedItem?.ToString() ?? "All";
        ApplyFilter(filter);
    }

    private void ApplyFilter(string filter)
    {
        if (filter == "All")
        {
            _outputList.ItemsSource = _outputLines;
        }
        else
        {
            var type = Enum.Parse<OutputType>(filter);
            _outputList.ItemsSource = _outputLines.Where(l => l.Type == type);
        }
    }

    public void AppendOutput(string text, OutputType type = OutputType.Normal)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrEmpty(text))
            {
                _outputLines.Add(new OutputLine { Text = "", Type = type });
            }
            else
            {
                var lines = text.Split('\n');
                foreach (var line in lines)
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        _outputLines.Add(new OutputLine 
                        { 
                            Text = line.TrimEnd('\r'), 
                            Type = type 
                        });
                    }
                }
            }

            _scrollViewer.ScrollToEnd();

            // Update filter if active
            /*var filterCombo = this.FindControl<ComboBox>("FilterCombo");
            if (filterCombo != null && filterCombo.SelectedItem?.ToString() != "All")
            {
                ApplyFilter(filterCombo.SelectedItem?.ToString() ?? "All");
            }*/
        });
    }

    public void ClearOutput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _outputLines.Clear();
        });
    }

    public void RunCommand(string command, string arguments = "", string workingDirectory = "")
    {
        var dir = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        _ = RunApplication(command, arguments, dir);
    }

    public void Stop()
    {
        StopButton_Click(null, new RoutedEventArgs());
    }

    public bool IsRunning => _isRunning;
    public Process? CurrentProcess => _currentProcess;
}