using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CAAI;

public class TerminalWidget : UserControl
{
    private SelectableTextBlock _outputText = null!;
    private ScrollViewer _scrollViewer = null!;
    private TextBox _inputBox = null!;
    private StackPanel _panel = null!;
    private readonly List<string> _commandHistory = new();
    private int _historyIndex = -1;
    private Process? _currentProcess;
    private string _currentDirectory;

    public event EventHandler<string>? CommandEntered;
    public event EventHandler<string>? OutputReceived;

    public TerminalWidget(string workingDirectory = "")
    {
        _currentDirectory = string.IsNullOrEmpty(workingDirectory) ? 
            Environment.CurrentDirectory : workingDirectory;

        BuildUI();
    }

    private void BuildUI()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*, Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35))
        };

        // Output area
        _panel = new StackPanel
        {
            Margin = new Thickness(5)
        };

        _outputText = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200))
        };
        _panel.Children.Add(_outputText);

        _scrollViewer = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(5)
        };

        // Input area
        var inputPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Padding = new Thickness(10),
            Margin = new Thickness(5, 0, 5, 5),
            CornerRadius = new CornerRadius(4)
        };

        var inputGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto, *")
        };

        var prompt = new TextBlock
        {
            Text = "> ",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        _inputBox = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Watermark = "Type command or press Ctrl+Shift+C to copy"
        };
        _inputBox.KeyDown += InputBox_KeyDown;
        _inputBox.PointerPressed += InputBox_PointerPressed;

        inputGrid.Children.Add(prompt);
        Grid.SetColumn(prompt, 0);
        inputGrid.Children.Add(_inputBox);
        Grid.SetColumn(_inputBox, 1);

        inputPanel.Child = inputGrid;

        grid.Children.Add(_scrollViewer);
        Grid.SetRow(_scrollViewer, 0);
        grid.Children.Add(inputPanel);
        Grid.SetRow(inputPanel, 1);

        Content = grid;

        // Initial welcome message
        WriteWelcomeMessage();
    }

    private void WriteWelcomeMessage()
    {
        var welcome = new StringBuilder();
        welcome.AppendLine("╔══════════════════════════════════════════════════════════╗");
        welcome.AppendLine("║                    CAAI TERMINAL                       ║");
        welcome.AppendLine("╚══════════════════════════════════════════════════════════╝");
        welcome.AppendLine($"Working directory: {_currentDirectory}");
        welcome.AppendLine("Type commands and press Enter. Use ↑↓ for history.");
        welcome.AppendLine("Type 'help' for available commands, 'clear' to clear screen.");
        welcome.AppendLine("");

        AppendOutput(welcome.ToString(), OutputType.Info);
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteCommand(_inputBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            NavigateHistory(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            NavigateHistory(1);
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && 
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Ctrl+Shift+C to copy selected text
            var clipboard = this.GetVisualRoot();//?.GetClipboard();
            /*if (clipboard != null && !string.IsNullOrEmpty(_outputText.SelectedText))
            {
                Dispatcher.UIThread.InvokeAsync(async () => 
                    await clipboard.SetTextAsync(_outputText.SelectedText));
            }*/
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && 
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Ctrl+Shift+V to paste
            var clipboard = this.GetVisualRoot();//?.GetClipboard();
            /*if (clipboard != null)
            {
                /*Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var text = await clipboard.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                        _inputBox.Text += text;
                });
            }*/
            e.Handled = true;
        }
    }

    private void InputBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _inputBox.Focus();
    }

    private void NavigateHistory(int direction)
    {
        if (_commandHistory.Count == 0) return;

        _historyIndex += direction;
        
        if (_historyIndex < 0)
        {
            _historyIndex = -1;
            _inputBox.Text = "";
            return;
        }
        
        if (_historyIndex >= _commandHistory.Count)
        {
            _historyIndex = _commandHistory.Count - 1;
        }

        if (_historyIndex >= 0 && _historyIndex < _commandHistory.Count)
        {
            _inputBox.Text = _commandHistory[_historyIndex];
            _inputBox.CaretIndex = _inputBox.Text.Length;
        }
    }

    private async void ExecuteCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        // Add to history
        _commandHistory.Add(command);
        _historyIndex = _commandHistory.Count;

        // Echo command
        AppendOutput($"> {command}{Environment.NewLine}", OutputType.Info);

        // Parse command
        var parts = command.Trim().Split(' ');
        var cmd = parts[0].ToLower();
        var args = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";

        // Handle built-in commands
        switch (cmd)
        {
            case "clear":
            case "cls":
                ClearTerminal();
                return;

            case "help":
            case "?":
                ShowHelp();
                return;

            case "cd":
                ChangeDirectory(args);
                return;

            case "pwd":
                AppendOutput($"{_currentDirectory}{Environment.NewLine}", OutputType.Normal);
                return;

            case "ls":
            case "dir":
                ListDirectory(args);
                return;

            case "exit":
                ExitTerminal();
                return;

            case "echo":
                AppendOutput($"{args}{Environment.NewLine}", OutputType.Normal);
                return;

            case "date":
                AppendOutput($"{DateTime.Now}{Environment.NewLine}", OutputType.Normal);
                return;

            case "whoami":
                AppendOutput($"{Environment.UserName}{Environment.NewLine}", OutputType.Normal);
                return;

            default:
                // Try to run as external command
                await RunExternalCommand(command);
                break;
        }

        CommandEntered?.Invoke(this, command);
    }

    private void ShowHelp()
    {
        var help = new StringBuilder();
        help.AppendLine("Available commands:");
        help.AppendLine("  clear / cls    - Clear the terminal");
        help.AppendLine("  help / ?      - Show this help");
        help.AppendLine("  cd <path>     - Change directory");
        help.AppendLine("  pwd           - Print working directory");
        help.AppendLine("  ls / dir      - List directory contents");
        help.AppendLine("  echo <text>   - Echo text");
        help.AppendLine("  date          - Show current date and time");
        help.AppendLine("  whoami        - Show current user");
        help.AppendLine("  exit          - Close terminal");
        help.AppendLine("");
        help.AppendLine("External commands:");
        help.AppendLine("  Any command that works in your system shell");
        help.AppendLine("  (e.g., git, dotnet, npm, etc.)");
        help.AppendLine("");
        help.AppendLine("Shortcuts:");
        help.AppendLine("  ↑ / ↓         - Command history");
        help.AppendLine("  Ctrl+Shift+C  - Copy selected text");
        help.AppendLine("  Ctrl+Shift+V  - Paste text");

        AppendOutput(help.ToString(), OutputType.Info);
    }

    private void ChangeDirectory(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                var newPath = Path.Combine(_currentDirectory, path);
                newPath = Path.GetFullPath(newPath);
                if (Directory.Exists(newPath))
                {
                    _currentDirectory = newPath;
                }
                else
                {
                    AppendOutput($"Directory not found: {path}{Environment.NewLine}", OutputType.Error);
                    return;
                }
            }

            AppendOutput($"{_currentDirectory}{Environment.NewLine}", OutputType.Success);
        }
        catch (Exception ex)
        {
            AppendOutput($"Error: {ex.Message}{Environment.NewLine}", OutputType.Error);
        }
    }

    private void ListDirectory(string args)
    {
        try
        {
            var dirInfo = new DirectoryInfo(_currentDirectory);
            var items = dirInfo.GetFileSystemInfos();
            
            // Parse args for options
            var showHidden = args.Contains("-a") || args.Contains("--all");
            var longFormat = args.Contains("-l") || args.Contains("--long");
            var filter = args.Split(' ').LastOrDefault(a => !a.StartsWith("-"));

            var filteredItems = items.AsEnumerable();
            
            if (!showHidden)
                filteredItems = filteredItems.Where(i => !i.Name.StartsWith("."));
            
            if (!string.IsNullOrEmpty(filter) && !filter.StartsWith("-"))
                filteredItems = filteredItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

            var list = filteredItems.OrderBy(i => i is DirectoryInfo ? 0 : 1).ThenBy(i => i.Name);
            
            var sb = new StringBuilder();
            
            if (longFormat)
            {
                var total = list.Count();
                sb.AppendLine($"Total: {total} items");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine($"{"Type",-5} {"Size",-12} {"Modified",-20} Name");
                sb.AppendLine(new string('-', 60));
                
                foreach (var item in list)
                {
                    var isDir = item is DirectoryInfo;
                    var size = isDir ? "<DIR>" : ((FileInfo)item).Length.ToString("N0");
                    var modified = item.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    var type = isDir ? "📁" : "📄";
                    
                    sb.AppendLine($"{type,-5} {size,-12} {modified,-20} {item.Name}");
                }
            }
            else
            {
                // Color-coded output
                var dirs = list.Where(i => i is DirectoryInfo).ToList();
                var files = list.Where(i => i is FileInfo).ToList();
                
                if (dirs.Any())
                {
                    sb.Append("📁 ");
                    sb.AppendLine(string.Join("  ", dirs.Select(d => d.Name)));
                }
                
                if (files.Any())
                {
                    sb.Append("📄 ");
                    sb.AppendLine(string.Join("  ", files.Select(f => f.Name)));
                }
                
                if (!dirs.Any() && !files.Any())
                {
                    sb.AppendLine("(empty)");
                }
            }
            
            AppendOutput(sb.ToString(), OutputType.Normal);
        }
        catch (Exception ex)
        {
            AppendOutput($"Error: {ex.Message}{Environment.NewLine}", OutputType.Error);
        }
    }

    private void ExitTerminal()
    {
        AppendOutput("Terminal session ended.", OutputType.Info);
        _inputBox.IsEnabled = false;
    }

    private async Task RunExternalCommand(string command)
    {
        try
        {
            _currentProcess = new Process();
            _currentProcess.StartInfo.FileName = GetShellExecutable();
            _currentProcess.StartInfo.Arguments = GetShellArguments(command);
            _currentProcess.StartInfo.WorkingDirectory = _currentDirectory;
            _currentProcess.StartInfo.UseShellExecute = false;
            _currentProcess.StartInfo.RedirectStandardOutput = true;
            _currentProcess.StartInfo.RedirectStandardError = true;
            _currentProcess.StartInfo.CreateNoWindow = true;

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            _currentProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    AppendOutput(e.Data + Environment.NewLine, OutputType.Normal);
                }
            };

            _currentProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    AppendOutput(e.Data + Environment.NewLine, OutputType.Error);
                }
            };

            _currentProcess.Start();
            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();
            await _currentProcess.WaitForExitAsync();
            
            if (_currentProcess.ExitCode != 0)
            {
                AppendOutput($"Process exited with code: {_currentProcess.ExitCode}{Environment.NewLine}", OutputType.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"Error: {ex.Message}{Environment.NewLine}", OutputType.Error);
        }
        finally
        {
            _currentProcess?.Dispose();
            _currentProcess = null;
        }
    }

    private string GetShellExecutable()
    {
        if (OperatingSystem.IsWindows())
            return "cmd.exe";
        else if (OperatingSystem.IsLinux())
            return "/bin/bash";
        else if (OperatingSystem.IsMacOS())
            return "/bin/zsh";
        return "sh";
    }

    private string GetShellArguments(string command)
    {
        if (OperatingSystem.IsWindows())
            return $"/c {command}";
        else
            return $"-c \"{command}\"";
    }

    public void AppendOutput(string text, OutputType type = OutputType.Normal)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var color = type switch
            {
                OutputType.Error => new SolidColorBrush(Color.FromRgb(255, 100, 100)),
                OutputType.Warning => new SolidColorBrush(Color.FromRgb(255, 200, 100)),
                OutputType.Success => new SolidColorBrush(Color.FromRgb(100, 255, 100)),
                OutputType.Info => new SolidColorBrush(Color.FromRgb(100, 200, 255)),
                _ => new SolidColorBrush(Color.FromRgb(200, 200, 200))
            };

            var run = new Run
            {
                Text = text,
                Foreground = color
            };

            _outputText.Inlines?.Add(run);
            _scrollViewer.ScrollToEnd();
            
            OutputReceived?.Invoke(this, text);
        });
    }

    public void ClearTerminal()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _outputText.Inlines?.Clear();
            _commandHistory.Clear();
            _historyIndex = -1;
        });
    }

    public void SetWorkingDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            _currentDirectory = path;
            AppendOutput($"Changed directory to: {path}{Environment.NewLine}", OutputType.Info);
        }
    }

    public void ExecuteCommandDirect(string command)
    {
        if (!string.IsNullOrEmpty(command))
        {
            _inputBox.Text = command;
            ExecuteCommand(command);
        }
    }

    public void FocusInput()
    {
        _inputBox.Focus();
    }

    public string GetWorkingDirectory() => _currentDirectory;
}