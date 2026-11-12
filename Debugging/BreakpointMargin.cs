using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace CAAI;

public sealed class BreakpointMargin : Control, IBackgroundRenderer, IDisposable
{
    private readonly TextEditor _editor;
    private readonly BreakpointStore _store;
    private readonly string _filePath;
    private readonly Dictionary<int, TextAnchor> _anchors = new();
    private int? _executionLine;
    private bool _subscribed;

    public KnownLayer Layer => KnownLayer.Background;

    public BreakpointMargin(TextEditor editor, BreakpointStore store, string filePath)
    {
        _editor = editor;
        _store = store;
        _filePath = Path.GetFullPath(filePath);
        Width = 22;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Toggle breakpoint (F9)");
        Avalonia.Automation.AutomationProperties.SetName(this, "Breakpoint gutter");
        _editor.TextArea.TextView.BackgroundRenderers.Add(this);
        _editor.TextArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        _editor.Document.Changed += OnDocumentChanged;
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        SyncAnchors();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _store.Changed += OnBreakpointsChanged;
        SyncAnchors();
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _store.Changed -= OnBreakpointsChanged;
    }

    private void OnBreakpointsChanged(string filePath, bool userChange)
    {
        if (!BreakpointStore.SameFile(filePath, _filePath)) return;
        if (userChange) SyncAnchors();
        InvalidateVisual();
    }

    private void SyncAnchors()
    {
        _anchors.Clear();
        foreach (var breakpoint in _store.Items.Where(item => BreakpointStore.SameFile(item.FilePath, _filePath)))
        {
            if (breakpoint.Line > _editor.Document.LineCount) continue;
            var anchor = _editor.Document.CreateAnchor(_editor.Document.GetLineByNumber(breakpoint.Line).Offset);
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            _anchors[breakpoint.Line] = anchor;
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        var moved = _anchors.ToDictionary(pair => pair.Key, pair => pair.Value.IsDeleted ? 0 : pair.Value.Line);
        foreach (var item in _store.Items.Where(item => BreakpointStore.SameFile(item.FilePath, _filePath)))
            moved.TryAdd(item.Line, item.Line);
        _store.Relocate(_filePath, moved);
        SyncAnchors();
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var view = _editor.TextArea.TextView;
        if (!view.VisualLinesValid) return;
        var position = e.GetPosition(view);
        var line = view.VisualLines.FirstOrDefault(candidate =>
            position.Y >= candidate.VisualTop - view.ScrollOffset.Y &&
            position.Y < candidate.VisualTop - view.ScrollOffset.Y + candidate.Height);
        if (line == null) return;
        _store.Toggle(_filePath, line.FirstDocumentLine.LineNumber);
        SyncAnchors();
        InvalidateVisual();
        e.Handled = true;
    }

    public void ToggleAtCaret()
    {
        _store.Toggle(_filePath, _editor.TextArea.Caret.Line);
        SyncAnchors();
        InvalidateVisual();
    }

    public void SetExecutionLine(int? line)
    {
        _executionLine = line;
        InvalidateVisual();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var view = _editor.TextArea.TextView;
        if (!view.VisualLinesValid) return;
        var breakpoints = _store.Items.Where(item => BreakpointStore.SameFile(item.FilePath, _filePath)).ToArray();
        foreach (var line in view.VisualLines)
        {
            var center = new Point(10, line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.ScrollOffset.Y);
            var breakpoint = breakpoints.FirstOrDefault(item => item.Line >= line.FirstDocumentLine.LineNumber && item.Line <= line.LastDocumentLine.LineNumber);
            if (breakpoint != null)
            {
                var brush = breakpoint.Enabled ? Brushes.IndianRed : Brushes.Gray;
                context.DrawEllipse(breakpoint.Enabled && breakpoint.Verified ? brush : null, new Pen(brush, 2), center, 5, 5);
            }
            if (_executionLine >= line.FirstDocumentLine.LineNumber && _executionLine <= line.LastDocumentLine.LineNumber)
            {
                var arrow = new StreamGeometry();
                using (var drawing = arrow.Open())
                {
                    drawing.BeginFigure(new Point(14, center.Y - 5), true);
                    drawing.LineTo(new Point(21, center.Y));
                    drawing.LineTo(new Point(14, center.Y + 5));
                    drawing.EndFigure(true);
                }
                context.DrawGeometry(Brushes.Gold, null, arrow);
            }
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_executionLine is not { } line || line < 1 || line > _editor.Document.LineCount || !textView.VisualLinesValid) return;
        var documentLine = _editor.Document.GetLineByNumber(line);
        foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, documentLine))
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(55, 240, 190, 50)), null,
                new Rect(0, rectangle.Y, textView.Bounds.Width, rectangle.Height));
    }

    public void Dispose()
    {
        Unsubscribe();
        _editor.Document.Changed -= OnDocumentChanged;
        _editor.TextArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(this);
    }
}