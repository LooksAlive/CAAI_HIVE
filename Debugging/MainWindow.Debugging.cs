using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CAAI;

public partial class MainWindow
{
    private readonly BreakpointStore _debugBreakpoints = new();
    private DebugSession _debugSession = null!;
    private DebugPanel _debugPanel = null!;
    private TabItem _debugTab = null!;
    private string? _debugProjectPath;
    private bool _debugCommandPending;
    private bool _debugClosePending;
    private bool _debugCloseComplete;

    private void InitializeDebugging()
    {
        _debugSession = new DebugSession(_debugBreakpoints);
        _debugPanel = new DebugPanel(_debugSession, _debugBreakpoints);
        _debugPanel.SelectProjectRequested += () => RunDebugAction(async () => { await SelectDebugProjectAsync(forcePicker: true); });
        _debugPanel.LaunchSettingsRequested += () => RunDebugAction(OpenDebugSettingsAsync);
        _debugPanel.NavigateRequested += (path, line) => OpenFileAtLine(path, line - 1);
        _debugSession.Output += (text, error) => _terminal.AppendOutput(text, error ? OutputType.Error : OutputType.Info);
        _debugSession.StateChanged += (state, _) =>
        {
            _toolbar.SetDebugState(state);
            _menu.SetDebugState(state);
            if (state is DebugSessionState.Building or DebugSessionState.Paused or DebugSessionState.Faulted) ShowDebugOutput();
        };
        _debugSession.FrameChanged += frame =>
        {
            try
            {
                foreach (var editor in OpenCodeEditors()) editor.SetExecutionLine(null);
                if (frame?.FilePath is not { Length: > 0 } path || frame.Line < 1 || !File.Exists(path)) return;
                OpenFileAtLine(path, frame.Line - 1, Math.Max(0, frame.Column - 1));
                OpenCodeEditors().FirstOrDefault(editor => BreakpointStore.SameFile(editor.FilePath, path))?.SetExecutionLine(frame.Line);
            }
            catch (Exception exception)
            {
                _terminal.AppendOutput($"Could not display the debug frame: {exception.Message}{Environment.NewLine}", OutputType.Error);
            }
        };
        _debugBreakpoints.PersistenceFailed += message => _terminal.AppendOutput(message + Environment.NewLine, OutputType.Error);
        if (_debugBreakpoints.LoadError is { } error) _terminal.AppendOutput(error + Environment.NewLine, OutputType.Error);
        var candidates = Directory.GetFiles(_baseDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 1)
        {
            _debugProjectPath = candidates[0];
            _debugPanel.SetProject(_debugProjectPath);
        }
        AddHandler(KeyDownEvent, OnDebugKeyDown, RoutingStrategies.Tunnel);
        Closing += (_, args) =>
        {
            if (_debugCloseComplete) return;
            args.Cancel = true;
            if (_debugClosePending) return;
            _debugClosePending = true;
            Dispatcher.UIThread.Post(async () =>
            {
                try { await _debugSession.DisposeAsync(); }
                catch (Exception exception) { _terminal.AppendOutput(exception.Message + Environment.NewLine, OutputType.Error); }
                finally
                {
                    _runner.Stop();
                    _debugCloseComplete = true;
                    Close();
                }
            });
        };
    }

    private IEnumerable<CodeEditor> OpenCodeEditors() => _tabs.Items.OfType<TabItem>()
        .Select(tab => FindEditorInContent(tab.Content)).OfType<CodeEditor>();

    private void ToggleActiveBreakpoint()
    {
        if (_tabs.SelectedItem is TabItem tab) FindEditorInContent(tab.Content)?.ToggleBreakpoint();
    }

    private void ShowDebugOutput()
    {
        if (_outputTabs == null || _debugTab == null) return;
        SetOutputVisible(true);
        _outputTabs.SelectedItem = _debugTab;
        if (_mainGrid.RowDefinitions[2].ActualHeight < 300) _mainGrid.RowDefinitions[2].Height = new GridLength(320);
    }

    private async Task<string?> SelectDebugProjectAsync(bool forcePicker = false)
    {
        if (_debugSession.IsActive && forcePicker) throw new InvalidOperationException("Stop the current session before changing projects.");
        if (!forcePicker && _debugProjectPath != null && File.Exists(_debugProjectPath)) return _debugProjectPath;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Project to Debug", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("C# Project") { Patterns = ["*.csproj"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return null;
        _debugProjectPath = Path.GetFullPath(path);
        _debugPanel.SetProject(_debugProjectPath);
        return _debugProjectPath;
    }

    private async Task StartOrContinueDebuggingAsync()
    {
        if (_debugSession.State == DebugSessionState.Paused)
        {
            await ExecuteDebugCommandAsync("continue");
            return;
        }
        if (_debugSession.IsActive) return;
        if (await SelectDebugProjectAsync() is not { } project) return;
        if (!await SaveBeforeDebugAsync(project)) return;
        _runner.Stop();
        ShowDebugOutput();
        await _debugSession.StartAsync(DebugLaunchConfiguration.Load(project));
    }

    private async Task ExecuteDebugCommandAsync(string command)
    {
        if (_debugCommandPending || _debugSession.State != DebugSessionState.Paused) return;
        _debugCommandPending = true;
        try { await _debugSession.ContinueAsync(command); }
        finally { _debugCommandPending = false; }
    }

    private async Task OpenDebugSettingsAsync()
    {
        if (await SelectDebugProjectAsync() is not { } project) return;
        DebugLaunchConfiguration.CreateSettings(project);
        OpenFile(DebugLaunchConfiguration.SettingsPath(project));
    }

    private async Task RestartDebuggingAsync()
    {
        if (!_debugSession.IsActive) return;
        await _debugSession.StopAsync();
        await StartOrContinueDebuggingAsync();
    }

    private async Task StopExecutionAsync()
    {
        _runner.Stop();
        if (_debugSession.IsActive) await _debugSession.StopAsync();
    }

    private async Task RunWithoutDebuggingAsync()
    {
        if (_debugSession.IsActive) throw new InvalidOperationException("Stop the debug session before running another process.");
        if (await SelectDebugProjectAsync() is not { } project || !await SaveBeforeDebugAsync(project)) return;
        SetOutputVisible(true);
        _outputTabs.SelectedItem = _outputTabs.Items.OfType<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, _terminal));
        _runner.Run(project, _toolbar.Configuration);
    }

    private async Task BuildDebugProjectAsync()
    {
        if (_debugSession.IsActive) throw new InvalidOperationException("Stop the debug session before building.");
        if (await SelectDebugProjectAsync() is not { } project || !await SaveBeforeDebugAsync(project)) return;
        SetOutputVisible(true);
        _outputTabs.SelectedItem = _outputTabs.Items.OfType<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Content, _terminal));
        _runner.Build(project, _toolbar.Configuration);
    }

    private async Task<bool> SaveBeforeDebugAsync(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        var changed = OpenCodeEditors().Where(editor =>
        {
            var relative = Path.GetRelativePath(directory, editor.FilePath);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                File.Exists(editor.FilePath) && File.ReadAllText(editor.FilePath) != editor.GetText();
        }).ToArray();
        if (changed.Length == 0) return true;
        var save = new Button { Content = "Save and Build", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"Save {changed.Length} modified file(s) before building?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(buttons);
        var dialog = new Window { Title = "Save Before Build", Width = 420, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (!await dialog.ShowDialog<bool>(this)) return false;
        foreach (var editor in changed) await File.WriteAllTextAsync(editor.FilePath, editor.GetText());
        return true;
    }

    private async void RunDebugAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SetOutputVisible(true);
            _terminal.AppendOutput(exception.Message + Environment.NewLine, OutputType.Error);
            _notificationManager.Show(new Notification("Debugger", exception.Message, NotificationType.Error));
        }
    }

    private void OnDebugKeyDown(object? sender, KeyEventArgs e)
    {
        Func<Task>? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.F5, KeyModifiers.None) => StartOrContinueDebuggingAsync,
            (Key.F5, KeyModifiers.Control) => RunWithoutDebuggingAsync,
            (Key.F5, KeyModifiers.Shift) => StopExecutionAsync,
            (Key.F5, KeyModifiers.Control | KeyModifiers.Shift) => RestartDebuggingAsync,
            (Key.F6, KeyModifiers.None) => _debugSession.PauseAsync,
            (Key.F10, KeyModifiers.None) => () => ExecuteDebugCommandAsync("next"),
            (Key.F11, KeyModifiers.None) => () => ExecuteDebugCommandAsync("stepIn"),
            (Key.F11, KeyModifiers.Shift) => () => ExecuteDebugCommandAsync("stepOut"),
            (Key.B, KeyModifiers.Control | KeyModifiers.Shift) => BuildDebugProjectAsync,
            _ => null
        };
        if (e.Key == Key.F9 && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleActiveBreakpoint();
            e.Handled = true;
        }
        else if (command != null)
        {
            e.Handled = true;
            RunDebugAction(command);
        }
    }
}