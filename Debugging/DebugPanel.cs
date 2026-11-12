using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace CAAI;

public sealed class DebugPanel : UserControl
{
    private readonly DebugSession _session;
    private readonly BreakpointStore _breakpoints;
    private readonly TextBlock _status = new() { Text = "No debug session", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _project = new() { Text = "No project selected", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _threads = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _frames = new();
    private readonly ListBox _breakpointList = new();
    private readonly ObjectInspectorWidget _locals = new();
    private readonly ObjectInspectorWidget _watchValues = new();
    private readonly ObservableCollection<string> _watches = [];
    private readonly TextBox _expression = new() { Watermark = "Expression", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ObservableCollection<SourceBreakpoint> _breakpointItems = [];
    private bool _syncing;
    private int _watchVersion;
    public event Action? SelectProjectRequested;
    public event Action? LaunchSettingsRequested;
    public event Action<string, int>? NavigateRequested;

    public DebugPanel(DebugSession session, BreakpointStore breakpoints)
    {
        _session = session;
        _breakpoints = breakpoints;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(6) };
        header.Children.Add(Tool(Symbol.Folder, "Select debug project", () => SelectProjectRequested?.Invoke()));
        Grid.SetColumn(_project, 1);
        header.Children.Add(_project);
        var settings = Tool(Symbol.Setting, "Open project launch settings", () => LaunchSettingsRequested?.Invoke());
        Grid.SetColumn(settings, 2);
        header.Children.Add(settings);
        Grid.SetColumn(_status, 3);
        header.Children.Add(_status);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Call Stack & Variables", Content = BuildStack() });
        tabs.Items.Add(new TabItem { Header = "Watches", Content = BuildWatches() });
        tabs.Items.Add(new TabItem { Header = "Breakpoints", Content = BuildBreakpoints() });
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(header);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        Content = root;
        _session.StateChanged += (state, message) =>
        {
            _status.Text = message;
            ToolTip.SetTip(_status, message);
            _expression.IsEnabled = state == DebugSessionState.Paused;
            if (state != DebugSessionState.Paused)
            {
                Interlocked.Increment(ref _watchVersion);
                _locals.SetNodes([], "Not paused");
                _watchValues.SetNodes([], "Not paused");
            }
        };
        _session.StackChanged += UpdateStack;
        _session.FrameChanged += frame =>
        {
            _syncing = true;
            _frames.SelectedItem = frame;
            _syncing = false;
        };
        _session.VariablesChanged += (nodes, context) =>
        {
            _locals.SetNodes(nodes, context);
            _ = RefreshWatchesAsync();
        };
        _locals.RefreshRequested += () => Run(async () =>
        {
            if (_session.SelectedFrame is { } frame) await _session.SelectFrameAsync(frame);
        });
        _watchValues.RefreshRequested += () => _ = RefreshWatchesAsync();
        _breakpoints.Changed += (_, _) => RefreshBreakpoints();
        RefreshBreakpoints();
        _expression.IsEnabled = false;
    }

    public void SetProject(string path)
    {
        _project.Text = Path.GetFileName(path);
        ToolTip.SetTip(_project, path);
    }

    private Control BuildStack()
    {
        var stack = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        stack.Children.Add(_threads);
        Grid.SetRow(_frames, 1);
        stack.Children.Add(_frames);
        _frames.ItemTemplate = new FuncDataTemplate<DebugStackFrame>((frame, _) => new TextBlock
        {
            Text = frame?.ToString(), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4)
        });
        _threads.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _threads.SelectedItem is DebugThread thread && thread.Id != _session.ThreadId)
                Run(() => _session.SelectThreadAsync(thread.Id));
        };
        _frames.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _frames.SelectedItem is DebugStackFrame frame && frame != _session.SelectedFrame)
                Run(() => _session.SelectFrameAsync(frame));
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,4,*") };
        grid.Children.Add(stack);
        var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        Grid.SetColumn(_locals, 2);
        grid.Children.Add(_locals);
        return grid;
    }

    private Control BuildWatches()
    {
        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(6), ColumnSpacing = 4 };
        input.Children.Add(_expression);
        void AddWatch()
        {
            var expression = _expression.Text?.Trim();
            if (string.IsNullOrWhiteSpace(expression)) return;
            if (!_watches.Contains(expression)) _watches.Add(expression);
            _expression.Text = "";
            _ = RefreshWatchesAsync();
        }
        var add = Tool(Symbol.Add, "Evaluate watch in selected frame; may execute debuggee code", AddWatch);
        Grid.SetColumn(add, 1);
        input.Children.Add(add);
        var remove = Tool(Symbol.Delete, "Remove selected watch expression", () =>
        {
            if (!string.IsNullOrWhiteSpace(_expression.Text)) _watches.Remove(_expression.Text.Trim());
            _expression.Text = "";
            _ = RefreshWatchesAsync();
        });
        Grid.SetColumn(remove, 2);
        input.Children.Add(remove);
        _expression.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddWatch(); e.Handled = true; } };
        var expressions = new ListBox { ItemsSource = _watches, MaxHeight = 90 };
        expressions.SelectionChanged += (_, _) => { if (expressions.SelectedItem is string expression) _expression.Text = expression; };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        root.Children.Add(input);
        Grid.SetRow(expressions, 1);
        root.Children.Add(expressions);
        Grid.SetRow(_watchValues, 2);
        root.Children.Add(_watchValues);
        return root;
    }

    private Control BuildBreakpoints()
    {
        _breakpointList.ItemsSource = _breakpointItems;
        _breakpointList.ItemTemplate = new FuncDataTemplate<SourceBreakpoint>((breakpoint, _) =>
        {
            if (breakpoint == null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(4) };
            var enabled = new CheckBox { IsChecked = breakpoint.Enabled, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(enabled, "Enable breakpoint");
            enabled.IsCheckedChanged += (_, _) => _breakpoints.Update(breakpoint, enabled.IsChecked == true, breakpoint.Condition);
            row.Children.Add(enabled);
            var label = new TextBlock
            {
                Text = $"{Path.GetFileName(breakpoint.FilePath)}:{breakpoint.Line}" +
                    (breakpoint.Condition == null ? "" : $" if {breakpoint.Condition}"),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(label, breakpoint.FilePath + Environment.NewLine + breakpoint.Message);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var verification = new TextBlock
            {
                Text = !breakpoint.Enabled ? "Disabled" : breakpoint.Verified ? "Verified" : "Pending",
                Foreground = breakpoint.Verified ? Brushes.MediumSeaGreen : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(verification, 2);
            row.Children.Add(verification);
            return row;
        });
        _breakpointList.DoubleTapped += (_, _) =>
        {
            if (_breakpointList.SelectedItem is SourceBreakpoint breakpoint)
                NavigateRequested?.Invoke(breakpoint.FilePath, breakpoint.Line);
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6) };
        tools.Children.Add(Tool(Symbol.Edit, "Edit breakpoint condition", EditCondition));
        tools.Children.Add(Tool(Symbol.Delete, "Remove selected breakpoint", () =>
        {
            if (_breakpointList.SelectedItem is SourceBreakpoint breakpoint) _breakpoints.Toggle(breakpoint.FilePath, breakpoint.Line);
        }));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(tools);
        Grid.SetRow(_breakpointList, 1);
        root.Children.Add(_breakpointList);
        return root;
    }

    private async void EditCondition()
    {
        if (_breakpointList.SelectedItem is not SourceBreakpoint breakpoint || TopLevel.GetTopLevel(this) is not Window owner) return;
        var input = new TextBox { Text = breakpoint.Condition, Watermark = "count > 5" };
        var apply = new Button { Content = "Apply", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"{Path.GetFileName(breakpoint.FilePath)}:{breakpoint.Line}" });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        var dialog = new Window { Title = "Breakpoint Condition", Width = 420, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) => { _breakpoints.Update(breakpoint, breakpoint.Enabled, input.Text); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(owner);
    }

    private void UpdateStack()
    {
        _syncing = true;
        _threads.ItemsSource = _session.Threads;
        _threads.SelectedItem = _session.Threads.FirstOrDefault(thread => thread.Id == _session.ThreadId);
        _frames.ItemsSource = _session.Frames;
        _frames.SelectedItem = _session.SelectedFrame;
        _syncing = false;
    }

    private void RefreshBreakpoints()
    {
        var selected = _breakpointList.SelectedItem as SourceBreakpoint;
        _breakpointItems.Clear();
        foreach (var breakpoint in _breakpoints.Items.OrderBy(item => item.FilePath).ThenBy(item => item.Line)) _breakpointItems.Add(breakpoint);
        if (selected != null) _breakpointList.SelectedItem = _breakpointItems.FirstOrDefault(item => item.FilePath == selected.FilePath && item.Line == selected.Line);
    }

    private async Task RefreshWatchesAsync()
    {
        var version = Interlocked.Increment(ref _watchVersion);
        if (_session.State != DebugSessionState.Paused || _session.SelectedFrame == null) return;
        var nodes = new List<ObjectNode>();
        foreach (var expression in _watches.ToArray())
        {
            try { nodes.Add(await _session.EvaluateAsync(expression)); }
            catch (Exception exception) { nodes.Add(new ObjectNode(expression, "Error", exception.Message)); }
        }
        if (version == Volatile.Read(ref _watchVersion) && _session.State == DebugSessionState.Paused)
            _watchValues.SetNodes(nodes, "Watches");
    }

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private static Button Tool(Symbol symbol, string tooltip, Action action)
    {
        var button = new Button { Content = new SymbolIcon { Symbol = symbol, FontSize = 16 }, Width = 30, Height = 30, Padding = new Thickness(4) };
        ToolTip.SetTip(button, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
}