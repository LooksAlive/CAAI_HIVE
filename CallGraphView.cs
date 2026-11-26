using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Layout;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Threading;
using System.Threading.Tasks;

namespace CAAI.CallGraphVisualizer;

public sealed class CallGraphView : Control
{
    public event Action<string>? NodeActivated;

    private CallGraph? _graph;
    private readonly Dictionary<string, CallNode> _nodesById = new();
    private CallNode? _hovered;
    private CallNode? _selected;

    private Point _pan = new(0, 0);
    private double _zoom = 1.0;
    private bool _dragging;
    private Point _dragStart;
    private Point _panStart;

    // Painting resources
    private static readonly IBrush NodeBrush = new SolidColorBrush(Color.Parse("#1e2530"));
    private static readonly IBrush NodeBrushExternal = new SolidColorBrush(Color.Parse("#2a1f1f"));
    private static readonly IBrush NodeBrushSelected = new SolidColorBrush(Color.Parse("#2d4a6b"));
    private static readonly IBrush NodeBorderBrush = new SolidColorBrush(Color.Parse("#3a4555"));
    private static readonly IBrush NodeBorderSelected = new SolidColorBrush(Color.Parse("#5aa9ff"));
    private static readonly IBrush TextBrush = Brushes.White;
    private static readonly IBrush SubTextBrush = new SolidColorBrush(Color.Parse("#9aa5b1"));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#4a5568")), 1.2);
    private static readonly IPen EdgePenHighlight = new Pen(new SolidColorBrush(Color.Parse("#5aa9ff")), 2.2);
    private static readonly IPen NodePen = new Pen(NodeBorderBrush, 1.2);
    private static readonly IPen NodePenSelected = new Pen(NodeBorderSelected, 2.0);
    private static readonly Typeface Font = new("Segoe UI,Inter,Arial");

    public CallGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = new SolidColorBrush(Color.Parse("#14181f"));
        DoubleTapped += (_, args) =>
        {
            var node = HitTest(args.GetPosition(this));
            if (node == null) return;
            NodeActivated?.Invoke(node.Id);
            args.Handled = true;
        };
    }

    public IBrush Background { get; set; } = Brushes.Transparent;

    public void SelectNode(string id)
    {
        _selected = _nodesById.GetValueOrDefault(id);
        InvalidateVisual();
    }

    public void SetGraph(global::CallGraphVisualizer.ControlFlowGraphModel model)
    {
        var graph = new CallGraph();
        foreach (var node in model.Nodes)
            graph.Nodes[node.Id] = new CallNode
            {
                Id = node.Id,
                DisplayName = $"{node.Kind} #{node.Ordinal}",
                ContainingType = node.Label,
                FullName = node.Label,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height
            };
        foreach (var edge in model.Edges)
            graph.Edges.Add(new CallEdge { FromId = edge.FromId, ToId = edge.ToId, Label = edge.Condition });
        SetGraph(graph);
    }

    public void SetGraph(CallGraph graph)
    {
        _graph = graph;
        _nodesById.Clear();
        foreach (var n in graph.Nodes.Values) _nodesById[n.Id] = n;
        _hovered = null;
        _selected = null;
        _pan = new Point(0, 0);
        _zoom = 1.0;
        InvalidateVisual();
    }

    public void FitToContent()
    {
        if (_graph == null || _graph.Nodes.Count == 0) return;
        double minX = _graph.Nodes.Values.Min(n => n.X);
        double minY = _graph.Nodes.Values.Min(n => n.Y);
        double maxX = _graph.Nodes.Values.Max(n => n.X + n.Width);
        double maxY = _graph.Nodes.Values.Max(n => n.Y + n.Height);

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        double margin = 40;
        double contentW = Math.Max(1, maxX - minX + margin * 2);
        double contentH = Math.Max(1, maxY - minY + margin * 2);

        _zoom = Math.Min(b.Width / contentW, b.Height / contentH);
        _zoom = Math.Clamp(_zoom, 0.1, 1.0);

        _pan = new Point(
            (b.Width - (maxX - minX) * _zoom) / 2 - minX * _zoom,
            (b.Height - (maxY - minY) * _zoom) / 2 - minY * _zoom);

        InvalidateVisual();
    }

    // ---------------- Rendering ----------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));

        if (_graph == null) return;

        using (ctx.PushTransform(
            Matrix.CreateScale(_zoom, _zoom) *
            Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            // Edges first
            foreach (var e in _graph.Edges)
            {
                if (!_nodesById.TryGetValue(e.FromId, out var from)) continue;
                if (!_nodesById.TryGetValue(e.ToId, out var to)) continue;
                bool highlight = _selected != null &&
                                 (e.FromId == _selected.Id || e.ToId == _selected.Id);
                DrawEdge(ctx, from, to, highlight, e.Label);
            }

            // Nodes on top
            foreach (var n in _graph.Nodes.Values)
            {
                bool selected = _selected?.Id == n.Id;
                bool hovered = _hovered?.Id == n.Id;
                DrawNode(ctx, n, selected, hovered);
            }
        }
    }

    private void DrawEdge(DrawingContext ctx, CallNode from, CallNode to, bool highlight, string? label)
    {
        var (start, end) = EdgeEndpoints(from, to);
        var pen = highlight ? EdgePenHighlight : EdgePen;

        // Gentle curve for readability
        var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double curve = Math.Min(30, len * 0.15);
        var ctrl = new Point(mid.X - dy / (len + 0.001) * curve,
                             mid.Y + dx / (len + 0.001) * curve);
        if (from.Id == to.Id)
        {
            start = new Point(from.X + from.Width, from.Y + 10);
            end = new Point(from.X + from.Width, from.Y + from.Height - 10);
            ctrl = new Point(from.X + from.Width + 90, from.Y + from.Height / 2);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.QuadraticBezierTo(ctrl, end);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);

        // Arrowhead at the "to" end
        DrawArrow(ctx, ctrl, end, pen.Brush ?? TextBrush);
        if (!string.IsNullOrEmpty(label))
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 11, TextBrush);
            var position = new Point((start.X + 2 * ctrl.X + end.X) / 4,
                (start.Y + 2 * ctrl.Y + end.Y) / 4);
            ctx.FillRectangle(Background, new Rect(position, new Size(text.Width + 6, text.Height + 2)));
            ctx.DrawText(text, position + new Vector(3, 1));
        }
    }

    private static void DrawArrow(DrawingContext ctx, Point from, Point to, IBrush brush)
    {
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double size = 8;
        var p1 = new Point(to.X - size * Math.Cos(angle - 0.4),
                           to.Y - size * Math.Sin(angle - 0.4));
        var p2 = new Point(to.X - size * Math.Cos(angle + 0.4),
                           to.Y - size * Math.Sin(angle + 0.4));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(p1);
            g.LineTo(p2);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    private static (Point start, Point end) EdgeEndpoints(CallNode from, CallNode to)
    {
        var a = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        var b = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);

        // Clip at rectangle borders
        Point ClipToRect(Point center, Point towards, CallNode n)
        {
            double dx = towards.X - center.X, dy = towards.Y - center.Y;
            if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return center;
            double halfW = n.Width / 2, halfH = n.Height / 2;
            double scale = Math.Min(
                halfW / Math.Max(0.001, Math.Abs(dx)),
                halfH / Math.Max(0.001, Math.Abs(dy)));
            return new Point(center.X + dx * scale, center.Y + dy * scale);
        }

        return (ClipToRect(a, b, from), ClipToRect(b, a, to));
    }

    private void DrawNode(DrawingContext ctx, CallNode n, bool selected, bool hovered)
    {
        var rect = new Rect(n.X, n.Y, n.Width, n.Height);
        var fill = selected ? NodeBrushSelected
                 : n.IsExternal ? NodeBrushExternal
                 : NodeBrush;
        var border = selected ? NodeBorderSelected : NodeBorderBrush;
        var pen = selected ? NodePenSelected : NodePen;

        ctx.DrawRectangle(fill, pen, rect, 6, 6);

        if (hovered)
            ctx.DrawRectangle(null, new Pen(NodeBorderSelected, 1.5), rect.Inflate(1), 6, 6);

        var label = FitText(ctx, n.DisplayName, n.Width - 16, 12);
        var sub = FitText(ctx, n.ContainingType, n.Width - 16, 10);

        var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Font, 12, TextBrush);
        var st = new FormattedText(sub, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Font, 10, SubTextBrush);

        ctx.DrawText(ft, new Point(n.X + 8, n.Y + 6));
        ctx.DrawText(st, new Point(n.X + 8, n.Y + 24));

        // Edge count badge
        if (n.CallCount > 0)
        {
            var badge = new FormattedText(n.CallCount.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 9, Brushes.White);
            var badgeRect = new Rect(n.X + n.Width - 24, n.Y + n.Height - 16, 20, 12);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#3b4a5e")), null, badgeRect, 3, 3);
            ctx.DrawText(badge, new Point(badgeRect.X + 4, badgeRect.Y + 1));
        }
    }

    private static string FitText(DrawingContext ctx, string text, double maxWidth, double fontSize)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Font, fontSize, Brushes.White);
        if (ft.Width <= maxWidth) return text;
        for (int i = text.Length - 1; i > 0; i--)
        {
            var candidate = text.Substring(0, i) + "…";
            var c = new FormattedText(candidate, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, fontSize, Brushes.White);
            if (c.Width <= maxWidth) return candidate;
        }
        return "…";
    }

    // ---------------- Interaction ----------------

    private CallNode? HitTest(Point screenPoint)
    {
        if (_graph == null) return null;
        // Convert screen -> world
        double wx = (screenPoint.X - _pan.X) / _zoom;
        double wy = (screenPoint.Y - _pan.Y) / _zoom;
        foreach (var n in _graph.Nodes.Values)
            if (new Rect(n.X, n.Y, n.Width, n.Height).Contains(new Point(wx, wy)))
                return n;
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            var hit = HitTest(pt);
            if (hit != null)
            {
                _selected = _selected == hit ? null : hit;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            _dragging = true;
            _dragStart = pt;
            _panStart = _pan;
            e.Pointer.Capture(this);
        }
        else if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _dragging = true;
            _dragStart = pt;
            _panStart = _pan;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        if (_dragging)
        {
            _pan = new Point(
                _panStart.X + (pt.X - _dragStart.X),
                _panStart.Y + (pt.Y - _dragStart.Y));
            InvalidateVisual();
            return;
        }

        var hit = HitTest(pt);
        if (hit != _hovered)
        {
            _hovered = hit;
            ToolTip.SetTip(this, hit?.FullName);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pt = e.GetPosition(this);
        double oldZoom = _zoom;
        _zoom *= e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _zoom = Math.Clamp(_zoom, 0.1, 4.0);

        // Zoom to cursor
        _pan = new Point(
            pt.X - (pt.X - _pan.X) * (_zoom / oldZoom),
            pt.Y - (pt.Y - _pan.Y) * (_zoom / oldZoom));

        InvalidateVisual();
        e.Handled = true;
    }
}

public sealed class FlowGraphWidget : UserControl, IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly Func<ProjectId> _projectId;
    private readonly Func<(DocumentId? DocumentId, int Offset)> _source;
    private readonly CallGraphView _view = new();
    private readonly ComboBox _methods = new() { MinWidth = 180, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _layout = new() { ItemsSource = Enum.GetValues<LayoutKind>(), SelectedItem = LayoutKind.Layered, Width = 110 };
    private readonly CheckBox _external = new() { Content = "External calls", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _methodSearch = new() { PlaceholderText = "Find method", MinWidth = 120 };
    private readonly ComboBox _scope = new() { ItemsSource = new[] { "Focused method", "Whole project" }, SelectedIndex = 0, Width = 150 };
    private readonly ComboBox _direction = new() { ItemsSource = Enum.GetValues<CallFlowDirection>(), SelectedItem = CallFlowDirection.Callees, Width = 110 };
    private readonly NumericUpDown _depth = new() { Minimum = 0, Maximum = 6, Value = 2, Increment = 1, FormatString = "0", Width = 130 };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private CancellationTokenSource? _request;
    private CallGraph? _callGraph;
    private string? _focusedMethodId;
    private (DocumentId? DocumentId, int Offset)? _lastCallSource;
    private bool _updating;
    private bool _disposed;

    public bool IsControlFlow { get; }

    private sealed record MethodChoice(string Display, SyntaxNode Syntax, SemanticModel Model, DocumentId DocumentId)
    {
        public override string ToString() => Display;
    }

    private sealed record CallMethodChoice(string Id, string Display)
    {
        public override string ToString() => Display;
    }

    public FlowGraphWidget(AdhocWorkspace workspace, Func<ProjectId> projectId,
        Func<(DocumentId? DocumentId, int Offset)> source, bool controlFlow)
    {
        _workspace = workspace;
        _projectId = projectId;
        _source = source;
        IsControlFlow = controlFlow;
        var refresh = new Button { Content = new SymbolIcon { Symbol = Symbol.Refresh }, Width = 34, Height = 32 };
        var fit = new Button { Content = new SymbolIcon { Symbol = Symbol.FullScreen }, Width = 34, Height = 32 };
        ToolTip.SetTip(refresh, "Refresh graph");
        ToolTip.SetTip(fit, "Fit graph to view");
        ToolTip.SetTip(_methods, "Method");
        ToolTip.SetTip(_layout, "Graph layout");
        refresh.Click += async (_, _) => await RefreshAsync();
        fit.Click += (_, _) => _view.FitToContent();
        _methods.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            if (IsControlFlow) ShowControlFlow();
            else if (_methods.SelectedItem is CallMethodChoice method)
            {
                _focusedMethodId = method.Id;
                _scope.SelectedIndex = 0;
                ShowCallFlow();
            }
        };
        _layout.SelectionChanged += (_, _) => ShowCallFlow();
        _external.Click += async (_, _) => await RefreshAsync();
        _methodSearch.TextChanged += (_, _) => FilterCallMethods();
        _scope.SelectionChanged += (_, _) => ShowCallFlow();
        _direction.SelectionChanged += (_, _) => ShowCallFlow();
        _depth.ValueChanged += (_, _) => ShowCallFlow();
        _view.NodeActivated += id =>
        {
            if (IsControlFlow) return;
            _focusedMethodId = id;
            _methodSearch.Text = string.Empty;
            FilterCallMethods();
            _scope.SelectedIndex = 0;
            ShowCallFlow();
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(6), ColumnSpacing = 6 };
        toolbar.Children.Add(refresh);
        Grid.SetColumn(fit, 1);
        toolbar.Children.Add(fit);
        Control options;
        if (controlFlow) options = _methods;
        else
        {
            var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6 };
            var methodRow = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), ColumnSpacing = 6 };
            methodRow.Children.Add(_methodSearch);
            _methods.MaxWidth = double.PositiveInfinity;
            _methods.MinWidth = 120;
            Grid.SetColumn(_methods, 1);
            methodRow.Children.Add(_methods);
            panel.Children.Add(methodRow);
            var settings = new WrapPanel { Orientation = Orientation.Horizontal };
            var depthControl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            depthControl.Children.Add(new TextBlock { Text = "Depth", VerticalAlignment = VerticalAlignment.Center });
            depthControl.Children.Add(_depth);
            foreach (var setting in new Control[] { _scope, _direction, depthControl, _layout })
            {
                setting.Margin = new Thickness(0, 0, 6, 4);
                settings.Children.Add(setting);
            }
            ToolTip.SetTip(_scope, "Graph scope");
            ToolTip.SetTip(_direction, "Follow calls to callees, from callers, or both");
            ToolTip.SetTip(_depth, "Call depth from the focused method");
            _external.Margin = new Thickness(12, 0, 0, 0);
            settings.Children.Add(_external);
            Grid.SetRow(settings, 1);
            panel.Children.Add(settings);
            options = panel;
        }
        Grid.SetColumn(options, 2);
        toolbar.Children.Add(options);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = _view.Background };
        root.Children.Add(toolbar);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Grid.SetRow(_view, 2);
        root.Children.Add(_view);
        Content = root;
        _view.Loaded += (_, _) => _view.FitToContent();
        _view.SizeChanged += (_, _) => FitAfterLayout();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        var solution = _workspace.CurrentSolution;
        var project = solution.GetProject(_projectId());
        var source = _source();
        var previous = (_methods.SelectedItem as MethodChoice)?.Display;
        var includeExternal = _external.IsChecked == true;
        _status.Text = "Loading graph...";
        try
        {
            if (project == null) throw new InvalidOperationException("No project is open.");
            var compilation = await Task.Run(() => project.GetCompilationAsync(token), token);
            if (compilation == null) throw new InvalidOperationException("Project compilation is unavailable.");
            if (!IsControlFlow)
            {
                var graph = await Task.Run(() => new CallGraphExtractor(compilation)
                    .ExtractAsync(includeExternal, ct: token), token);
                token.ThrowIfCancellationRequested();
                _callGraph = graph;
                var document = source.DocumentId == null ? null : solution.GetDocument(source.DocumentId);
                var tree = document == null ? null : await document.GetSyntaxTreeAsync(token);
                if ((_lastCallSource is not { } lastSource || lastSource != source)
                    && tree != null && compilation.ContainsSyntaxTree(tree))
                {
                    var root = await tree.GetRootAsync(token);
                    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                        .Where(node => node.FullSpan.Contains(source.Offset))
                        .OrderBy(node => node.FullSpan.Length).FirstOrDefault();
                    if (method != null && compilation.GetSemanticModel(tree).GetDeclaredSymbol(method, token) is IMethodSymbol symbol)
                    {
                        var id = CallGraph.GetId(symbol);
                        if (graph.Nodes.ContainsKey(id)) _focusedMethodId = id;
                    }
                }
                token.ThrowIfCancellationRequested();
                _lastCallSource = source;
                if (_focusedMethodId == null || !graph.Nodes.ContainsKey(_focusedMethodId))
                    _focusedMethodId = graph.Nodes.Values.Where(node => !node.IsExternal)
                        .OrderBy(node => node.FullName).FirstOrDefault()?.Id;
                FilterCallMethods();
                ShowCallFlow();
                return;
            }

            var choices = await Task.Run(async () =>
            {
                var result = new List<MethodChoice>();
                foreach (var document in project.Documents)
                {
                    var tree = await document.GetSyntaxTreeAsync(token);
                    if (tree == null || !compilation.ContainsSyntaxTree(tree)) continue;
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync(token);
                    foreach (var node in root.DescendantNodes().Where(node =>
                        node is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null }
                        or AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null }))
                    {
                        token.ThrowIfCancellationRequested();
                        var symbol = model.GetDeclaredSymbol(node, token);
                        if (symbol == null) continue;
                        result.Add(new MethodChoice($"{symbol.ToDisplayString()}  ({document.Name}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1})",
                            node, model, document.Id));
                    }
                }
                return result.OrderBy(choice => choice.Display).ToList();
            }, token);
            token.ThrowIfCancellationRequested();
            _updating = true;
            try
            {
                _methods.ItemsSource = choices;
                _methods.SelectedItem = choices.Where(choice => choice.DocumentId == source.DocumentId && choice.Syntax.FullSpan.Contains(source.Offset))
                    .OrderBy(choice => choice.Syntax.FullSpan.Length).FirstOrDefault()
                    ?? choices.FirstOrDefault(choice => choice.Display == previous)
                    ?? choices.FirstOrDefault(choice => choice.DocumentId == source.DocumentId)
                    ?? choices.FirstOrDefault();
            }
            finally { _updating = false; }
            ShowControlFlow();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested && !_disposed)
            {
                _view.SetGraph(new CallGraph());
                _status.Text = $"Graph unavailable: {exception.Message}";
            }
        }
    }

    private void FilterCallMethods()
    {
        if (IsControlFlow || _callGraph == null || _disposed) return;
        var search = _methodSearch.Text?.Trim() ?? string.Empty;
        var choices = _callGraph.Nodes.Values
            .Where(node => node.FullName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.FullName)
            .Select(node => new CallMethodChoice(node.Id, node.FullName)).ToList();
        _updating = true;
        try
        {
            _methods.ItemsSource = choices;
            _methods.SelectedItem = choices.FirstOrDefault(choice => choice.Id == _focusedMethodId);
        }
        finally { _updating = false; }
    }

    private void ShowCallFlow()
    {
        if (_callGraph == null || _disposed) return;
        var focused = _scope.SelectedIndex != 1;
        _direction.IsEnabled = _depth.IsEnabled = focused;
        var graph = _callGraph;
        var truncated = false;
        if (focused)
        {
            (graph, truncated) = _callGraph.Focus(_focusedMethodId ?? string.Empty,
                _direction.SelectedItem is CallFlowDirection direction ? direction : CallFlowDirection.Callees,
                (int)(_depth.Value ?? 2));
        }
        new GraphLayoutEngine { NodeWidth = 240, NodeHeight = 52 }.Layout(graph,
            _layout.SelectedItem is LayoutKind kind ? kind : LayoutKind.Layered);
        _view.SetGraph(graph);
        if (focused && _focusedMethodId != null) _view.SelectNode(_focusedMethodId);
        _status.Text = _callGraph.Nodes.Count == 0 ? "No methods found in this project."
            : $"{graph.Nodes.Count} of {_callGraph.Nodes.Count} methods, {graph.Edges.Count} call edges"
                + (truncated ? " - 80-method limit reached; reduce depth or narrow the flow." : string.Empty);
        FitAfterLayout();
    }

    private void ShowControlFlow()
    {
        if (_disposed) return;
        _view.SetGraph(new CallGraph());
        if (_methods.SelectedItem is not MethodChoice method)
        {
            _status.Text = "No executable methods found in this project.";
            return;
        }
        try
        {
            var graph = global::CallGraphVisualizer.ControlFlowGraphModel.FromSyntax(method.Syntax, method.Model, method.Display);
            graph.ApplyGridLayout(2);
            _view.SetGraph(graph);
            _status.Text = $"{graph.Nodes.Count} blocks, {graph.Edges.Count} branches";
            FitAfterLayout();
        }
        catch (Exception exception) { _status.Text = $"Control flow unavailable: {exception.Message}"; }
    }

    private void FitAfterLayout() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) _view.FitToContent();
    }, DispatcherPriority.Loaded);

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }
}