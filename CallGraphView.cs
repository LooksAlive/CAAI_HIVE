using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CAAI.CallGraphVisualizer;

public sealed class CallGraphView : Control
{
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
    }

    public IBrush Background { get; set; } = Brushes.Transparent;

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
        _zoom = Math.Clamp(_zoom, 0.1, 4.0);

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
                DrawEdge(ctx, from, to, highlight);
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

    private void DrawEdge(DrawingContext ctx, CallNode from, CallNode to, bool highlight)
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

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.QuadraticBezierTo(ctrl, end);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);

        // Arrowhead at the "to" end
        DrawArrow(ctx, ctrl, end, pen.Brush);
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